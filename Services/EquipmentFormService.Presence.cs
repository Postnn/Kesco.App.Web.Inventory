using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal async Task<ClayPresenceAccess?> ReadPresenceAccessAsync(
        string login, int expectedEmployeeId, int equipmentId, CancellationToken token)
    {
        // Presence polling is independent of the business editor's DB operation and error handling.
        using var db = new DbManager(_db.ConnectionString);
        var actor = await Employee.GetCurrentWithRolesAsync(db, login, token);
        if (actor?.Id != expectedEmployeeId)
            return null;

        var equipment = await db.RunDbAsync(connection => connection.QuerySingleOrDefaultAsync<Equipment>(
            new CommandDefinition(SQLQueries.SELECT_ID_Оборудование, new { Id = equipmentId }, cancellationToken: token)));
        if (equipment?.Id != equipmentId)
            return null;

        return new(actor.Id, actor.Name, actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования)
            ? ClayPresenceActivity.Editing : ClayPresenceActivity.Viewing);
    }
}