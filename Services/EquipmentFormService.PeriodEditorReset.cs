using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal sealed record PeriodEditorReset(
        IReadOnlyList<EquipmentLocationPeriod> Locations, IReadOnlyList<EquipmentResponsiblePeriod> Responsibles);

    internal virtual async Task<PeriodEditorReset?> ReadPeriodEditorResetAsync(int equipmentId, int employeeId, CancellationToken ct)
    {
        await RequireMainActorAsync(await GetWindowsPrincipalAsync(), employeeId, ct);
        var equipment = (await _db.QueryRequiredAsync<Equipment>(SQLQueries.SELECT_ID_Оборудование,
            new { Id = equipmentId }, ct)).SingleOrDefault();
        if (equipment?.Id != equipmentId) return null;

        var locations = (await _db.QueryRequiredAsync<EquipmentLocationPeriod>(SQLQueries.SELECT_ОборудованиеРасположение,
            new { EquipmentId = equipmentId }, ct)).ToList();
        var responsibles = (await _db.QueryRequiredAsync<EquipmentResponsiblePeriod>(SQLQueries.SELECT_ОборудованиеСотрудники,
            new { EquipmentId = equipmentId }, ct)).ToList();
        return new(locations, responsibles);
    }
}
