using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;
using Microsoft.JSInterop;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private Guid? _configureAttemptId;
    private bool _configureCheckRequired;
    private bool _legacyConfigurePending;

    private PhoneSyncAttempt? PendingConfigure => _pendingPhoneSync?.Attempt is
        { Intent.Origin: EquipmentPhoneSyncOrigin.ExplicitConfigure } attempt ? attempt : null;

    private bool ConfigureNeedsReconciliation => _configureCheckRequired || _legacyConfigurePending ||
        _configureAttemptId is not null || PendingConfigure is not null;

    private bool CanResumeConfigure => !_configureCheckRequired && !_legacyConfigurePending && !EquipmentRecoveryWriteBlocked &&
        _canEdit && !_loading && !_saving && !_mainTransition && MainRawReady &&
        PendingConfigure is { } pending && !pending.Operations.Any(step => step.IsUnknown) &&
        pending.Operations.FirstOrDefault(step => step.Phase != PhoneSyncPhase.Succeeded)?.CanRetry == true;

    private async Task ConfigurePhoneEquipmentAsync()
    {
        if (!CanConfigurePhone || _model is not { } model || CurrentUser is not { } actor ||
            _saving || _mainTransition || _activeEditor is not null)
            return;
        if (HasUnsavedDraft || !MainRawReady)
        {
            _validationMessage = "Сохраните оборудование перед настройкой или отмените изменения.";
            StateHasChanged();
            return;
        }
        if (ConfigureNeedsReconciliation || HasPendingPhoneSync)
        {
            _warningMessage ??= "Проверьте результат предыдущей настройки перед новой командой АТС.";
            StateHasChanged();
            return;
        }

        var attemptId = Guid.NewGuid();
        await RunConfigureActionAsync(model, actor, attemptId, () =>
            Service.ConfigurePhoneEquipmentAttemptAsync(model.Id, actor.Id, attemptId, _pageCts.Token));
    }

    private async Task ResumeConfigurePhoneAsync()
    {
        if (!CanResumeConfigure || _model is not { } model || CurrentUser is not { } actor ||
            PendingConfigure is not { } attempt || !Guid.TryParseExact(attempt.AttemptId, "N", out var id))
            return;
        await RunConfigureActionAsync(model, actor, id, () => Service.RetryPhoneSyncAttemptAsync(attempt));
    }

    private async Task RunConfigureActionAsync(EquipmentEntity model, Employee actor, Guid attemptId,
        Func<Task<PhoneSyncAttempt>> action)
    {
        var version = ++_operationVersion;
        var registration = _draftRegistration;
        var beginning = PendingConfigure is null;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id;

        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        try
        {
            if (registration is null)
                throw new InvalidOperationException("Не удалось проверить состояние редактора.");
            if (!Current() || !ReferenceEquals(registration, _draftRegistration)) return;
            if (EquipmentRecoveryWriteBlocked || _draftOperationUnresolved || !MainRawReady || beginning && HasUnsavedDraft)
                throw new InvalidOperationException("Сначала проверьте незавершённую операцию или ввод в карточке.");

            _configureAttemptId = attemptId;
            var result = await action();
            if (!Current()) return;
            if (result.AttemptId != attemptId.ToString("N") || result.Intent.EquipmentId != model.Id ||
                result.Intent.Origin != EquipmentPhoneSyncOrigin.ExplicitConfigure)
                throw new InvalidOperationException("Сервер не подтвердил идентификатор настройки.");

            PresentConfigureResult(result, completedNow: true);
            if (!ConfigureNeedsReconciliation)
                await CheckMainServerWriteAsync(model.Id, actor.Id, version);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InventoryOperationPendingException
            { BlockedBeforeReservation: true })
        {
            if (Current())
            {
                _configureAttemptId = PendingConfigure is null ? null : _configureAttemptId;
                _warningMessage = error.Message;
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or JSException or
            TimeoutException or OperationCanceledException)
        {
            if (Current())
            {
                _configureCheckRequired = _configureAttemptId is not null;
                _warningMessage = _configureCheckRequired
                    ? "Не удалось подтвердить исход настройки. Обновите журнал АТС перед повтором."
                    : error.Message;
            }
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task ReconcileConfigureAsync()
    {
        if (_model is not { } model || CurrentUser is not { } actor || _loading || _saving || _mainTransition)
            return;
        if (_legacyConfigurePending)
        {
            ShowLegacyConfigurePending();
            StateHasChanged();
            return;
        }
        var version = _operationVersion;
        var id = _configureAttemptId;
        var result = await ReadConfigureForFormAsync(model.Id, actor.Id, id);
        if (!IsCurrentSave(model, version) || CurrentUser?.Id != actor.Id || id != _configureAttemptId)
            return;
        ApplyConfigureRead(result);
        if (!ConfigureNeedsReconciliation)
            await CheckMainServerWriteAsync(model.Id, actor.Id, version);
        StateHasChanged();
    }

    private async Task<(bool Read, PhoneSyncAttempt? Attempt)> ReadConfigureForFormAsync(int equipmentId, int employeeId,
        Guid? attemptId)
    {
        try
        {
            var result = await Service.ReadConfigurePhoneAttemptAsync(equipmentId, employeeId, attemptId, _pageCts.Token);
            if (result is not null && (result.Intent.EquipmentId != equipmentId ||
                result.Intent.Origin != EquipmentPhoneSyncOrigin.ExplicitConfigure ||
                !Guid.TryParseExact(result.AttemptId, "N", out var verified) || verified == Guid.Empty ||
                attemptId is not null && verified != attemptId))
                return (false, null);
            // Если точная попытка не дошла до резерва, отдельно проверяем ранее открытый план этой карточки.
            return attemptId is not null && result is null
                ? await ReadConfigureForFormAsync(equipmentId, employeeId, null) : (true, result);
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            TimeoutException or OperationCanceledException)
        {
            return (false, null);
        }
    }

    private void ApplyConfigureRead((bool Read, PhoneSyncAttempt? Attempt) result)
    {
        if (_legacyConfigurePending)
        {
            ShowLegacyConfigurePending();
            return;
        }
        if (result.Read)
            PresentConfigureResult(result.Attempt);
        else
        {
            _configureCheckRequired = true;
            _warningMessage = "Не удалось прочитать журнал АТС оборудования. Повтор настройки заблокирован.";
        }
    }

    private void ShowLegacyConfigurePending() =>
        _warningMessage = "Исход настройки из прежней версии не подтверждён. Повтор команды АТС заблокирован.";

    private void PresentConfigureResult(PhoneSyncAttempt? attempt, bool completedNow = false)
    {
        var reconciling = ConfigureNeedsReconciliation;
        _configureCheckRequired = false;
        _configureAttemptId = null;
        if (PendingConfigure is not null)
            _pendingPhoneSync = null;
        if (attempt is null)
        {
            if (reconciling) _warningMessage = null;
            return;
        }
        if (attempt.Abandoned)
        {
            _warningMessage = "Устаревший план закрыт. Команды АТС не отправлялись. Обновите карточку перед новой настройкой.";
            return;
        }
        var incomplete = attempt.Operations.Where(step => step.Phase != PhoneSyncPhase.Succeeded).ToArray();
        if (incomplete.Length == 0)
        {
            _warningMessage = null;
            if (completedNow)
                ShowStatus("Настройка оборудования выполнена.");
            return;
        }

        _configureAttemptId = Guid.ParseExact(attempt.AttemptId, "N");
        var unknown = incomplete.Count(step => step.IsUnknown);
        _pendingPhoneSync = new PendingPhoneSync(attempt.Intent.EquipmentId, [], [],
            incomplete.Select(step => step.Operation).ToArray(), unknown > 0, null) { Attempt = attempt };
        if (unknown > 0)
            _warningMessage = $"Настройка АТС: подтверждено {attempt.Operations.Count - incomplete.Length}, неизвестно {unknown}. Повтор заблокирован.";
        else if (incomplete[0].CanRetry)
            _warningMessage = "Есть неотправленные шаги АТС. Продолжить настройку можно через «Применить».";
        else
            _warningMessage = incomplete[0].Error ?? "Контекст настройки АТС изменился. Требуется сверка.";
    }
}
