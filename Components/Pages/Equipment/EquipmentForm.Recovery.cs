using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private IReadOnlyList<ClayDraftRecoveryCandidate> _equipmentRecoveryCandidates = [];
    private bool _recoveryScanReady;
    private bool _checkingEquipmentRecovery;
    private bool _recoveringEquipmentDraft;
    private bool _recoveryPristine;
    private bool _orphanAdoptionConflicted;
    private string? _equipmentRecoveryNotice;

    private bool EquipmentRecoveryWriteBlocked => !_recoveryScanReady || _checkingEquipmentRecovery ||
        _recoveringEquipmentDraft || _orphanAdoptionConflicted || _equipmentRecoveryCandidates.Any(item => item.HasOperation);

    private bool IsEquipmentRecoveryCurrent(ClayDraftRegistration registration, int version, int id, int employeeId) =>
        OwnsMainDraft(registration, id) && IsCurrent(version, id) && CurrentUser?.Id == employeeId;

    private async Task ScanEquipmentRecoveryAsync(ClayDraftRegistration registration, int version, int id, int employeeId)
    {
        if (!IsEquipmentRecoveryCurrent(registration, version, id, employeeId) || _checkingEquipmentRecovery)
            return;
        _checkingEquipmentRecovery = true;
        try
        {
            var candidates = await registration.ListRecoveryCandidatesAsync(_pageCts.Token);
            if (!IsEquipmentRecoveryCurrent(registration, version, id, employeeId)) return;
            _equipmentRecoveryCandidates = candidates ??
                throw new InvalidOperationException("Не удалось подтвердить список черновиков.");
            _recoveryScanReady = true;
            if (!_orphanAdoptionConflicted)
                _equipmentRecoveryNotice = candidates.Any(item => item.HasOperation)
                    ? "С оборудованием связана незавершённая операция из другого сеанса. Повтор записи заблокирован."
                    : null;
        }
        catch (Exception error) when (IsEquipmentRecoveryError(error))
        {
            if (IsEquipmentRecoveryCurrent(registration, version, id, employeeId))
            {
                _recoveryScanReady = false;
                _equipmentRecoveryNotice = "Не удалось проверить незавершённые операции оборудования. Запись заблокирована.";
            }
        }
        finally
        {
            if (ReferenceEquals(_draftRegistration, registration))
            {
                _checkingEquipmentRecovery = false;
                if (IsEquipmentRecoveryCurrent(registration, version, id, employeeId)) StateHasChanged();
            }
        }
    }

    private async Task<bool> TryRestoreSingleEquipmentDraftAsync()
    {
        if (_equipmentRecoveryCandidates is not [var candidate] || !CanOpenEquipmentRecovery(candidate))
            return false;

        await OpenEquipmentRecoveryAsync(candidate);
        return true;
    }

    private bool CanOpenEquipmentRecovery(ClayDraftRecoveryCandidate candidate) =>
        candidate.CanRecover && _equipmentRecoveryCandidates.Contains(candidate) && _recoveryScanReady &&
        _recoveryPristine && _canEdit && !_loading && !_saving && !_draftRestoring && !_draftPersisting &&
        !_mainTransition && !_checkingEquipmentRecovery && !_recoveringEquipmentDraft && !_orphanAdoptionConflicted &&
        _activeEditor is null && !HasUnsavedDraft && !PreserveMainOutcome &&
        _draftRegistration is { } registration && !registration.Restore.HasDraft;

    private async Task OpenEquipmentRecoveryAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (!CanOpenEquipmentRecovery(candidate) || _draftRegistration is not { } registration ||
            _model is not { } model || CurrentUser is not { } openedBy)
            return;
        var version = _operationVersion;
        var generation = _editorGeneration;
        var initial = SerializeDraftState();
        var id = model.Id;
        bool Current() => IsEquipmentRecoveryCurrent(registration, version, id, openedBy.Id) &&
            ReferenceEquals(_model, model) && _editorGeneration == generation;
        bool Pristine() => Current() && _recoveryPristine && !HasUnsavedDraft && _activeEditor is null &&
            !PreserveMainOutcome && initial == SerializeDraftState();

        _recoveringEquipmentDraft = true;
        var adoptionRequested = false;
        try
        {
            var actor = await Service.GetCurrentUserWithRolesAsync();
            if (!Pristine()) return;
            if (actor?.Id != openedBy.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            {
                _equipmentRecoveryNotice = "Не удалось подтвердить сотрудника и право на открытие черновика.";
                return;
            }

            var equipment = await Service.GetAsync(id);
            if (!Pristine()) return;
            if (equipment?.Id != id)
            {
                _equipmentRecoveryNotice = "Оборудование недоступно. Черновик сохранён.";
                return;
            }

            await ScanEquipmentRecoveryAsync(registration, version, id, openedBy.Id);
            if (!Pristine() || !_recoveryScanReady || _equipmentRecoveryCandidates is not [var current] || current != candidate)
                return;
            actor = await Service.GetCurrentUserWithRolesAsync();
            if (!Pristine()) return;
            if (actor?.Id != openedBy.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            {
                _equipmentRecoveryNotice = "Сотрудник или право на открытие черновика изменились.";
                return;
            }
            adoptionRequested = true;
            var adopted = await registration.RecoverListedDraftAsync(candidate.DraftId, _pageCts.Token);
            if (!adopted)
            {
                if (Current()) _equipmentRecoveryNotice = "Черновик занят или локальный ввод уже изменился.";
                return;
            }

            if (!Pristine() || registration.Restore.Revision != candidate.Revision)
            {
                if (ReferenceEquals(_draftRegistration, registration))
                    BlockEquipmentRecoveryPublication();
                return;
            }

            _recoveryPristine = false;
            _equipmentRecoveryCandidates = _equipmentRecoveryCandidates.Where(item => item.DraftId != candidate.DraftId).ToArray();
            _equipmentRecoveryNotice = null;
            await ApplyEquipmentDraftAsync(registration, id, version, canPublishRecovery: () => Current() && initial == SerializeDraftState());
            if (Current())
                await ScanEquipmentRecoveryAsync(registration, version, id, openedBy.Id);
        }
        catch (Exception error) when (IsEquipmentRecoveryError(error))
        {
            if (ReferenceEquals(_draftRegistration, registration))
            {
                if (adoptionRequested) BlockEquipmentRecoveryPublication();
                else if (Current()) _equipmentRecoveryNotice = "Не удалось открыть сохранённый черновик оборудования.";
            }
        }
        finally
        {
            if (ReferenceEquals(_draftRegistration, registration))
            {
                _recoveringEquipmentDraft = false;
                if (!_disposed) StateHasChanged();
            }
        }
    }

    private void BlockEquipmentRecoveryPublication()
    {
        _orphanAdoptionConflicted = true;
        _draftRestoring = false;
        _recoveryPristine = false;
        _equipmentRecoveryNotice = "Локальный ввод оставлен на форме. Сохранённый черновик не изменён.";
    }

    private void ResetEquipmentRecovery()
    {
        _equipmentRecoveryCandidates = [];
        _recoveryScanReady = _checkingEquipmentRecovery = _recoveringEquipmentDraft = _recoveryPristine = _orphanAdoptionConflicted = false;
        _equipmentRecoveryNotice = null;
    }

    private static bool IsEquipmentRecoveryError(Exception error) =>
        error is JSException or JSDisconnectedException or InvalidOperationException or
            OperationCanceledException or SqlException or UnauthorizedAccessException or System.Text.Json.JsonException;
}
