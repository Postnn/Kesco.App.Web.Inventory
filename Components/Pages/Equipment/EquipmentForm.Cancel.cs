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
    private MainEditorDecision? _pendingMainClose;
    private bool _finishingMainClose;
    private bool MainCloseInProgress => _pendingMainClose is not null;

    private bool PreserveMainOutcome => _draftRestoreBlocked || _phoneSyncCheckpoint is not null || HasPendingPhoneSync ||
        ConfigureNeedsReconciliation || _activeEditor is not null;
    private bool ConfirmExternalExit => !_mainDraftCancelled && !_allowNavigation &&
        (IsModified || PreserveMainOutcome || _mainRawRestorePending || MainCloseInProgress);

    private sealed record MainEditorDecision(
        ClayDraftRegistration Registration, ClayDraftEditorCloseState Scope, EquipmentEntity Model,
        int Version, long EditorGeneration, int EmployeeId, string Snapshot, CancellationToken Token);

    private bool IsCurrent(MainEditorDecision decision) =>
        !decision.Token.IsCancellationRequested && IsCurrent(decision.Version, decision.Model.Id) &&
        ReferenceEquals(_draftRegistration, decision.Registration) && ReferenceEquals(_model, decision.Model) &&
        CurrentUser?.Id == decision.EmployeeId && _editorGeneration == decision.EditorGeneration;

    private Task CancelAsync() => RunMainTransitionAsync(false, CloseAsync, _pageCts.Token);

    private async Task RefreshAsync()
    {
        if (MainCloseInProgress || _mainDraftCancelled)
            return;
        if (EquipmentRecoveryWriteBlocked && !_loading && !_saving && !_draftRestoring && !_mainTransition)
        {
            if (_draftRestoreBlocked) await RetryLoadAsync();
            else if (!_draftRegistered) await InitializeDraftAsync();
            return;
        }
        if (_mainRawRestorePending && CanRetryMainRaw && _canEdit)
        {
            await RetryMainDraftRawAsync();
            return;
        }

        if (ConfigureNeedsReconciliation)
        {
            await ReconcileConfigureAsync();
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
            _draftAttachingInstance == _draftInstance || token.IsCancellationRequested)
            return;

        if (_mainDraftCancelled || MainCloseInProgress)
        {
            if (!refresh) await FinishMainCloseAndExitAsync(exit, token);
            return;
        }
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
            var scope = await registration.ReadEditorCloseStateAsync(token) ??
                throw new InvalidOperationException("Не удалось прочитать состояние редактора.");
            var decision = new MainEditorDecision(registration, scope, model, version, generation, employeeId, Snapshot(), token);
            if (!IsCurrent(decision))
                return;

            var protectedOutcome = PreserveMainOutcome || HasNestedInput(scope);
            var dirty = HasUnsavedDraft || scope.HasRaw || _mainRawRestorePending || _mainConflictMessage is not null;
            if (refresh && protectedOutcome)
            {
                if (_draftRestoreBlocked || _phoneSyncCheckpoint is not null ||
                    _activeEditor is not null || HasNestedInput(scope) || dirty)
                {
                    if (IsCurrent(decision))
                        _warningMessage ??= "Обновление сохранило локальный ввод.";
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
                    await StartMainCloseAsync(decision, exit);
                return;
            }

            var choice = await AskMainEditorChoiceAsync(token);
            if (!IsCurrent(decision) || choice == ClayEditorCancelChoice.Continue)
                return;

            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                if (protectedOutcome || PreserveMainOutcome)
                    _validationMessage = "Сначала завершите проверку предыдущей команды или работу дочернего редактора. Черновик сохранён.";
                else
                    await LoadEquipmentAsync(Id, decision);
                return;
            }

            await StartMainCloseAsync(decision, exit);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or
            OperationCanceledException or System.Data.Common.DbException or UnauthorizedAccessException or TimeoutException)
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

    private static bool HasNestedInput(ClayDraftEditorCloseState scope) =>
        scope.TypedScopes.Any(name => name.StartsWith("child:", StringComparison.Ordinal));

    private async Task StartMainCloseAsync(MainEditorDecision decision, Func<Task> exit)
    {
        if (_activeEditor is not null)
        {
            _validationMessage = "Сначала закройте дочерний редактор.";
            return;
        }
        if (!IsCurrent(decision) || decision.Snapshot != Snapshot())
        {
            _validationMessage = "Ввод изменился во время подтверждения. Проверьте текущие данные.";
            return;
        }
        if (_phoneSyncCheckpoint is not null || _legacyConfigurePending || HasPendingPhoneSync)
        {
            _validationMessage = "Исход предыдущей команды не подтверждён. Сверьте данные перед закрытием.";
            return;
        }
        if (!IsCurrent(decision) || decision.Snapshot != Snapshot())
            return;
        _pendingMainClose = decision;
        await FinishMainCloseAndExitAsync(exit, decision.Token);
    }

    private async Task FinishMainCloseAndExitAsync(Func<Task> exit, CancellationToken token)
    {
        if (_mainDraftCancelled)
        {
            await exit();
            return;
        }
        if (_finishingMainClose || _pendingMainClose is not { } pending || !IsCurrent(pending with { Token = token }))
            return;
        _finishingMainClose = true;
        try
        {
            if (!await pending.Registration.CloseRamIfRevisionAsync(pending.Scope.Revision, token))
            {
                _pendingMainClose = null;
                _validationMessage = "Ввод изменился во время подтверждения. Проверьте текущие данные.";
                return;
            }
        }
        catch (Exception error) when (error is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrent(pending with { Token = CancellationToken.None }))
                _validationMessage = "Не удалось удалить локальный ввод. Нажмите «Отмена», чтобы повторить закрытие.";
            return;
        }
        finally
        {
            _finishingMainClose = false;
        }

        if (ReferenceEquals(_draftRegistration, pending.Registration))
        {
            _pendingMainClose = null;
            _draftRegistration = null;
            _mainDraftCancelled = true;
            _validationMessage = null;
            if (IsCurrent(pending.Version, pending.Model.Id) && !token.IsCancellationRequested)
                await exit();
        }
    }

    private async Task CloseAfterMainSaveAsync()
    {
        if (_mainTransition || _model is not { } model || _draftRegistration is not { } registration ||
            CurrentUser is not { } actor)
            return;
        var version = _operationVersion;
        var generation = _editorGeneration;
        var snapshot = Snapshot();
        var ct = _pageCts.Token;
        _mainTransition = true;
        try
        {
            var scope = await registration.ReadEditorCloseStateAsync(ct) ??
                throw new InvalidOperationException("Не удалось прочитать состояние редактора.");
            var decision = new MainEditorDecision(registration, scope, model, version, generation, actor.Id, snapshot, ct);
            if (!IsCurrent(decision)) return;
            if (snapshot != Snapshot() || HasUnsavedDraft || PreserveMainOutcome || scope.HasRaw ||
                HasNestedInput(scope) || _activeEditorContext is not null || _restoredEditorState is not null ||
                _upsTestDraft is not null || _mainRawRestorePending)
            {
                _validationMessage = "На форме остался ввод. Проверьте данные перед закрытием.";
                return;
            }
            await StartMainCloseAsync(decision, CloseAsync);
        }
        catch (Exception error) when (error is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrent(version, model.Id))
                _validationMessage = "Не удалось проверить ввод перед закрытием. Форма осталась открыта.";
        }
        finally
        {
            _mainTransition = false;
            if (!_disposed) StateHasChanged();
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
