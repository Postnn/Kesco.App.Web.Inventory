using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private int _editorRenderVersion;

    private bool ConfirmModelExternalExit => _saving || IsModified || InputBlocked ||
        _writeAttempt is not null || _knownWrite is not null || _notSentAttempt is not null;

    private async Task CancelAsync()
    {
        if (!_disposed && await PrepareModelExitAsync(_pageCts.Token))
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
        if (!await PrepareModelExitAsync(request.Token))
            context.PreventNavigation();
    }

    private async Task<bool> PrepareModelExitAsync(CancellationToken token)
    {
        if (_disposed || _loading || _saving || _journalBusy || token.IsCancellationRequested)
            return false;

        if (_knownWrite is not null && _writeAttempt is not null)
        {
            _journalNotice = "Сначала завершите обработку подтверждённого сохранения. Журнал сохранён.";
            return false;
        }
        // Recovery views and unresolved attempts remain in their durable scope.
        if (_localRecovery is not null || _writeAttempt is not null || _knownWrite is not null || _notSentAttempt is not null)
            return true;

        if (_journal is not { } registration)
            return !IsModified && !InputBlocked;
        if (!_canEdit) return true;

        var id = Id;
        var initialType = id > 0 ? 0 : InitialTypeId;
        var version = _loadVersion;
        var live = _model is { } model ? ModelEditorFields.From(model) : null;
        bool Current() => !token.IsCancellationRequested && IsEditorContextCurrent(registration, version) &&
            Id == id && (id > 0 || InitialTypeId == initialType);
        bool SameInput() => _model is { } current && ModelEditorFields.From(current) == live;

        _journalBusy = true;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(EditorScope, token);
            if (!Current()) return false;
            if (scope.HasOperation) return true;
            if (!IsModified && !InputBlocked && !scope.HasRaw) return true;

            var parameters = new DialogParameters<ClayEditorCancelDialog>();
            if (id <= 0)
                parameters.Add(dialog => dialog.ResetText, "Отменить мои изменения и начать заново");
            var dialog = await DialogService.ShowAsync<ClayEditorCancelDialog>("Отмена изменений", parameters,
                new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small });
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
                await LoadDataAsync(id, initialType, version, acceptReset: async (fresh, employee) =>
                {
                    if (!Current() || !SameInput() || employee?.Id != _journalEmployeeId ||
                        employee.HasRole(Роли.Специалист_по_инвентаризации_оборудования) != true)
                        return false;
                    var original = GetEditorOriginal(fresh);
                    var replacement = JsonSerializer.Serialize(new ModelEditorDraft(original, original.Fields));
                    if (!await registration.ReplaceEditorScopeAsync(EditorScope, scope.Revision, replacement, token) || !Current())
                        return false;
                    ResetEditorInput();
                    accepted = true;
                    return true;
                }, cancellationToken: token);
                if (!Current()) return false;
                if (accepted)
                {
                    _editorRenderVersion++;
                    _loadVersion++;
                }
                else
                    _inputNotice = "Не удалось принять актуальные данные. Черновик остался на форме.";
                return false;
            }

            if (!SameInput() || !await registration.ReplaceEditorScopeAsync(EditorScope, scope.Revision, null, token))
            {
                if (Current()) _inputNotice = "Ввод изменился. Подтвердите отмену ещё раз.";
                return false;
            }
            if (!Current()) return false;

            ResetEditorInput();
            _editorRenderVersion++;
            _loadVersion++;
            _canEdit = false;
            UpdateReadOnlyState();
            _inputNotice = "Ввод отменён. Если окно осталось открытым, закройте его или обновите страницу.";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) when (IsJournalError(error) || error is SqlException or TimeoutException)
        {
            if (Current()) _inputNotice = "Не удалось завершить отмену. Черновик сохранён.";
            return false;
        }
        finally
        {
            _journalBusy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }
}
