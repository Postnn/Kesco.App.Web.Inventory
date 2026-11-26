using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.DBSelect.Inventory;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Duty;

public partial class DutyEditor
{
    private static readonly ClayToolbarButtonOptions CancelButton = new()
    {
        Text = "Отмена", Icon = Icons.Material.Filled.Close,
    };
    private bool _inputClosed;
    private bool _keepPlannedEditorDuringExit;
    private int _inputViewRevision;

    private bool ConfirmDutyExternalExit => _busy || DutyExitHasInput || _confirmedRevision is not null ||
        _notSentAttemptId is not null;

    private bool DutyExitHasInput => Dirty || _restoreRaw || _retryRaw || _rawRecoveryPending;

    private async Task CancelAsync()
    {
        if (_disposed || !await PrepareDutyExitAsync(_lifetime.Token))
            return;

        _leaveApproved = true;
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token); }
        catch (JSException)
        {
            _leaveApproved = false;
            _message = "Карточка осталась открыта. Закройте её штатным способом.";
        }
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_disposed) return;
        if (_leaveApproved)
        {
            _leaveApproved = false;
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.Token);
        if (!await PrepareDutyExitAsync(request.Token))
            context.PreventNavigation();
    }

    private async Task<bool> PrepareDutyExitAsync(CancellationToken token)
    {
        if (_disposed || _busy || _applyingRaw || token.IsCancellationRequested)
            return false;
        if (_loading) return _registration is null && !Dirty;
        if (_confirmedRevision is not null)
        {
            _message = "Сначала завершите обработку подтверждённого сохранения. Журнал сохранён.";
            return false;
        }
        if (_unknown || _notSentAttemptId is not null || _completed || _inputClosed || !HasWriteAccess)
            return true;
        if (_registration is not { } registration)
            return !DutyExitHasInput;
        if (CurrentUser is not { } owner)
            return false;

        var route = RouteId;
        var date = _routeDate;
        var revision = _revision;
        var live = _draft;
        bool Current() => !_disposed && !token.IsCancellationRequested && ReferenceEquals(registration, _registration) &&
            string.Equals(RouteId, route, StringComparison.Ordinal) && CurrentUser?.Id == owner.Id &&
            revision == _revision && live == _draft;

        _keepPlannedEditorDuringExit = CanEdit;
        _busy = true;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(Scope, token);
            if (!Current()) return false;
            if (scope.HasOperation) return true;
            if (!DutyExitHasInput && !scope.HasRaw) return true;

            var parameters = new DialogParameters<ClayEditorCancelDialog>();
            if (date is null)
                parameters.Add(dialog => dialog.ResetText, "Отменить мои изменения и начать заново");
            var dialog = await Dialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений", parameters,
                new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseButton = true, CloseOnEscapeKey = true });
            DialogResult? result;
            try { result = await dialog.Result.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                dialog.Close(DialogResult.Cancel());
                return false;
            }
            if (!Current() || result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                choice == ClayEditorCancelChoice.Continue)
                return false;

            DutyInputReset? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                fresh = await ReadDutyInputResetAsync(date, owner.Id, token);
                if (!Current()) return false;
                if (fresh is null)
                {
                    _message = "Не удалось прочитать доступное для изменения дежурство. Черновик сохранён.";
                    return false;
                }
            }

            var replacement = fresh is null ? null : JsonSerializer.Serialize(new DutySnapshot(fresh.Row, fresh.Draft));
            if (!await registration.ReplaceEditorScopeAsync(Scope, scope.Revision, replacement, token))
            {
                if (Current()) _message = "Ввод изменился. Подтвердите отмену ещё раз.";
                return false;
            }
            if (!Current()) return false;

            _revision++;
            _inputViewRevision++;
            _conflict = _retryRaw = _restoreRaw = _rawRestoreFailed = _storageError = false;
            _recoverySnapshot = null;
            _stalePlanDay = null;
            _stalePlanReview = null;
            _focusTarget = FocusTarget.None;
            if (fresh is not null)
            {
                ApplyDutyInputReset(fresh);
                return false;
            }

            _inputClosed = true;
            _draft = _baseline;
            _message = "Ввод отменён. Если окно осталось открытым, закройте его или обновите страницу.";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is JSException or SqlException or InvalidOperationException or TimeoutException)
        {
            if (Current()) _message = "Не удалось завершить отмену. Черновик остался на форме.";
            return false;
        }
        finally
        {
            _busy = false;
            _keepPlannedEditorDuringExit = false;
            UpdateOptions();
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<DutyInputReset?> ReadDutyInputResetAsync(DateOnly? date, int employeeId, CancellationToken token)
    {
        var actor = await Employee.GetCurrentAsync(Db, await PrincipalAsync(), token);
        if (actor?.Id != employeeId) return null;
        var context = await DutyData.ReadContextAsync(Db, token);
        if (date is { } existing)
        {
            if (!context.CanUpdate) return null;
            var row = await DutyData.ReadAsync(Db, existing, token);
            if (row is null || row.Closed != 0 || DateOnly.FromDateTime(row.DutyDate) != existing)
                return null;
            var draft = DutyDraft.FromRow(row);
            var workDay = draft.EffectiveEngineer(existing < context.BusinessDay) is { } engineer
                ? await DutyData.IsWorkDayAsync(Db, existing, engineer, token) : (bool?)null;
            return new DutyInputReset(context, row, draft, workDay, existing);
        }

        if (!context.CanInsert) return null;
        var next = await DutyData.NextDayAsync(Db, context.BusinessDay, token);
        return new DutyInputReset(context, null, new DutyDraft(next, null), null, next);
    }

    private void ApplyDutyInputReset(DutyInputReset fresh)
    {
        _context = fresh.Context;
        _original = fresh.Row;
        _draft = _baseline = fresh.Draft;
        _workDay = fresh.WorkDay;
        _calendarPending = _calendarFailed = false;
        _engineers = new DutyEngineerDataSource(Db, fresh.CalendarDay);
        _engineerMonth = new DateOnly(fresh.CalendarDay.Year, fresh.CalendarDay.Month, 1);
        _message = null;
    }

    private sealed record DutyInputReset(DutyContext Context, DutyRow? Row, DutyDraft Draft, bool? WorkDay, DateOnly CalendarDay);
}
