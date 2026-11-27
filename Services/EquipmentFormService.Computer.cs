using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Сохраняет дневной снимок конфигурации компьютера.</summary>
    public virtual async Task SaveComputerAsync(EquipmentComputerSnapshot snapshot, DateTime? businessToday,
        int? expectedModelId, int employeeId, CancellationToken ct)
    {
        var equipmentId = snapshot.EquipmentId;
        var date = snapshot.Date.Date;
        var existing = snapshot.ExpectedExisting;
        var changedAt = snapshot.ChangedAt;
        if (equipmentId <= 0 || date == default || expectedModelId is not > 0)
            throw new EquipmentComputerWriteRejectedException("Не удалось подтвердить дату и модель снимка компьютера.");
        if (existing && changedAt == default)
            throw new EquipmentComputerWriteRejectedException("Не удалось подтвердить исходную версию снимка компьютера.");
        if (businessToday is null)
            throw new EquipmentComputerWriteRejectedException("Актуальная календарная дата клиента не задана. Сохранение конфигурации отменено.");

        var principal = await GetWindowsPrincipalAsync();
        var (_, context) = await _db.RunDbAsync(connection => ReadComputerContextAsync(connection,
            null, principal, employeeId, equipmentId, ct));
        ValidateComputerSnapshot(snapshot, businessToday.Value, expectedModelId.Value, context);
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var (_, fresh) = await ReadComputerContextAsync(connection, transaction, principal, employeeId, equipmentId, ct);
            if (snapshot.EquipmentId != equipmentId || snapshot.Date.Date != date ||
                snapshot.ExpectedExisting != existing || snapshot.ChangedAt != changedAt)
                throw new EquipmentComputerWriteRejectedException("Исходный снимок компьютера изменился.");
            ValidateComputerSnapshot(snapshot, businessToday.Value, expectedModelId.Value, fresh);
            var resultId = await snapshot.SaveEnlistedAsync(connection, transaction, ct);
            if (resultId != equipmentId)
                throw new InvalidOperationException("База не подтвердила сохранение конфигурации компьютера.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

    private static void ValidateComputerSnapshot(EquipmentComputerSnapshot snapshot, DateTime day,
        int expectedModelId, ComputerModelContext context)
    {
        if (context.ModelId != expectedModelId)
            throw new EquipmentComputerWriteRejectedException("Модель компьютера изменилась. Откройте редактор заново.");
        if (snapshot.ValidateConfiguration(day, context.MemoryBanks) is { } validation)
            throw new EquipmentComputerWriteRejectedException(validation);
    }

    /// <summary>Проверяет актуальную модель и читает строку исходного календарного дня для сброса редактора.</summary>
    public virtual async Task<(int MemoryBanks, EquipmentComputerSnapshot? Snapshot)?> ReadComputerEditorResetAsync(
        int equipmentId, int employeeId, int? expectedModelId, DateTime day, bool expectedExisting, CancellationToken ct)
    {
        if (equipmentId <= 0 || expectedModelId is not > 0 || day == default)
            return null;

        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync<(int MemoryBanks, EquipmentComputerSnapshot? Snapshot)?>(async connection =>
        {
            var (_, actual) = await ReadComputerContextAsync(connection, null, principal, employeeId, equipmentId, ct);
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

    private static async Task<(Employee Actor, ComputerModelContext Context)> ReadComputerContextAsync(
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
