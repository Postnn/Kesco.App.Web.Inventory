using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string InsertComputerCommand = "equipment-computer.insert";
    internal const string UpdateComputerCommand = "equipment-computer.update";

    /// <summary>Сохраняет дневной снимок и точное серверное подтверждение в одной транзакции.</summary>
    public virtual async Task SaveComputerPhasedAsync(EquipmentComputerSnapshot snapshot, DateTime? businessToday,
        int? expectedModelId, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var submitting = false;
        var equipmentId = snapshot.EquipmentId;
        var date = snapshot.Date.Date;
        var existing = snapshot.ExpectedExisting;
        var changedAt = snapshot.ChangedAt;
        try
        {
            if (equipmentId <= 0 || date == default || expectedModelId is not > 0 || attemptId == Guid.Empty)
                throw new EquipmentComputerWriteRejectedException("Не удалось подтвердить дату и модель снимка компьютера.");
            if (existing && changedAt == default)
                throw new EquipmentComputerWriteRejectedException("Не удалось подтвердить исходную версию снимка компьютера.");
            if (businessToday is null)
                throw new EquipmentComputerWriteRejectedException("Актуальная календарная дата клиента не задана. Сохранение конфигурации отменено.");

            var principal = await GetWindowsPrincipalAsync();
            var (actor, context) = await _db.RunDbAsync(connection => ReadComputerJournalContextAsync(connection,
                null, principal, employeeId, equipmentId, ct));
            ValidateComputerJournalSnapshot(snapshot, businessToday.Value, expectedModelId.Value, context);
            var attempt = ComputerJournalAttempt(actor, equipmentId, date, existing, attemptId);
            submitting = true;
            var receipt = await InventoryOperationJournal.RunSqlAsync<EquipmentComputerWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) => await snapshot.SaveEnlistedAsync(connection, transaction, token), ct,
                prepare: async (connection, transaction, token) =>
                {
                    var (_, fresh) = await ReadComputerJournalContextAsync(connection, transaction, principal, employeeId, equipmentId, token);
                    if (snapshot.EquipmentId != equipmentId || snapshot.Date.Date != date ||
                        snapshot.ExpectedExisting != existing || snapshot.ChangedAt != changedAt)
                        throw new EquipmentComputerWriteRejectedException("Исходный снимок компьютера изменился.");
                    ValidateComputerJournalSnapshot(snapshot, businessToday.Value, expectedModelId.Value, fresh);
                });
            if (receipt.State == InventoryOperationState.Rejected)
                throw new EquipmentComputerWriteRejectedException("Предыдущая попытка сохранения снимка отклонена.");
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != equipmentId)
                throw new InventoryOperationPendingException();
        }
        catch (EquipmentComputerWriteRejectedException error)
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

    /// <summary>Сверяет собственный результат по точной области оборудования и дате снимка.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadComputerWriteReceiptAsync(int equipmentId, DateTime date,
        bool expectedExisting, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (equipmentId <= 0 || date == default || attemptId == Guid.Empty)
            throw new UnauthorizedAccessException("Контекст снимка компьютера недоступен для сверки.");
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var (actor, _) = await ReadComputerJournalContextAsync(connection, transaction, principal, employeeId, equipmentId, ct);
            var receipt = await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction,
                ComputerJournalAttempt(actor, equipmentId, date.Date, expectedExisting, attemptId), ct);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    private static void ValidateComputerJournalSnapshot(EquipmentComputerSnapshot snapshot, DateTime day,
        int expectedModelId, ComputerModelContext context)
    {
        if (context.ModelId != expectedModelId)
            throw new EquipmentComputerWriteRejectedException("Модель компьютера изменилась. Откройте редактор заново.");
        if (snapshot.ValidateConfiguration(day, context.MemoryBanks) is { } validation)
            throw new EquipmentComputerWriteRejectedException(validation);
    }

    private static InventoryOperationJournal.Attempt ComputerJournalAttempt(Employee actor, int equipmentId, DateTime date,
        bool expectedExisting, Guid id) => InventoryOperationJournal.ForEmployee(actor, id, MainEquipmentScope(equipmentId),
        expectedExisting ? UpdateComputerCommand : InsertComputerCommand,
        ["equipment/computer/" + equipmentId.ToString(CultureInfo.InvariantCulture) + "/" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)]);

    /// <summary>Проверяет актуальную модель и читает строку исходного календарного дня для сброса редактора.</summary>
    public virtual async Task<(int MemoryBanks, EquipmentComputerSnapshot? Snapshot)?> ReadComputerEditorResetAsync(
        int equipmentId, int employeeId, int? expectedModelId, DateTime day, bool expectedExisting, CancellationToken ct)
    {
        if (equipmentId <= 0 || expectedModelId is not > 0 || day == default)
            return null;

        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync<(int MemoryBanks, EquipmentComputerSnapshot? Snapshot)?>(async connection =>
        {
            var (_, actual) = await ReadComputerJournalContextAsync(connection, null, principal, employeeId, equipmentId, ct);
            if (actual.ModelId != expectedModelId)
                return null;

            var rows = await connection.QueryAsync<EquipmentComputerSnapshot>(new CommandDefinition(
                SQLQueries.SELECT_Компьютеры_КодОборудования, new { EquipmentId = equipmentId }, cancellationToken: ct));
            var snapshot = rows.SingleOrDefault(row => row.EquipmentId == equipmentId && row.Date.Date == day.Date);
            if ((snapshot is not null) != expectedExisting)
                return null;
            return (actual.MemoryBanks ?? 0, snapshot);
        });
    }

    private static async Task<(Employee Actor, ComputerModelContext Context)> ReadComputerJournalContextAsync(
        DbConnection connection, DbTransaction? transaction, string? principal, int employeeId, int equipmentId, CancellationToken ct)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
        if (actor?.Id != employeeId || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            throw new EquipmentComputerWriteRejectedException("Недостаточно прав для изменения конфигурации компьютера.");
        var context = await connection.QuerySingleOrDefaultAsync<ComputerModelContext>(new CommandDefinition(
            SQLQueries.SELECT_КомпьютерКонтекстСохранения, new { EquipmentId = equipmentId }, transaction, cancellationToken: ct));
        if (context is not { HasComputerCharacteristics: > 0, ModelId: > 0 })
            throw new EquipmentComputerWriteRejectedException("Конфигурация компьютера недоступна для фактического типа оборудования.");
        return (actor, context);
    }
}
