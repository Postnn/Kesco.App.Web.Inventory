using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string InsertAccountCommand = "equipment-account.insert";
    internal const string UpdateAccountCommand = "equipment-account.update";
    internal const string DeleteAccountCommand = "equipment-account.delete";

    /// <summary>Добавляет учётную запись и подтверждает её сохранение в защищённой транзакции.</summary>
    public virtual Task AddAccountAsync(EquipmentAccount account, int employeeId, Guid attemptId, byte[] bindingNonce,
        CancellationToken ct) => RunAccountWriteAsync(account, EquipmentAccountChange.Insert, InsertAccountCommand, employeeId, attemptId, bindingNonce, ct);

    /// <summary>Обновляет подтверждённый исходный снимок учётной записи через серверный журнал.</summary>
    public virtual Task UpdateAccountAsync(EquipmentAccount account, int employeeId, Guid attemptId, byte[] bindingNonce,
        CancellationToken ct) => RunAccountWriteAsync(account, EquipmentAccountChange.Update, UpdateAccountCommand, employeeId, attemptId, bindingNonce, ct);

    /// <summary>Удаляет подтверждённый исходный снимок учётной записи через серверный журнал.</summary>
    public virtual Task DeleteAccountAsync(EquipmentAccount account, int employeeId, Guid attemptId, byte[] bindingNonce,
        CancellationToken ct) => RunAccountWriteAsync(account, EquipmentAccountChange.Delete, DeleteAccountCommand, employeeId, attemptId, bindingNonce, ct);

    private async Task RunAccountWriteAsync(EquipmentAccount account, EquipmentAccountChange change, string commandKind,
        int employeeId, Guid attemptId, byte[] bindingNonce, CancellationToken ct)
    {
        var submitting = false;
        try
        {
            if (account.EquipmentId <= 0 || attemptId == Guid.Empty || string.IsNullOrWhiteSpace(account.Login) ||
                bindingNonce is not { Length: 32 })
                throw new EquipmentAccountWriteRejectedException("Контекст учётной записи изменился.");
            var deleting = change == EquipmentAccountChange.Delete;
            var submitted = new EquipmentAccount
            {
                EquipmentId = account.EquipmentId, Login = account.Login, ExpectedVersion = account.ExpectedVersion,
                Password = deleting ? string.Empty : account.Password, LimitedRights = account.LimitedRights,
                Notes = deleting ? string.Empty : account.Notes,
            };
            if (change != EquipmentAccountChange.Insert && string.IsNullOrWhiteSpace(submitted.ExpectedVersion))
                throw new EquipmentAccountWriteRejectedException("Исходная версия учётной записи не подтверждена. Сверьте актуальные данные.");

            var binding = EquipmentAccount.AttemptBinding(submitted.EquipmentId, submitted.Login, bindingNonce);
            var principal = await GetWindowsPrincipalAsync();
            var actor = await _db.RunDbAsync(connection => RequireJournalAccountAccessAsync(connection, null, principal,
                employeeId, submitted.EquipmentId, ct));
            var attempt = AccountAttempt(actor, submitted.EquipmentId, commandKind, binding, attemptId);
            submitting = true;
            var receipt = await InventoryOperationJournal.RunProtectedSqlAsync<EquipmentAccountWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) => await submitted.WriteEnlistedAsync(connection, transaction, change, token),
                static () => new EquipmentAccountStorageException(true, true), ct,
                prepare: (connection, transaction, token) =>
                    RequireJournalAccountAccessAsync(connection, transaction, principal, employeeId, submitted.EquipmentId, token));
            if (receipt.State == InventoryOperationState.Rejected)
                throw new EquipmentAccountWriteRejectedException("Предыдущая попытка изменения учётной записи отклонена.");
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != submitted.EquipmentId)
                throw new InventoryOperationPendingException();
        }
        catch (EquipmentAccountWriteRejectedException error)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (Exception error) when (error is InventoryOperationPreparationException or
            InventoryOperationPendingException { BlockedBeforeReservation: true })
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or
            OperationCanceledException or SqlTypeException or ArgumentException)
        {
            var redacted = new EquipmentAccountStorageException(submitting, submitting);
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent,
                redacted.Message, redacted);
        }
    }

    /// <summary>Читает исход точной попытки по непрозрачной привязке, без чтения учётной записи или пароля.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadAccountWriteReceiptAsync(int equipmentId, string commandKind,
        string binding, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (equipmentId <= 0 || attemptId == Guid.Empty || !IsAccountBinding(binding) ||
            commandKind is not (InsertAccountCommand or UpdateAccountCommand or DeleteAccountCommand))
            throw new UnauthorizedAccessException("Контекст учётной записи недоступен для сверки.");
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = await RequireJournalAccountAccessAsync(connection, transaction, principal, employeeId, equipmentId, ct);
            var receipt = await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction,
                AccountAttempt(actor, equipmentId, commandKind, binding, attemptId), ct);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    internal static bool IsAccountBinding(string? binding) =>
        binding is { Length: 64 } && binding.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InventoryOperationJournal.Attempt AccountAttempt(Employee actor, int equipmentId, string commandKind,
        string binding, Guid attemptId) => InventoryOperationJournal.ForEmployee(actor, attemptId, MainEquipmentScope(equipmentId),
        commandKind, ["equipment/account-attempt/" + binding]);

    /// <summary>Читает защищённую версию учётной записи для отмены только после свежей проверки записи.</summary>
    public virtual async Task<EquipmentAccount?> ReadAccountEditorResetAsync(int equipmentId, string? originalLogin,
        int employeeId, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        try
        {
            return await _db.RunDbAsync<EquipmentAccount?>(async connection =>
            {
                try
                {
                    await RequireJournalAccountAccessAsync(connection, null, principal, employeeId, equipmentId, ct);
                    if (originalLogin is null)
                        return new EquipmentAccount { EquipmentId = equipmentId };
                    if (string.IsNullOrWhiteSpace(originalLogin) || originalLogin.Length > 50)
                        return null;

                    var row = await connection.QuerySingleOrDefaultAsync<EquipmentAccount>(new CommandDefinition(
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            SQLQueries.SELECT_ОборудованиеУчётнаяЗаписьСнимок, ""),
                        new { EquipmentId = equipmentId, Login = new DbString { Value = originalLogin, IsAnsi = true, Length = 50 } },
                        cancellationToken: ct));
                    if (row?.EquipmentId != equipmentId || !string.Equals(row.Login, originalLogin, StringComparison.Ordinal))
                        return null;
                    row.ExpectedVersion = row.CaptureVersion();
                    return row;
                }
                catch (DbException)
                {
                    throw new EquipmentAccountStorageException(false);
                }
            });
        }
        catch (DbException)
        {
            throw new EquipmentAccountStorageException(false);
        }
    }

    private static async Task<Employee> RequireJournalAccountAccessAsync(DbConnection connection, DbTransaction? transaction,
        string? principal, int employeeId, int equipmentId, CancellationToken ct)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
        if (actor?.Id != employeeId || !actor.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.Global) ||
            !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            throw new EquipmentAccountWriteRejectedException("Недостаточно прав для изменения учётных записей оборудования.");

        var type = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
            SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { EquipmentId = equipmentId }, transaction,
            cancellationToken: ct));
        if (type is not { MacAddressCount: > 0 })
            throw new EquipmentAccountWriteRejectedException("Учётные записи недоступны для фактического типа оборудования.");
        return actor;
    }
}
