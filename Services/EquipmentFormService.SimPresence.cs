using Clayzor.Lib.Web.Controls.Components.Presence;
using static Clayzor.Lib.Entities.Inventory.EquipmentChildPresenceQueries;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Серверная проверка присутствия в сохранённых записях SIM.</summary>
public partial class EquipmentFormService
{
    internal EquipmentChildPresence? CreateSimPresence(int equipmentId) =>
        equipmentId > 0
            ? CreateSimPresence(ClayPresenceKey.Create("inventory", "equipment-sim", equipmentId),
                equipmentId, SimPresenceRow, new { EquipmentId = equipmentId })
            : null;

    internal EquipmentChildPresence? CreateSimServicePresence(int equipmentId, int id) =>
        equipmentId > 0 && id > 0
            ? CreateSimPresence(ClayPresenceKey.Create("inventory", "equipment-sim-service", id),
                equipmentId, SimServicePresenceRow, new { EquipmentId = equipmentId, Id = id })
            : null;

    private static EquipmentChildPresence CreateSimPresence(ClayPresenceKey key, int equipmentId, string query, object parameters) =>
        new(key, async (connection, login, employeeId, token) =>
        {
            var actor = await RequireSimJournalAccessAsync(connection, null, login, employeeId, equipmentId, token);
            return await HasChildPresenceRowAsync(connection, query, parameters, token) ? actor : null;
        });
}
