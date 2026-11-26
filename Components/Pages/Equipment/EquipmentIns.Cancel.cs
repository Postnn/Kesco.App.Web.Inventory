using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentIns
{
    private bool PreservesCreationOutcome =>
        _creationUncertain || _creationAttempt is not null || _notSentCreationId is not null || _creationResult is not null;

    private bool HasUnacceptedCreationInput =>
        IsModified || _draftNeedsReview || _creationRawPending || _restoreCreationRaw || _editorSaveFailed;

    private bool ConfirmExternalExit => _saving || _creationRecoveryBusy || HasUnacceptedCreationInput || PreservesCreationOutcome;

    private async Task CancelAsync()
    {
        if (!_disposed && await PrepareEditorExitAsync(_pageCts.Token))
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
        if (!await PrepareEditorExitAsync(request.Token))
            context.PreventNavigation();
    }

    private async Task<bool> PrepareEditorExitAsync(CancellationToken token)
    {
        if (_disposed || _loading || _saving || _applyingCreationRaw || _creationRecoveryBusy || token.IsCancellationRequested)
            return false;

        if (_creationResult is not null && !_receiptPersisted)
        {
            _validationMessage = "Сначала завершите обработку черновика, чтобы сохранить номер созданного оборудования.";
            return false;
        }

        // The durable attempt and its accepted input remain available after leaving the editor.
        if (PreservesCreationOutcome)
            return true;

        if (_creationDraft is not { } registration)
        {
            if (!HasUnacceptedCreationInput) return true;
            _validationMessage = "Хранилище черновика недоступно. Ввод остался на форме.";
            return false;
        }

        _saving = true;
        var version = _operationVersion;
        var source = SourceKey;
        var live = Snapshot();
        bool Current() => !token.IsCancellationRequested && IsCurrent(version) &&
            ReferenceEquals(_creationDraft, registration) && SourceKey == source;
        try
        {
            if (_creationAutosave is { } pending)
                await pending;
            if (!Current()) return false;

            var scope = await registration.ReadEditorScopeAsync(CreationScope, token);
            if (!Current()) return false;
            if (scope.HasOperation) return true;
            if (!HasUnacceptedCreationInput && !scope.HasRaw) return true;

            var copyId = QueryInt("copyId");
            var initialType = QueryInt("codetype");
            var parameters = new DialogParameters<ClayEditorCancelDialog>();
            if (copyId <= 0)
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

            CreationReset? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                fresh = await ReadCreationResetAsync(copyId, initialType, token);
                if (!Current()) return false;
                if (fresh is null)
                {
                    _validationMessage = "Не удалось подтвердить доступ или прочитать исходное оборудование. Черновик сохранён.";
                    return false;
                }
            }

            var original = fresh is null ? null : Snapshot(fresh.Model, fresh.Date, TimeSpan.Zero);
            var replacement = fresh is null ? null :
                System.Text.Json.JsonSerializer.Serialize(new CreationEditorDraft(source, original!, fresh.Model, fresh.Date, TimeSpan.Zero));
            if (live != Snapshot() || !await registration.ReplaceEditorScopeAsync(CreationScope, scope.Revision, replacement, token))
            {
                if (Current()) _validationMessage = "Ввод изменился. Подтвердите отмену ещё раз.";
                return false;
            }

            if (!Current()) return false;
            InvalidateCreationSelections();
            if (fresh is not null)
            {
                ApplyCreationReset(fresh, original!, replacement!, copyId > 0);
                return false;
            }

            _canEdit = false;
            UpdateOptions();
            _validationMessage = "Ввод отменён. Если окно осталось открытым, закройте его или обновите страницу.";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is JSException or InvalidOperationException or SqlException or TimeoutException)
        {
            if (Current()) _validationMessage = "Не удалось завершить отмену. Черновик сохранён.";
            return false;
        }
        finally
        {
            _saving = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<CreationReset?> ReadCreationResetAsync(int copyId, int initialType, CancellationToken token)
    {
        var actor = await Service.GetCurrentUserAsync(await PrincipalAsync(), token);
        if (actor is null || actor.Id != CurrentUser?.Id ||
            !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            return null;

        var model = copyId > 0 ? await Service.GetForCopyAsync(copyId) : new EquipmentEntity();
        token.ThrowIfCancellationRequested();
        if (model is null) return null;

        model.Id = 0;
        if (model.EquipmentTypeId is null && initialType > 0)
            model.EquipmentTypeId = initialType;

        var selectedType = model.EquipmentTypeId is > 0
            ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, token) : null;
        if (model.EquipmentTypeId is > 0 && selectedType is null) return null;

        var selectedModel = model.ModelId is > 0 ? await ModelSource.GetByIdAsync(model.ModelId, token) : null;
        if (model.ModelId is > 0 && (selectedModel is null || selectedModel.EquipmentTypeId != model.EquipmentTypeId))
            return null;

        var accountingAvailable = await Service.CanUseAccountingAsync(actor, model.OwnerId, token);
        var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", token);
        return new CreationReset(model, actor, selectedType, selectedModel, accountingAvailable,
            DateTime.UtcNow.AddMinutes(offset).Date);
    }

    private void InvalidateCreationSelections()
    {
        _operationVersion++;
        _ownerVersion++;
        _shipperVersion++;
        _selectionVersion++;
        _locationChoiceVersion++;
    }

    private void ApplyCreationReset(CreationReset fresh, string original, string replacement, bool fromCopy)
    {
        _model = fresh.Model;
        _receiptDate = fresh.Date;
        _receiptTime = TimeSpan.Zero;
        _freshOriginal = _originalSnapshot = original;
        _lastEditorDraft = replacement;
        _loadedFromCopy = fromCopy;
        _sourceUnavailable = _draftNeedsReview = _editorSaveFailed = false;
        _editorRestored = _creationRawPending = _restoreCreationRaw = _creationRawFailed = false;
        _confirmedOwnerId = fresh.Model.OwnerId;
        CurrentUser = fresh.Actor;
        _canEdit = true;
        _accountingAvailable = fresh.AccountingAvailable;
        ModelSource.Filter.EquipmentTypeId = fresh.Model.EquipmentTypeId;
        ReceiptDocumentSource.PersonId = fresh.Model.OwnerId;
        _selectedType = fresh.Type;
        _selectedModel = fresh.SelectedModel;
        _validationMessage = null;
        UpdateOptions();
    }

    private sealed record CreationReset(EquipmentEntity Model, Employee Actor, EquipmentType? Type,
        EquipmentModel? SelectedModel, bool AccountingAvailable, DateTime Date);
}
