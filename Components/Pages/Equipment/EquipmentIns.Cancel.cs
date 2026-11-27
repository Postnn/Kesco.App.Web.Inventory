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
    private CreationCloseRequest? _pendingCreationClose;
    private bool _creationDraftClosed;

    private bool PreservesCreationOutcome => _creationUncertain || _creationResult is not null;

    private bool HasUnacceptedCreationInput =>
        IsModified || _draftNeedsReview || _creationRawPending || _restoreCreationRaw || _editorSaveFailed || _retainedCreationInput;

    private bool ConfirmExternalExit => !_creationDraftClosed &&
        (_saving || HasUnacceptedCreationInput || PreservesCreationOutcome || _pendingCreationClose is not null);

    private Task CancelAsync() => PrepareEditorExitAsync(CloseAsync, _pageCts.Token);

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_disposed) return;

        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }

        if (_creationDraftClosed && _creationDraft is null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _pageCts.Token);
        var approved = false;
        var awaitingDecision = true;
        await PrepareEditorExitAsync(() =>
        {
            if (awaitingDecision)
                approved = true;
            else
            {
                _allowNavigation = true;
                Navigation.NavigateTo(context.TargetLocation);
            }
            return Task.CompletedTask;
        }, request.Token);
        awaitingDecision = false;
        if (!approved) context.PreventNavigation();
    }

    private async Task PrepareEditorExitAsync(Func<Task> exit, CancellationToken token)
    {
        if (_disposed || _loading || _saving || _applyingCreationRaw || _pendingCreationClose is not null || token.IsCancellationRequested)
            return;

        if (_creationDraft is not { } registration)
        {
            if (_creationDraftClosed || !HasUnacceptedCreationInput) await exit();
            else _validationMessage = "Хранилище черновика недоступно. Ввод остался на форме.";
            return;
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
            if (!Current()) return;

            var scope = await registration.ReadEditorScopeAsync(CreationScope, token);
            if (!Current()) return;
            if (!HasUnacceptedCreationInput && !scope.HasRaw)
            {
                await CloseCreationDraftAsync(registration, scope.Revision, exit, token);
                return;
            }

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
                return;
            }
            if (!Current() || result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                choice == ClayEditorCancelChoice.Continue)
                return;

            CreationReset? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                if (PreservesCreationOutcome)
                {
                    _validationMessage = "Сначала проверьте исход предыдущего создания.";
                    return;
                }
                fresh = await ReadCreationResetAsync(copyId, initialType, token);
                if (!Current()) return;
                if (fresh is null)
                {
                    _validationMessage = "Не удалось подтвердить доступ или прочитать исходное оборудование. Черновик сохранён.";
                    return;
                }
            }

            if (live != Snapshot())
            {
                if (Current()) _validationMessage = "Ввод изменился. Подтвердите отмену ещё раз.";
                return;
            }
            if (fresh is null)
            {
                await CloseCreationDraftAsync(registration, scope.Revision, exit, token);
                return;
            }

            var original = Snapshot(fresh.Model, fresh.Date, TimeSpan.Zero);
            var replacement = System.Text.Json.JsonSerializer.Serialize(
                new CreationEditorDraft(source, original, fresh.Model, fresh.Date, TimeSpan.Zero));
            if (!await registration.ReplaceEditorScopeAsync(CreationScope, scope.Revision, replacement, token) || !Current())
            {
                if (Current()) _validationMessage = "Ввод изменился. Подтвердите отмену ещё раз.";
                return;
            }
            InvalidateCreationSelections();
            ApplyCreationReset(fresh, original, replacement, copyId > 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or SqlException or TimeoutException)
        {
            if (Current()) _validationMessage = "Не удалось завершить отмену. Черновик сохранён.";
        }
        finally
        {
            _saving = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private Task CloseCreationDraftAsync(ClayDraftRegistration registration, long revision, Func<Task> exit, CancellationToken ct)
    {
        if (_pendingCreationClose is not null)
            throw new InvalidOperationException("Сначала подтвердите прежнее удаление черновика.");
        var pending = new CreationCloseRequest(registration, revision, _operationVersion, SourceKey, exit);
        _pendingCreationClose = pending;
        return RetryCreationCloseAsync(pending, ct);
    }

    private async Task RetryCreationCloseAsync(CreationCloseRequest pending, CancellationToken ct)
    {
        bool Current() => IsCurrentCreationInput(pending.Registration, pending.Version) && SourceKey == pending.Source &&
            ReferenceEquals(_pendingCreationClose, pending);
        if (!Current()) return;
        try
        {
            if (!await pending.Registration.CloseRamIfRevisionAsync(pending.Revision, ct))
            {
                if (Current())
                {
                    _pendingCreationClose = null;
                    _validationMessage = "Ввод изменился перед закрытием. Подтвердите закрытие ещё раз.";
                }
                return;
            }
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            if (Current()) _validationMessage = "Удаление черновика не подтверждено. Нажмите «Обновить», чтобы повторить закрытие.";
            return;
        }
        if (!Current()) return;
        _pendingCreationClose = null;
        _creationDraft = null;
        _creationDraftClosed = true;
        _creationInstance = Guid.NewGuid();
        _canEdit = false;
        _saving = false;
        InvalidateCreationSelections();
        UpdateOptions();
        try { await pending.Exit(); }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!_disposed) _validationMessage = "Черновик удалён. Не удалось завершить переход или закрыть окно.";
        }
    }

    private sealed record CreationCloseRequest(ClayDraftRegistration Registration, long Revision, int Version, string Source, Func<Task> Exit);

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
