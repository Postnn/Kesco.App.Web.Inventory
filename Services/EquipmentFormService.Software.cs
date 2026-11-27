using System.Data;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Перечитывает исходные данные установки ПО после проверки сотрудника и фактического типа.</summary>
    public virtual async Task<EquipmentSoftwareInstallation?> ReadSoftwareEditorResetAsync(int equipmentId, int installationId,
        int employeeId, CancellationToken ct)
    {
        await RequireMainActorAsync(await GetWindowsPrincipalAsync(), employeeId, ct);
        var type = (await _db.QueryRequiredAsync<EquipmentType>(SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей,
            new { EquipmentId = equipmentId }, ct)).SingleOrDefault();
        if (type is not { HasComputerCharacteristics: > 0 }) return null;
        if (installationId == 0) return new EquipmentSoftwareInstallation { EquipmentId = equipmentId };

        var rows = await _db.QueryRequiredAsync<EquipmentSoftwareInstallation>(SQLQueries.SELECT_ПО_КодОборудования,
            new { EquipmentId = equipmentId }, ct);
        return rows.SingleOrDefault(row => row.EquipmentId == equipmentId && row.Id == installationId);
    }

    /// <summary>Записывает установку ПО в транзакции с повторной проверкой полномочий.</summary>
    public virtual async Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation, int employeeId,
        CancellationToken ct)
    {
        if (installation.EquipmentId <= 0 || installation.Id < 0)
            throw new EquipmentSoftwareWriteRejectedException("Не удалось подтвердить контекст установки ПО.");
        if (installation.Validate() is { } validation)
            throw new EquipmentSoftwareWriteRejectedException(validation);

        var principal = await GetWindowsPrincipalAsync();
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var fresh = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
            if (fresh?.Id != employeeId || !fresh.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                throw new EquipmentSoftwareWriteRejectedException("Недостаточно прав для изменения установленного ПО.");

            var type = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
                SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { installation.EquipmentId },
                transaction, cancellationToken: ct));
            if (type is not { HasComputerCharacteristics: > 0 })
                throw new EquipmentSoftwareWriteRejectedException("Установка ПО недоступна для фактического типа оборудования.");

            var softwareId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                SQLQueries.SELECT_ПрограммноеОборудованиеПоИдентификатору, new { Id = installation.SoftwareId },
                transaction, cancellationToken: ct));
            if (softwareId is not > 0 || softwareId != installation.SoftwareId)
                throw new EquipmentSoftwareWriteRejectedException("Выбранное программное обеспечение недоступно.");

            var id = await installation.SaveEnlistedAsync(connection, transaction, ct);
            if (id <= 0 || installation.Id > 0 && id != installation.Id)
                throw new InvalidOperationException("База данных не подтвердила установку ПО.");
            await transaction.CommitAsync(CancellationToken.None);
            return id;
        });
    }
}
