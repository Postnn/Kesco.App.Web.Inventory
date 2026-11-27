using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string UpdateLocationPeriodCommand = "equipment-location-period.update";
    internal const string AddLocationPeriodCommand = "equipment-location-period.add";
    internal const string IssueResponsiblePeriodCommand = "equipment-responsible-period.issue";
    internal const string UpdateResponsiblePeriodCommand = "equipment-responsible-period.update";
    internal const string WithdrawResponsiblePeriodCommand = "equipment-responsible-period.withdraw";

    private sealed record CompoundPeriodContext(EquipmentLocationPeriod? Relocation, EquipmentResponsiblePeriod? Next);

    /// <summary>Фиксирует обновление последнего расположения и подтверждение точного периода.</summary>
    public virtual Task UpdateLocationAsync(EquipmentLocationPeriod location, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var submitted = new EquipmentLocationPeriod
        {
            EquipmentId = location.EquipmentId, FromUtc = location.FromUtc, LocationId = location.LocationId,
            ChangedAtUtc = location.ChangedAtUtc, ReturnDate = location.ReturnDate, Notes = location.Notes,
        };
        return RunPeriodJournalWriteAsync(submitted.EquipmentId, submitted.FromUtc, UpdateLocationPeriodCommand,
            employeeId, attemptId, submitted.LocationId <= 0 || submitted.ChangedAtUtc == default
                ? "Не удалось подтвердить исходное расположение." : null,
            (connection, transaction, start, _, token) => submitted.UpdateEnlistedAsync(connection, transaction, start, token), ct);
    }

    /// <summary>Фиксирует выдачу оборудования по точному UTC-ключу периода ответственности.</summary>
    public virtual Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, Guid attemptId, CancellationToken ct) =>
        SaveResponsibleJournalAsync(responsible, insert: true, employeeId, attemptId, ct);

    /// <summary>Фиксирует изменение срока возврата и примечаний последнего периода ответственности.</summary>
    public virtual Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, Guid attemptId, CancellationToken ct) =>
        SaveResponsibleJournalAsync(responsible, insert: false, employeeId, attemptId, ct);

    private Task SaveResponsibleJournalAsync(EquipmentResponsiblePeriod source, bool insert, int employeeId,
        Guid attemptId, CancellationToken ct)
    {
        var submitted = new EquipmentResponsiblePeriod
        {
            EquipmentId = source.EquipmentId, FromUtc = source.FromUtc, ToUtc = source.ToUtc, EmployeeId = source.EmployeeId,
            ChangedAtUtc = source.ChangedAtUtc, ReturnDate = source.ReturnDate, Notes = source.Notes,
        };
        var invalid = submitted.EmployeeId <= 0 || !insert && submitted.ChangedAtUtc == default;
        return RunPeriodJournalWriteAsync(submitted.EquipmentId, submitted.FromUtc,
            insert ? IssueResponsiblePeriodCommand : UpdateResponsiblePeriodCommand, employeeId, attemptId,
            invalid ? "Не удалось подтвердить исходный период ответственности." : null,
            (connection, transaction, start, _, token) => insert
                ? submitted.IssueEnlistedAsync(connection, transaction, start, token)
                : submitted.UpdateEnlistedAsync(connection, transaction, start, token), ct);
    }

    private async Task RunPeriodJournalWriteAsync(int equipmentId, DateTime fromUtc, string commandKind, int employeeId, Guid attemptId,
        string? validation, Func<DbConnection, DbTransaction, DateTime, IReadOnlyList<int>, CancellationToken, Task<int>> write,
        CancellationToken ct, CompoundPeriodContext? compound = null)
    {
        var submitting = false;
        try
        {
            if (equipmentId <= 0 || fromUtc == default || attemptId == Guid.Empty)
                throw new EquipmentPeriodWriteRejectedException("Не удалось подтвердить оборудование и начало периода.");
            if (validation is not null) throw new EquipmentPeriodWriteRejectedException(validation);
            var start = EquipmentPeriodJournalWrite.NormalizeStartUtc(fromUtc);
            var principal = await GetWindowsPrincipalAsync();
            var actor = await RequireMainActorAsync(principal, employeeId, ct);
            var attempt = PeriodJournalAttempt(actor, equipmentId, start, commandKind, attemptId);
            int[] simIds = [];
            if (compound is not null)
            {
                var scopes = attempt.OtherScopes!.ToList();
                if (compound.Relocation is { } relocation)
                {
                    relocation.FromUtc = EquipmentPeriodJournalWrite.NormalizeStartUtc(relocation.FromUtc);
                    scopes.Add(PeriodJournalScope(equipmentId, relocation.FromUtc, location: true));
                }
                if (compound.Next is { } next)
                {
                    next.FromUtc = EquipmentPeriodJournalWrite.NormalizeStartUtc(next.FromUtc);
                    scopes.Add(PeriodJournalScope(equipmentId, next.FromUtc, location: false));
                }

                attempt = InventoryOperationJournal.ForEmployee(actor, attemptId, MainEquipmentScope(equipmentId), commandKind, scopes);
                var previous = await _db.RunDbAsync(connection =>
                    InventoryOperationJournal.ReadAttemptContainingScopesAsync(connection, null, attempt, ct));
                if (previous is not null)
                {
                    submitting = true;
                    AssertPeriodReceipt(previous, equipmentId);
                    return;
                }
                if (compound.Relocation is { } move)
                    simIds = await _db.RunDbAsync(connection => EquipmentLocationEnlistedSql.ReadPlanAsync(connection, null, move, ct));

                scopes.AddRange(simIds.Select(MainEquipmentScope));
                attempt = InventoryOperationJournal.ForEmployee(actor, attemptId, MainEquipmentScope(equipmentId), commandKind, scopes);
            }
            submitting = true;
            var receipt = await InventoryOperationJournal.RunSqlAsync<EquipmentPeriodWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) => await write(connection, transaction, start, simIds, token), ct,
                prepare: async (connection, transaction, token) =>
                {
                    try { AssertMainActor(await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, token), employeeId); }
                    catch (EquipmentMainWriteRejectedException error) { throw new EquipmentPeriodWriteRejectedException(error.Message); }
                    if (compound?.Relocation is { } move)
                    {
                        var current = await EquipmentLocationEnlistedSql.ReadPlanAsync(connection, transaction, move, token);
                        if (!current.SequenceEqual(simIds))
                            throw new EquipmentPeriodWriteRejectedException("Состав связанных SIM изменился. Обновите карточку.");
                    }
                });
            AssertPeriodReceipt(receipt, equipmentId);
        }
        catch (Exception error) when (error is EquipmentPeriodWriteRejectedException or EquipmentMainWriteRejectedException)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (Exception error) when (error is InventoryOperationPreparationException or
            InventoryOperationPendingException { BlockedBeforeReservation: true })
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or
            OperationCanceledException or SqlTypeException)
        {
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent,
                error.Message, error);
        }
    }

    /// <summary>Читает результат попытки с точным временем начала собственного периода.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadPeriodWriteReceiptAsync(int equipmentId, DateTime fromUtc,
        string commandKind, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (equipmentId <= 0 || fromUtc == default || attemptId == Guid.Empty ||
            commandKind is not (AddLocationPeriodCommand or UpdateLocationPeriodCommand or IssueResponsiblePeriodCommand or
                UpdateResponsiblePeriodCommand or WithdrawResponsiblePeriodCommand))
            throw new UnauthorizedAccessException("Контекст периода недоступен для сверки.");
        var start = EquipmentPeriodJournalWrite.NormalizeStartUtc(fromUtc);
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = AssertMainActor(await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct), employeeId);
            var attempt = PeriodJournalAttempt(actor, equipmentId, start, commandKind, attemptId);
            var receipt = commandKind is AddLocationPeriodCommand or WithdrawResponsiblePeriodCommand
                ? await InventoryOperationJournal.ReadAttemptContainingScopesAsync(connection, transaction, attempt, ct)
                : await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction, attempt, ct);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    private static InventoryOperationJournal.Attempt PeriodJournalAttempt(Employee actor, int equipmentId, DateTime fromUtc,
        string commandKind, Guid attemptId) => InventoryOperationJournal.ForEmployee(actor, attemptId,
        MainEquipmentScope(equipmentId), commandKind,
        [PeriodJournalScope(equipmentId, fromUtc, commandKind is AddLocationPeriodCommand or UpdateLocationPeriodCommand)]);

    private static string PeriodJournalScope(int equipmentId, DateTime fromUtc, bool location)
    {
        var period = location ? "location-period" : "responsible-period";
        return "equipment/" + period + "/" + equipmentId.ToString(CultureInfo.InvariantCulture) + "/" +
            fromUtc.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static void AssertPeriodReceipt(InventoryOperationReceipt receipt, int equipmentId)
    {
        if (receipt.State == InventoryOperationState.Rejected)
            throw new EquipmentPeriodWriteRejectedException("Предыдущая попытка изменения периода отклонена.");
        if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != equipmentId)
            throw new InventoryOperationPendingException();
    }
}
