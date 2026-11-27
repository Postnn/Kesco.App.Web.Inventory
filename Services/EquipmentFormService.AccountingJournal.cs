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
    internal const string InsertAccountingCommand = "equipment-accounting.insert";
    internal const string UpdateAccountingCommand = "equipment-accounting.update";
    internal const string DeleteAccountingCommand = "equipment-accounting.delete";

    /// <summary>Сохраняет бухгалтерскую запись и серверное подтверждение её ключа и владельцев.</summary>
    public virtual Task SaveAccountingAsync(EquipmentAccounting accounting, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var equipmentId = accounting.EquipmentId;
        var accountingId = accounting.Id;
        var ownerId = accounting.OwnerId;
        return RunAccountingWriteAsync(equipmentId, accountingId,
            accountingId == 0 ? InsertAccountingCommand : UpdateAccountingCommand, employeeId, attemptId,
            async (connection, transaction, actor, token) =>
            {
                if (!await HasAccountingOwnerAccessAsync(connection, transaction, actor, ownerId!.Value, token))
                    throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");
                if (accounting.EquipmentId != equipmentId || accounting.Id != accountingId || accounting.OwnerId != ownerId)
                    throw new EquipmentAccountingWriteRejectedException("Контекст бухгалтерского учёта изменился.");
                return await accounting.SaveEnlistedAsync(connection, transaction, token);
            }, ct, ownerId, ownerId is not > 0 ? "Выберите владельца бухгалтерского учёта." : accounting.Validate());
    }

    /// <summary>Удаляет только проверенную последнюю бухгалтерскую запись и сохраняет её исходного владельца.</summary>
    public virtual Task DeleteAccountingAsync(int equipmentId, int accountingId, int employeeId, Guid attemptId, CancellationToken ct) =>
        RunAccountingWriteAsync(equipmentId, accountingId, DeleteAccountingCommand, employeeId, attemptId,
            (connection, transaction, _, token) => EquipmentAccounting.DeleteLatestEnlistedAsync(
                connection, transaction, equipmentId, accountingId, token), ct);

    private async Task RunAccountingWriteAsync(int equipmentId, int accountingId, string commandKind, int employeeId,
        Guid attemptId, Func<DbConnection, DbTransaction, Employee, CancellationToken, Task<EquipmentAccountingWriteProof>> write,
        CancellationToken ct, int? intendedOwnerId = null, string? validation = null)
    {
        var submitting = false;
        try
        {
            if (!ValidAccountingIdentity(equipmentId, accountingId, commandKind, attemptId))
                throw new EquipmentAccountingWriteRejectedException("Контекст бухгалтерского учёта изменился.");
            if (validation is not null)
                throw new EquipmentAccountingWriteRejectedException(validation);

            var principal = await GetWindowsPrincipalAsync();
            var actor = RequireAccountingActor(await Employee.GetCurrentWithRolesAsync(_db, principal, ct), employeeId);
            if (intendedOwnerId is int intendedOwner && !HasAccountingRole(actor, intendedOwner))
                throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");
            var attempt = AccountingAttempt(actor, equipmentId, accountingId, commandKind, attemptId);
            submitting = true;
            var applied = false;
            var receipt = await InventoryOperationJournal.RunSqlAsync<EquipmentAccountingWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) =>
                {
                    var fresh = RequireAccountingActor(await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, token), employeeId);
                    var proof = await write(connection, transaction, fresh, token);
                    if (proof.OriginalOwnerId is int original && !HasAccountingRole(fresh, original) ||
                        proof.EffectiveOwnerId is int effective && !HasAccountingRole(fresh, effective))
                        throw new EquipmentAccountingWriteRejectedException("Недостаточно прав по владельцу бухгалтерского учёта.");

                    var owners = FormattableString.Invariant($"{proof.OriginalOwnerId ?? 0}/{proof.EffectiveOwnerId ?? 0}");
                    await InventoryOperationJournal.WriteTextResultAsync(connection, transaction, attemptId, owners, token);
                    applied = true;
                    return proof.ConfirmedAccountingId;
                }, ct);
            if (receipt.State == InventoryOperationState.Rejected)
                throw new EquipmentAccountingWriteRejectedException("Предыдущая попытка изменения бухгалтерского учёта отклонена.");
            if (!ConfirmedAccountingResult(receipt, accountingId))
                throw new InventoryOperationPendingException();
            if (!applied && await ReadAccountingWriteReceiptAsync(equipmentId, accountingId, commandKind, employeeId, attemptId, ct) != receipt)
                throw new InventoryOperationPendingException();
        }
        catch (EquipmentAccountingWriteRejectedException error)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (InventoryOperationPendingException error) when (error.BlockedBeforeReservation)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or
            OperationCanceledException or UnauthorizedAccessException or SqlTypeException)
        {
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent,
                error.Message, error);
        }
    }

    /// <summary>Читает подтверждение исходной попытки и заново проверяет права по принятым сервером владельцам.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadAccountingWriteReceiptAsync(int equipmentId, int accountingId,
        string commandKind, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (!ValidAccountingIdentity(equipmentId, accountingId, commandKind, attemptId))
            throw new UnauthorizedAccessException("Контекст бухгалтерского учёта недоступен для сверки.");
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
            if (actor is null || !IsAccountingActor(actor, employeeId))
                throw new UnauthorizedAccessException("Недостаточно прав для сверки бухгалтерского учёта.");
            var equipment = await connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(
                SQLQueries.SELECT_ID_Оборудование, new { Id = equipmentId }, transaction, cancellationToken: ct));
            if (equipment?.Id != equipmentId) return null;

            var attempt = AccountingAttempt(actor, equipmentId, accountingId, commandKind, attemptId);
            var receipt = await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction, attempt, ct);
            if (receipt?.State == InventoryOperationState.Confirmed)
            {
                if (!ConfirmedAccountingResult(receipt, accountingId))
                    throw new InventoryOperationPendingException();
                await RequireAccountingReceiptOwnersAsync(connection, transaction, actor, commandKind, attemptId, ct);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    private static async Task RequireAccountingReceiptOwnersAsync(DbConnection connection, DbTransaction transaction,
        Employee actor, string commandKind, Guid attemptId, CancellationToken ct)
    {
        var text = await InventoryOperationJournal.ReadTextResultInTransactionAsync(connection, transaction, attemptId, ct);
        if (text?.Split('/') is not [var previous, var accepted] ||
            !int.TryParse(previous, NumberStyles.None, CultureInfo.InvariantCulture, out var originalOwner) ||
            !int.TryParse(accepted, NumberStyles.None, CultureInfo.InvariantCulture, out var effectiveOwner) ||
            (originalOwner == 0) != (commandKind == InsertAccountingCommand) ||
            (effectiveOwner == 0) != (commandKind == DeleteAccountingCommand))
            throw new InventoryOperationPendingException();

        if (originalOwner > 0 && !await HasAccountingOwnerAccessAsync(connection, transaction, actor, originalOwner, ct) ||
            effectiveOwner > 0 && effectiveOwner != originalOwner &&
            !await HasAccountingOwnerAccessAsync(connection, transaction, actor, effectiveOwner, ct))
            throw new UnauthorizedAccessException("Недостаточно прав по владельцу бухгалтерского учёта.");
    }

    private static bool ValidAccountingIdentity(int equipmentId, int accountingId, string commandKind, Guid attemptId) =>
        equipmentId > 0 && attemptId != Guid.Empty && (commandKind switch
        {
            InsertAccountingCommand => accountingId == 0,
            UpdateAccountingCommand or DeleteAccountingCommand => accountingId > 0,
            _ => false,
        });

    private static InventoryOperationJournal.Attempt AccountingAttempt(Employee actor, int equipmentId, int accountingId,
        string commandKind, Guid attemptId) => InventoryOperationJournal.ForEmployee(actor, attemptId,
        MainEquipmentScope(equipmentId), commandKind,
        accountingId == 0 ? [] : ["equipment/accounting/" + accountingId.ToString(CultureInfo.InvariantCulture)]);

    private static bool ConfirmedAccountingResult(InventoryOperationReceipt receipt, int accountingId) =>
        receipt.State == InventoryOperationState.Confirmed && receipt.ResultEntityId is > 0 &&
        (accountingId == 0 || receipt.ResultEntityId == accountingId);

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
