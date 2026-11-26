using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private bool _cancellingInput;

    private bool ConfirmNetExternalExit => IsModified || _saving || _poeRunning ||
        _operationContext is { } context &&
        (context.Attempt is not null || context.NotSentId is not null || HasUnacceptedNetInput(context));

    private bool HasUnacceptedNetInput(NetOperationContext context) =>
        IsModified || !context.InputReady || context.InputConflict || context.InputFailed || RequiresReconciliation;

    private async Task CancelAsync()
    {
        if (!_disposed && await PrepareNetExitAsync(_pageCts.Token))
            await CloseAsync();
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_disposed) return;
        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _pageCts.Token);
        if (!await PrepareNetExitAsync(request.Token))
            context.PreventNavigation();
    }

    private async Task<bool> PrepareNetExitAsync(CancellationToken token)
    {
        if (_disposed || _loading || _saving || _poeRunning || _inspectingOperation || token.IsCancellationRequested)
            return false;

        if (_readOnlyRecovery is not null) return true;
        if (_operationContext is not { } context || !IsCurrentInput(context))
            return !IsModified;
        if (context.Attempt?.Status == ClayDraftOperationStatus.Confirmed && !context.ReceiptRecorded)
        {
            _operationNotice = "Сначала завершите обработку подтверждённой операции. Журнал сохранён.";
            return false;
        }
        if (context.Attempt is not null || context.NotSentId is not null)
            return true;

        var registration = context.Registration;
        var id = context.PortId;
        var version = _loadVersion;
        var model = _model!;
        var live = Snapshot(model);
        bool Current() => !token.IsCancellationRequested && IsCurrentSave(model, version) && IsCurrentInput(context);
        bool SameInput() => Snapshot(model) == live;

        _saving = _cancellingInput = true;
        try
        {
            ClayDraftEditorScopeState scope;
            await context.InputGate.WaitAsync(token);
            try { scope = await registration.ReadEditorScopeAsync(NetInputScope, token); }
            finally { context.InputGate.Release(); }
            if (!Current()) return false;
            if (scope.HasOperation) return true;
            if (!HasUnacceptedNetInput(context) && !scope.HasRaw) return true;

            var dialog = await DialogService.ShowAsync<ClayEditorCancelDialog>("Отмена изменений",
                new DialogParameters(), new DialogOptions
                { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small });
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

            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                var accepted = false;
                await LoadDataAsync(id, version, null, null, acceptReset: async (port, actor, fresh) =>
                {
                    if (!Current() || !SameInput() || actor.Id != context.EmployeeId || port.EquipmentId != model.EquipmentId)
                        return false;
                    var input = new NetEditorInput(id, port.ChangedAtUtc, fresh, fresh);
                    if (!await registration.ReplaceEditorScopeAsync(NetInputScope, scope.Revision, JsonSerializer.Serialize(input), token) ||
                        !Current())
                        return false;
                    context.InputBaseline = fresh;
                    context.InputVersion = port.ChangedAtUtc;
                    context.LastSavedInput = input;
                    context.LastInputRevision = 0;
                    context.InputReady = context.InputRestoreStarted = true;
                    context.InputConflict = context.InputFailed = false;
                    context.InputReview = null;
                    accepted = true;
                    return true;
                }, cancellationToken: token);
                if (!IsCurrent(version, id) || token.IsCancellationRequested)
                    return false;
                if (accepted)
                    InvalidateNetInputView();
                else
                    _validationMessage = "Не удалось принять актуальный порт. Черновик остался на форме.";
                return false;
            }

            if (!SameInput() || !await registration.ReplaceEditorScopeAsync(NetInputScope, scope.Revision, null, token))
            {
                if (Current()) _validationMessage = "Ввод изменился. Подтвердите отмену ещё раз.";
                return false;
            }
            if (!Current()) return false;
            InvalidateNetInputView();
            CurrentUser = null;
            _validationMessage = "Локальный ввод отменён. Если окно осталось открытым, закройте его или обновите страницу.";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) when (IsJournalError(error) || error is SqlException or TimeoutException)
        {
            if (Current()) _validationMessage = "Не удалось завершить отмену. Черновик сохранён.";
            return false;
        }
        finally
        {
            _saving = _cancellingInput = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private void InvalidateNetInputView()
    {
        _loadVersion++;
        _employeeVersion++;
        _socketVersion++;
        _inputViewRevision++;
    }
}
