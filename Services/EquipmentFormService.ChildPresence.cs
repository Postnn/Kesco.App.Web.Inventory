using System.Data.Common;
using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Dapper;
using Microsoft.Data.SqlClient;
using static Clayzor.Lib.Entities.Inventory.EquipmentChildPresenceQueries;

namespace Kesco.App.Web.Inventory.Services;

internal sealed record EquipmentChildPresence(
    ClayPresenceKey Key,
    Func<DbConnection, string, int, CancellationToken, Task<Employee?>> ReadEmployeeAsync);

/// <summary>Серверные проверки присутствия в сохранённых дочерних записях.</summary>
public partial class EquipmentFormService
{
    internal EquipmentChildPresence CreateComputerPresence(int equipmentId, DateTime date, int? expectedModelId)
    {
        var day = date.Date;
        return new EquipmentChildPresence(ClayPresenceKey.Create("inventory", "equipment-computer", equipmentId,
            day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), async (connection, login, employeeId, ct) =>
        {
            var (actor, context) = await ReadComputerJournalContextAsync(connection, null, login, employeeId, equipmentId, ct);
            return expectedModelId is > 0 && context.ModelId == expectedModelId &&
                   await HasChildPresenceRowAsync(connection, ComputerPresenceRow, new { EquipmentId = equipmentId, Date = day }, ct)
                ? actor : null;
        });
    }

    internal EquipmentChildPresence CreateLocationPresence(int equipmentId, DateTime fromUtc)
    {
        var start = EquipmentPeriodJournalWrite.NormalizeStartUtc(fromUtc);
        return CreateInventoryChildPresence(ClayPresenceKey.Create("inventory", "equipment-location-period", equipmentId, start.Ticks),
            (connection, ct) => HasChildPresenceRowAsync(connection, LocationPresenceRow,
                new { EquipmentId = equipmentId, FromUtc = start }, ct));
    }

    internal EquipmentChildPresence CreateResponsiblePresence(int equipmentId, DateTime fromUtc, bool requireActive)
    {
        var start = EquipmentPeriodJournalWrite.NormalizeStartUtc(fromUtc);
        return CreateInventoryChildPresence(ClayPresenceKey.Create("inventory", "equipment-responsible-period", equipmentId, start.Ticks),
            (connection, ct) => HasChildPresenceRowAsync(connection, ResponsiblePresenceRow,
                new { EquipmentId = equipmentId, FromUtc = start, RequireActive = requireActive }, ct));
    }

    internal EquipmentChildPresence CreateSoftwarePresence(int equipmentId, int id) =>
        CreateInventoryChildPresence(ClayPresenceKey.Create("inventory", "equipment-software", equipmentId, id),
            async (connection, ct) =>
            {
                if (id <= 0)
                    return false;
                var actualType = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
                    SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { EquipmentId = equipmentId }, cancellationToken: ct));
                return actualType is { HasComputerCharacteristics: > 0 } &&
                    await HasChildPresenceRowAsync(connection, SoftwarePresenceRow, new { EquipmentId = equipmentId, Id = id }, ct);
            });

    internal EquipmentChildPresence CreateAccountingPresence(int equipmentId, int id) =>
        new EquipmentChildPresence(ClayPresenceKey.Create("inventory", "equipment-accounting", equipmentId, id),
            async (connection, login, employeeId, ct) =>
            {
                if (id <= 0)
                    return null;
                var actor = await Employee.GetCurrentWithRolesAsync(connection, null, login, ct);
                if (actor?.Id != employeeId)
                    return null;
                var ownerId = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                    AccountingPresenceOwner, new { EquipmentId = equipmentId, Id = id }, cancellationToken: ct));
                if (ownerId is not > 0 || !HasAccountingRole(actor, ownerId.Value))
                    return null;

                return await HasChildPresenceRowAsync(connection, SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
                    new { PersonId = ownerId.Value }, ct) ? actor : null;
            });

    internal EquipmentChildPresence CreateLicensePresence(int equipmentId) =>
        new EquipmentChildPresence(ClayPresenceKey.Create("inventory", "equipment-license", equipmentId),
            async (connection, login, employeeId, ct) =>
            {
                var actor = await RequireLicenseAccessAsync(connection, null, login, employeeId, equipmentId, ct);
                return await HasChildPresenceRowAsync(connection, LicensePresenceRow, new { EquipmentId = equipmentId }, ct)
                    ? actor : null;
            });

    private static EquipmentChildPresence CreateInventoryChildPresence(ClayPresenceKey key,
        Func<DbConnection, CancellationToken, Task<bool>> canEdit) =>
        new EquipmentChildPresence(key, async (connection, login, employeeId, ct) =>
        {
            var actor = await Employee.GetCurrentWithRolesAsync(connection, null, login, ct);
            if (actor?.Id != employeeId || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                return null;
            return await canEdit(connection, ct) ? actor : null;
        });

    internal async Task<ClayPresenceAccess?> ReadChildPresenceAccessAsync(EquipmentChildPresence presence,
        string login, int employeeId, CancellationToken ct)
    {
        using var db = new DbManager(_db.ConnectionString);
        return await ReadChildPresenceAccessAsync(db, presence, login, employeeId, ct);
    }

    internal static async Task<ClayPresenceAccess?> ReadChildPresenceAccessAsync(DbManager db, EquipmentChildPresence presence,
        string login, int employeeId, CancellationToken ct)
    {
        if (employeeId <= 0 || string.IsNullOrWhiteSpace(login))
            return null;

        try
        {
            var actor = await db.RunDbAsync(connection => presence.ReadEmployeeAsync(connection, login, employeeId, ct));
            ct.ThrowIfCancellationRequested();
            return actor?.Id == employeeId ? new(actor.Id, actor.Name, ClayPresenceActivity.Editing) : null;
        }
        catch (Exception error) when (error is EquipmentComputerWriteRejectedException or EquipmentLicenseWriteRejectedException
            or EquipmentSimWriteRejectedException or EquipmentAccountWriteRejectedException or UpsWriteRejectedException
            or SqlException { Number: 229 or 230 })
        {
            return null;
        }
    }

    private static async Task<bool> HasChildPresenceRowAsync(DbConnection connection, string query, object parameters,
        CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<int>(new CommandDefinition(query, parameters, cancellationToken: ct)) == 1;
}
