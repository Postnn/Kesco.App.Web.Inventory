using Clayzor.Lib.Entities.Inventory;
using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private bool _mainTransition;
    private bool _mainDraftCancelled;
    private int _mainFormRevision;

    private bool PreserveMainOutcome => _orphanAdoptionConflicted || _draftOperationUnresolved || HasPendingPhoneSync || _activeEditor is not null;
    private bool ConfirmExternalExit => !_mainDraftCancelled && !_allowNavigation &&
        (IsModified || PreserveMainOutcome || _mainRawRestorePending);

    private sealed record MainEditorDecision(
        ClayDraftRegistration Registration, ClayDraftEditorScopeState Scope, EquipmentEntity Model,
        int Version, long EditorGeneration, int EmployeeId, string Snapshot, CancellationToken Token);

    private bool IsCurrent(MainEditorDecision decision) =>
        !decision.Token.IsCancellationRequested && IsCurrent(decision.Version, decision.Model.Id) &&
        ReferenceEquals(_draftRegistration, decision.Registration) && ReferenceEquals(_model, decision.Model) &&
        CurrentUser?.Id == decision.EmployeeId && _editorGeneration == decision.EditorGeneration;

    private Task CancelAsync() => RunMainTransitionAsync(false, CloseAsync, _pageCts.Token);

    private async Task RefreshAsync()
    {
        if (_mainRawRestorePending && CanRetryMainRaw && _canEdit)
        {
            await RetryMainDraftRawAsync();
            return;
        }

        if (_checkingEquipmentRecovery || _recoveringEquipmentDraft)
            return;
        if (_recoveryPristine && !HasUnsavedDraft && !PreserveMainOutcome &&
            !_loading && !_saving && !_mainTransition && !_draftRestoring &&
            _draftRegistration is { } registration && _model is { } model && CurrentUser is { } actor)
        {
            var version = _operationVersion;
            await ScanEquipmentRecoveryAsync(registration, version, model.Id, actor.Id);
            if (!IsEquipmentRecoveryCurrent(registration, version, model.Id, actor.Id) ||
                await TryRestoreSingleEquipmentDraftAsync() || EquipmentRecoveryWriteBlocked)
                return;
        }

        await RunMainTransitionAsync(true, CloseAsync, _pageCts.Token);
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _pageCts.Token);
        var accepted = false;
        await RunMainTransitionAsync(false, () =>
        {
            accepted = true;
            return Task.CompletedTask;
        }, request.Token);
        if (!accepted)
            context.PreventNavigation();
    }

    private async Task RunMainTransitionAsync(bool refresh, Func<Task> exit, CancellationToken token)
    {
        if (_disposed || _saving || _mainTransition || _loading || _draftRestoring || _restoringMainRaw ||
            _recoveringEquipmentDraft || token.IsCancellationRequested)
            return;

        if (_model is not { } model)
        {
            if (!refresh)
                await exit();
            return;
        }

        if (_draftRegistration is not { } registration || CurrentUser is null)
        {
            if (!refresh && _draftStorageError is null && !HasUnsavedDraft && !PreserveMainOutcome)
                await exit();
            else
                _validationMessage = "Не удалось проверить черновик. Ввод остался на форме.";
            return;
        }

        _mainTransition = true;
        var version = _operationVersion;
        var generation = _editorGeneration;
        var employeeId = CurrentUser.Id;
        StateHasChanged();
        try
        {
            var scope = await registration.ReadEditorScopeAsync("main", token) ??
                throw new InvalidOperationException("Не удалось прочитать область черновика.");
            var decision = new MainEditorDecision(registration, scope, model, version, generation, employeeId, Snapshot(), token);
            if (!IsCurrent(decision))
                return;

            var protectedOutcome = PreserveMainOutcome || scope.HasOperation;
            var dirty = HasUnsavedDraft || scope.HasRaw || _mainRawRestorePending || _mainConflictMessage is not null;
            if (refresh && protectedOutcome)
            {
                if (_orphanAdoptionConflicted || _draftOperationUnresolved || scope.HasOperation || _activeEditor is not null || dirty)
                {
                    await ReconcileDraftOperationAsync();
                    if (IsCurrent(decision))
                        _warningMessage ??= "Обновление сохранило локальный ввод и состояние предыдущей команды.";
                    return;
                }

                if (await ConfirmAsync("Обновление", "Обновить запись? Ожидающая синхронизация и возможность повтора через «Применить» сохранятся.") &&
                    IsCurrent(decision))
                    await LoadCurrentAsync(Id);
                return;
            }

            if (!dirty && !protectedOutcome)
            {
                if (refresh)
                    await LoadCurrentAsync(Id);
                else
                    await exit();
                return;
            }

            var choice = await AskMainEditorChoiceAsync(token);
            if (!IsCurrent(decision) || choice == ClayEditorCancelChoice.Continue)
                return;

            if (protectedOutcome || PreserveMainOutcome)
            {
                if (choice == ClayEditorCancelChoice.CloseWithoutSaving)
                    await exit();
                else
                    _validationMessage = "Сначала завершите проверку предыдущей команды или работу дочернего редактора. Черновик сохранён.";
                return;
            }

            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                await LoadEquipmentAsync(Id, decision);
                return;
            }

            if (await ReplaceMainDraftAsync(decision, null))
            {
                _mainDraftCancelled = true;
                await exit();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrent(version, model.Id))
                _validationMessage = "Не удалось изменить черновик. Ввод остался на форме.";
        }
        finally
        {
            _mainTransition = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task<ClayEditorCancelChoice> AskMainEditorChoiceAsync(CancellationToken token)
    {
        var dialog = await DialogService.ShowAsync<ClayEditorCancelDialog>("Отмена изменений", new DialogParameters(),
            new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small });
        try
        {
            return await dialog.Result.WaitAsync(token) is { Canceled: false, Data: ClayEditorCancelChoice choice }
                ? choice : ClayEditorCancelChoice.Continue;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            dialog.Close(DialogResult.Cancel());
            throw;
        }
    }

    private async Task<bool> ReplaceMainDraftAsync(MainEditorDecision decision, string? replacement)
    {
        if (IsCurrent(decision) && !PreserveMainOutcome && decision.Snapshot == Snapshot())
        {
            var replaced = await decision.Registration.ReplaceEditorScopeAsync(
                "main", decision.Scope.Revision, replacement, _pageCts.Token);
            if (replaced)
            {
                if (IsCurrent(decision) && decision.Snapshot == Snapshot())
                {
                    _lastPersistedDraft = replacement;
                    _draftStorageError = null;
                    return true;
                }

                if (IsCurrent(decision.Version, decision.Model.Id) && ReferenceEquals(_draftRegistration, decision.Registration))
                {
                    _lastPersistedDraft = null;
                    if (replacement is null)
                        await decision.Registration.OpenScopeAsync("main", _pageCts.Token);
                }
            }
        }

        if (IsCurrent(decision))
            _validationMessage = "Черновик изменился во время подтверждения. Проверьте текущий ввод и повторите действие.";
        return false;
    }

    private async Task<bool> AcceptMainResetAsync(MainEditorDecision decision, EquipmentEntity model,
        DateTime? localWriteOff, IReadOnlyCollection<EquipmentPhoneLink> phones)
    {
        var state = new EquipmentDraftState
        {
            Model = model,
            WriteOffDate = localWriteOff?.Date,
            WriteOffTime = localWriteOff?.TimeOfDay,
            Phones = ClonePhones(phones),
            OriginalPhones = ClonePhones(phones),
            OriginalSnapshot = Snapshot(model, localWriteOff?.Date, localWriteOff?.TimeOfDay, phones),
        };
        return await ReplaceMainDraftAsync(decision, JsonSerializer.Serialize(state));
    }
}
