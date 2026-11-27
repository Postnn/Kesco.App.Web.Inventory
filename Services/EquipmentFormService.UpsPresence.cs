using Clayzor.Lib.Web.Controls.Components.Presence;
using static Clayzor.Lib.Entities.Inventory.EquipmentChildPresenceQueries;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Серверная проверка присутствия в редакторе ИБП и его сохранённых тестов.</summary>
public partial class EquipmentFormService
{
    internal EquipmentChildPresence? CreateUpsPresence(int upsEquipmentId, int serverEquipmentId) =>
        CanUseUpsPresence(upsEquipmentId, serverEquipmentId)
            ? CreateUpsPresence(ClayPresenceKey.Create("inventory", "equipment", upsEquipmentId), upsEquipmentId, serverEquipmentId)
            : null;

    internal EquipmentChildPresence? CreateUpsTestPresence(int upsEquipmentId, int serverEquipmentId, int testId) =>
        testId > 0 && CanUseUpsPresence(upsEquipmentId, serverEquipmentId)
            ? CreateUpsPresence(ClayPresenceKey.Create("inventory", "ups-test", upsEquipmentId, testId),
                upsEquipmentId, serverEquipmentId, testId)
            : null;

    private static bool CanUseUpsPresence(int upsEquipmentId, int serverEquipmentId) =>
        upsEquipmentId > 0 && serverEquipmentId > 0 && upsEquipmentId != serverEquipmentId;

    private static EquipmentChildPresence CreateUpsPresence(ClayPresenceKey key, int upsEquipmentId,
        int serverEquipmentId, int? testId = null) =>
        new(key, async (connection, login, employeeId, token) =>
        {
            var actor = await RequireUpsAccessAsync(connection, null, login, employeeId, upsEquipmentId, serverEquipmentId, token);
            if (!await HasChildPresenceRowAsync(connection, UpsLinkPresenceRow,
                    new { UpsEquipmentId = upsEquipmentId, ServerEquipmentId = serverEquipmentId }, token))
                return null;

            return testId is null || await HasChildPresenceRowAsync(connection, UpsTestPresenceRow,
                    new { UpsEquipmentId = upsEquipmentId, Id = testId.Value }, token)
                ? actor : null;
        });
}
