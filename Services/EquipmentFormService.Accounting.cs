using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Сохраняет бухгалтерскую запись после проверки прав по владельцу.</summary>
    public virtual Task SaveAccountingAsync(EquipmentAccounting accounting, int employeeId, CancellationToken ct)
    {
        var equipmentId = accounting.EquipmentId;
        var accountingId = accounting.Id;
        var ownerId = accounting.OwnerId;
        return RunAccountingWriteAsync(equipmentId, accountingId, employeeId,
            async (connection, transaction, actor, token) =>
            {
                if (!await HasAccountingOwnerAccessAsync(connection, transaction, actor, ownerId!.Value, token))
                    throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");
                if (accounting.EquipmentId != equipmentId || accounting.Id != accountingId || accounting.OwnerId != ownerId)
                    throw new EquipmentAccountingWriteRejectedException("Контекст бухгалтерского учёта изменился.");
                return await accounting.SaveEnlistedAsync(connection, transaction, token);
            }, ct, ownerId, ownerId is not > 0 ? "Выберите владельца бухгалтерского учёта." : accounting.Validate());
    }

    /// <summary>Удаляет только проверенную последнюю бухгалтерскую запись.</summary>
    public virtual Task DeleteAccountingAsync(int equipmentId, int accountingId, int employeeId, CancellationToken ct) =>
        RunAccountingWriteAsync(equipmentId, accountingId, employeeId,
            (connection, transaction, _, token) => EquipmentAccounting.DeleteLatestEnlistedAsync(
                connection, transaction, equipmentId, accountingId, token), ct);

    private async Task RunAccountingWriteAsync(int equipmentId, int accountingId, int employeeId,
        Func<DbConnection, DbTransaction, Employee, CancellationToken, Task<EquipmentAccountingWriteProof>> write,
        CancellationToken ct, int? intendedOwnerId = null, string? validation = null)
    {
        if (equipmentId <= 0 || accountingId < 0)
            throw new EquipmentAccountingWriteRejectedException("Контекст бухгалтерского учёта изменился.");
        if (validation is not null)
            throw new EquipmentAccountingWriteRejectedException(validation);

        var principal = await GetWindowsPrincipalAsync();
        var actor = RequireAccountingActor(await Employee.GetCurrentWithRolesAsync(_db, principal, ct), employeeId);
        if (intendedOwnerId is int intendedOwner && !HasAccountingRole(actor, intendedOwner))
            throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var fresh = RequireAccountingActor(await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct), employeeId);
            var proof = await write(connection, transaction, fresh, ct);
            if (proof.OriginalOwnerId is int original && !HasAccountingRole(fresh, original) ||
                proof.EffectiveOwnerId is int effective && !HasAccountingRole(fresh, effective))
                throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");
            if (proof.ConfirmedAccountingId is not > 0 || accountingId > 0 && proof.ConfirmedAccountingId != accountingId)
                throw new InvalidOperationException("База не подтвердила результат изменения бухгалтерского учёта.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

    /// <summary>Перечитывает точную строку учёта или подтверждает исходные значения нового редактора.</summary>
    public virtual async Task<EquipmentAccounting?> ReadAccountingEditorResetAsync(int equipmentId, int accountingId,
        int employeeId, int? openingOwnerId, DateTime? openingDate, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            var actor = RequireAccountingActor(await Employee.GetCurrentWithRolesAsync(connection, null, principal, ct), employeeId);
            if (accountingId == 0)
            {
                if (openingOwnerId is not > 0 ||
                    !await HasAccountingOwnerAccessAsync(connection, null, actor, openingOwnerId.Value, ct) ||
                    (await connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(
                        SQLQueries.SELECT_ID_Оборудование, new { Id = equipmentId }, cancellationToken: ct)))?.Id != equipmentId)
                    return null;
                return new EquipmentAccounting { EquipmentId = equipmentId, OwnerId = openingOwnerId, DateFrom = openingDate };
            }

            var rows = await connection.QueryAsync<EquipmentAccounting>(new CommandDefinition(
                SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования, new { EquipmentId = equipmentId }, cancellationToken: ct));
            var row = rows.SingleOrDefault(item => item.Id == accountingId && item.EquipmentId == equipmentId);
            return row?.OwnerId is > 0 && await HasAccountingOwnerAccessAsync(connection, null, actor, row.OwnerId.Value, ct)
                ? row : null;
        });
    }

    private static Employee RequireAccountingActor(Employee? actor, int employeeId) =>
        actor is not null && IsAccountingActor(actor, employeeId)
            ? actor : throw new EquipmentAccountingWriteRejectedException("Недостаточно прав для операции бухгалтерского учёта.");

    private static bool IsAccountingActor(Employee actor, int employeeId) => actor.Id == employeeId &&
        (actor.HasRole(Роли.Бухгалтер) || actor.HasRole(Роли.Главный_бухгалтер_1С));

    private static bool HasAccountingRole(Employee actor, int ownerId) =>
        actor.HasRole(Роли.Бухгалтер, EmployeeRoleScope.PersonOrGlobal, ownerId) ||
        actor.HasRole(Роли.Главный_бухгалтер_1С, EmployeeRoleScope.PersonOrGlobal, ownerId);

    private static async Task<bool> HasAccountingOwnerAccessAsync(DbConnection connection, DbTransaction? transaction, Employee actor,
        int ownerId, CancellationToken ct) => HasAccountingRole(actor, ownerId) &&
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition(SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
            new { PersonId = ownerId }, transaction, cancellationToken: ct)) == 1;
}
