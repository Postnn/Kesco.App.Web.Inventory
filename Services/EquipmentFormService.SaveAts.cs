using System.Data.Common;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    private static EquipmentPhoneSyncIntent SavedPhoneTicket(Guid attemptId, int equipmentId) =>
        new EquipmentPhoneSyncIntent(attemptId.ToString("N"), equipmentId, EquipmentPhoneSyncOrigin.EquipmentSave,
            default, null, null, [], []) { DurableSave = true };

    /// <summary>Читает исход АТС после SQL-сохранения связей по точной попытке или собственной открытой записи.</summary>
    public virtual async Task<PhoneSyncAttempt?> ReadSavedPhoneAttemptAsync(int equipmentId, int employeeId,
        Guid? attemptId, CancellationToken ct)
    {
        var saved = await EquipmentAtsWorkflow.ReadSavedAsync(_db, await GetWindowsPrincipalAsync(), employeeId,
            equipmentId, attemptId, ct);
        return saved is null ? null : SavedAttempt(saved);
    }

    private async Task<PhoneSyncAttempt> SynchronizeSavedPhonesAsync(EquipmentPhoneSyncIntent intent)
    {
        var attemptId = RequireSavedPhoneTicket(intent);
        var principal = await GetWindowsPrincipalAsync();
        var actor = await RequirePhoneSyncActorAsync(principal);
        EquipmentAtsSaveSnapshot snapshot;
        try
        {
            snapshot = await EquipmentAtsWorkflow.PrepareSavedAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default);
        }
        catch (EquipmentAtsContextChangedException error)
        {
            var abandoned = await EquipmentAtsWorkflow.TryAbandonSavedAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default);
            var changed = await EquipmentAtsWorkflow.ReadSavedAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default) ??
                throw new InventoryOperationPendingException();
            return SavedAttempt(changed) with { PlanningError = error.Message, Abandoned = abandoned };
        }
        catch (Exception error) when (error is EquipmentAtsPlanningUnavailableException or DbException or TimeoutException)
        {
            var existing = await EquipmentAtsWorkflow.ReadSavedAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default) ??
                throw new InventoryOperationPendingException();
            return SavedAttempt(existing) with { PlanningError = "Серверный план АТС требует повторной проверки." };
        }

        return await DispatchEquipmentAtsAsync(principal, snapshot.Header, snapshot.Steps, SavedAttempt(snapshot), null, default);
    }

    private async Task<PhoneSyncReconciliation> ReconcileSavedPhonesAsync(EquipmentPhoneSyncIntent intent)
    {
        var attemptId = RequireSavedPhoneTicket(intent);
        var principal = await GetWindowsPrincipalAsync();
        var actor = await RequirePhoneSyncActorAsync(principal);
        var snapshot = await EquipmentAtsWorkflow.ReadSavedAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default) ??
            throw new InventoryOperationPendingException();
        var matches = await EquipmentAtsWorkflow.MatchesSavedContextAsync(_db, principal, actor.Id, intent.EquipmentId, attemptId, default);
        var equipment = await GetAsync(intent.EquipmentId);
        var links = matches ? await GetPhonesAsync(intent.EquipmentId) : [];
        return new PhoneSyncReconciliation(SavedAttempt(snapshot), equipment, links, matches);
    }

    private static Guid RequireSavedPhoneTicket(EquipmentPhoneSyncIntent intent) =>
        intent is { Origin: EquipmentPhoneSyncOrigin.EquipmentSave, DurableSave: true, EquipmentId: > 0 } &&
        Guid.TryParseExact(intent.AttemptId, "N", out var id) && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Не удалось определить попытку синхронизации сохранённых связей.");

    private static PhoneSyncAttempt SavedAttempt(EquipmentAtsSaveSnapshot snapshot)
    {
        var header = snapshot.Header;
        var intent = SavedPhoneTicket(header.AttemptId, header.EquipmentId) with
        {
            ExpectedChangedAtUtc = header.ExpectedChangedAtUtc,
            OwnerId = header.OwnerId,
        };
        var attempt = ToPhoneSyncAttempt(intent, header, snapshot.Steps);
        return snapshot.Prepared || attempt.Abandoned ? attempt : attempt with
        {
            PlanningError = "Телефонные связи сохранены. План АТС ещё не подготовлен.",
        };
    }
}
