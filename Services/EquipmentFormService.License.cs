using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Записывает лицензию после проверки фактических правил её типа.</summary>
    public virtual async Task SaveLicenseAsync(EquipmentLicense license, LicenseType? type, int employeeId,
        CancellationToken ct)
    {
        var equipmentId = license.EquipmentId;
        var expectedExisting = license.ExpectedExisting;
        if (equipmentId <= 0)
            throw new EquipmentLicenseWriteRejectedException("Не удалось подтвердить контекст лицензии.");
        if (license.Validate(type) is { } validation)
            throw new EquipmentLicenseWriteRejectedException(validation);
        var licenseTypeId = type!.Id;
        var wasTemporary = type.Temporary > 0;
        var wasResale = type.Resale > 0;

        var principal = await GetWindowsPrincipalAsync();
        await _db.RunDbAsync(connection => RequireLicenseAccessAsync(connection, null,
            principal, employeeId, equipmentId, ct));
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await RequireLicenseAccessAsync(connection, transaction, principal, employeeId, equipmentId, ct);
            var actual = await connection.QuerySingleOrDefaultAsync<LicenseType>(new CommandDefinition(
                SQLQueries.SELECT_ID_ТипЛицензирования, new { Id = licenseTypeId }, transaction, cancellationToken: ct));
            if (actual?.Id != licenseTypeId || (actual.Temporary > 0) != wasTemporary || (actual.Resale > 0) != wasResale)
                throw new EquipmentLicenseWriteRejectedException("Правила типа лицензии изменились. Обновите данные.");
            if (license.EquipmentId != equipmentId || license.ExpectedExisting != expectedExisting)
                throw new EquipmentLicenseWriteRejectedException("Контекст лицензии изменился.");
            if (license.Validate(actual) is { } currentValidation)
                throw new EquipmentLicenseWriteRejectedException(currentValidation);

            var resultId = await license.SaveEnlistedAsync(connection, transaction, ct);
            if (resultId != equipmentId)
                throw new InvalidOperationException("База не подтвердила сохранение лицензии.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

    /// <summary>Перечитывает существующую лицензию или возвращает проверенное начальное состояние новой.</summary>
    public virtual async Task<EquipmentLicense?> ReadLicenseEditorResetAsync(int equipmentId, bool expectedExisting,
        int employeeId, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await RequireLicenseAccessAsync(connection, null, principal, employeeId, equipmentId, ct);
            var rows = await connection.QueryAsync<EquipmentLicense>(new CommandDefinition(
                SQLQueries.SELECT_Soft_КодОборудования, new { EquipmentId = equipmentId }, cancellationToken: ct));
            var row = rows.SingleOrDefault(item => item.EquipmentId == equipmentId);
            if (!expectedExisting)
                return row is null ? new EquipmentLicense { EquipmentId = equipmentId } : null;
            if (row is not null)
                row.ExpectedExisting = true;
            return row;
        });
    }

    private static async Task<Employee> RequireLicenseAccessAsync(DbConnection connection, DbTransaction? transaction,
        string? principal, int employeeId, int equipmentId, CancellationToken ct)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
        if (actor?.Id != employeeId || !actor.HasRole(Роли.Специалист_по_инвентаризации_по))
            throw new EquipmentLicenseWriteRejectedException("Недостаточно прав для изменения лицензии.");

        var equipment = await connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(SQLQueries.SELECT_ID_Оборудование,
            new { Id = equipmentId }, transaction, cancellationToken: ct));
        if (equipment?.Id != equipmentId || equipment.EquipmentTypeId is not > 0 ||
            !actor.HasRole(Роли.Специалист_по_инвентаризации_по, EmployeeRoleScope.PersonOrGlobal, equipment.OwnerId) &&
            !(equipment.TenantId is > 0 && equipment.TenantId != equipment.OwnerId &&
              actor.HasRole(Роли.Специалист_по_инвентаризации_по, EmployeeRoleScope.PersonOrGlobal, equipment.TenantId)))
            throw new EquipmentLicenseWriteRejectedException("Недостаточно прав по владельцу или арендатору оборудования.");

        var actualType = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
            SQLQueries.SELECT_ТипОборудованияПоИдентификатору, new { Id = equipment.EquipmentTypeId.Value }, transaction,
            cancellationToken: ct));
        if (actualType is not { HasSoftwareCharacteristics: > 0 })
            throw new EquipmentLicenseWriteRejectedException("Лицензия недоступна для фактического типа оборудования.");
        return actor;
    }
}
