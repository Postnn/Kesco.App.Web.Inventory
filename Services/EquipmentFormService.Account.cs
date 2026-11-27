using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Добавляет учётную запись в защищённой транзакции.</summary>
    public virtual Task AddAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
        RunAccountWriteAsync(account, EquipmentAccountChange.Insert, employeeId, ct);

    /// <summary>Обновляет подтверждённый исходный снимок учётной записи.</summary>
    public virtual Task UpdateAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
        RunAccountWriteAsync(account, EquipmentAccountChange.Update, employeeId, ct);

    /// <summary>Удаляет подтверждённый исходный снимок учётной записи.</summary>
    public virtual Task DeleteAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
        RunAccountWriteAsync(account, EquipmentAccountChange.Delete, employeeId, ct);

    private async Task RunAccountWriteAsync(EquipmentAccount account, EquipmentAccountChange change,
        int employeeId, CancellationToken ct)
    {
        if (account.EquipmentId <= 0 || string.IsNullOrWhiteSpace(account.Login))
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

        var principal = await GetWindowsPrincipalAsync();
        await _db.RunDbAsync(connection => RequireAccountAccessAsync(connection, null, principal,
            employeeId, submitted.EquipmentId, ct));
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await RequireAccountAccessAsync(connection, transaction, principal, employeeId, submitted.EquipmentId, ct);
            var resultId = await submitted.WriteEnlistedAsync(connection, transaction, change, ct);
            if (resultId != submitted.EquipmentId)
                throw new InvalidOperationException("База не подтвердила изменение учётной записи.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

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
                    await RequireAccountAccessAsync(connection, null, principal, employeeId, equipmentId, ct);
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

    private static async Task<Employee> RequireAccountAccessAsync(DbConnection connection, DbTransaction? transaction,
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
