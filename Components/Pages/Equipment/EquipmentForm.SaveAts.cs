using System.Data.Common;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private PhoneSyncAttempt? PendingSavedAts => _pendingPhoneSync?.Attempt is
        { Intent: { Origin: EquipmentPhoneSyncOrigin.EquipmentSave, DurableSave: true } } attempt ? attempt : null;

    private bool CanResumeSavedAts => PendingSavedAts is { } attempt && _pendingPhoneSync?.Unknown == false &&
        !_draftOperationUnresolved && !EquipmentRecoveryWriteBlocked && _canEdit && !_loading && !_saving &&
        !_mainTransition && MainRawReady && (attempt.PlanningError is not null ||
            attempt.Operations.FirstOrDefault(step => step.Phase != PhoneSyncPhase.Succeeded)?.CanRetry == true);

    private async Task<PhoneSyncAttempt?> ReadSavedAtsAsync(int equipmentId, int employeeId, Guid? attemptId)
    {
        var attempt = await Service.ReadSavedPhoneAttemptAsync(equipmentId, employeeId, attemptId, _pageCts.Token);
        if (attempt is null) return null;
        if (!attempt.Intent.DurableSave || attempt.Intent.Origin != EquipmentPhoneSyncOrigin.EquipmentSave ||
            attempt.Intent.EquipmentId != equipmentId || !Guid.TryParseExact(attempt.AttemptId, "N", out var id) ||
            id == Guid.Empty || attemptId is not null && id != attemptId)
            throw new InvalidOperationException("Не удалось подтвердить попытку синхронизации сохранённых связей.");
        return attempt;
    }

    private async Task ReconcileSavedAtsAsync()
    {
        if (_model is not { } model || CurrentUser is not { } actor || _loading || _saving || _mainTransition ||
            PendingSavedAts is not { } pending || !Guid.TryParseExact(pending.AttemptId, "N", out var id)) return;
        var version = _operationVersion;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id && PendingSavedAts?.AttemptId == pending.AttemptId;
        try
        {
            var result = await ReadSavedAtsAsync(model.Id, actor.Id, id);
            if (!Current()) return;
            if (result is null)
            {
                if (_pendingPhoneSync is { } state) _pendingPhoneSync = state with { Unknown = true };
                _warningMessage = "Серверный результат синхронизации недоступен. Повтор заблокирован.";
            }
            else
            {
                PresentSavedAtsResult(result);
                if (PendingSavedAts is null)
                    await CheckMainServerWriteAsync(model.Id, actor.Id, version);
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            OperationCanceledException or TimeoutException)
        {
            if (Current())
            {
                if (_pendingPhoneSync is { } state) _pendingPhoneSync = state with { Unknown = true };
                _warningMessage = "Не удалось сверить серверный план АТС. Повтор записи заблокирован.";
            }
        }
        if (!_disposed) StateHasChanged();
    }

    private async Task ResumeSavedAtsAsync()
    {
        if (!CanResumeSavedAts || _model is not { } model || CurrentUser is not { } actor ||
            PendingSavedAts is not { } pending || _draftRegistration is not { } registration) return;
        var version = ++_operationVersion;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id && PendingSavedAts?.AttemptId == pending.AttemptId;
        _saving = true;
        try
        {
            if (!Current() || !ReferenceEquals(registration, _draftRegistration) ||
                EquipmentRecoveryWriteBlocked || !MainRawReady || _draftOperationUnresolved) return;
            var result = await Service.RetryPhoneSyncAttemptAsync(pending);
            if (!Current()) return;
            if (result.AttemptId != pending.AttemptId || result.Intent.EquipmentId != model.Id || !result.Intent.DurableSave ||
                result.Intent.Origin != EquipmentPhoneSyncOrigin.EquipmentSave)
                throw new InvalidOperationException("Сервер не подтвердил исходную попытку синхронизации.");

            PresentSavedAtsResult(result);
            if (PendingSavedAts is null)
                await CheckMainServerWriteAsync(model.Id, actor.Id, version);
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JSException or OperationCanceledException or TimeoutException)
        {
            if (Current() && _pendingPhoneSync is { } state)
            {
                _pendingPhoneSync = state with { Unknown = true };
                _warningMessage = "Не удалось подтвердить результат команды АТС. Сверьте серверный журнал перед повтором.";
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

    private void PresentSavedAtsResult(PhoneSyncAttempt attempt)
    {
        var remaining = attempt.Operations.Where(step => step.Phase != PhoneSyncPhase.Succeeded).ToArray();
        if (attempt.Abandoned || remaining.Length == 0 && attempt.PlanningError is null)
        {
            if (PendingSavedAts is not null) _pendingPhoneSync = null;
            _warningMessage = attempt.Abandoned
                ? "Телефонные связи сохранены. Устаревший план АТС закрыт до отправки. Требуется сверка текущих связей."
                : null;
            return;
        }

        var unknown = remaining.Count(step => step.IsUnknown);
        _pendingPhoneSync = new PendingPhoneSync(attempt.Intent.EquipmentId, [], [],
            remaining.Select(step => step.Operation).ToArray(), unknown > 0, null) { Attempt = attempt };
        _warningMessage = unknown > 0
            ? $"Телефонные связи сохранены. АТС подтвердила {attempt.Operations.Count - remaining.Length} шагов, исход ещё {unknown} неизвестен. Повтор заблокирован."
            : remaining.FirstOrDefault() is { CanRetry: false } step
                ? step.Error ?? "Контекст синхронизации сохранённых связей требует сверки."
                : "Телефонные связи сохранены. Продолжить неотправленные шаги АТС можно через «Применить».";
    }
}
