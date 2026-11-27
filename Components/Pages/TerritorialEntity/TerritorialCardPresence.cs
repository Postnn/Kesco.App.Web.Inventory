using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Подтверждает присутствие в карточках территории и телефонного кода по данным Inventory.</summary>
internal static class TerritorialCardPresence
{
    internal static ClayPresenceTarget? Territory(DbManager db, int employeeId, int id, bool editing, Func<bool> current) =>
        Create(db, employeeId, ClayPresenceKey.Create("inventory", "territory", id), editing, current,
            async (connection, token) => (await TerritorialEntityData.ReadTerritoryAsync(connection, id, token))?.Id == id,
            access => access.TerritoryUpdate);

    internal static ClayPresenceTarget? PhoneCode(DbManager db, int employeeId, string code, bool editing, Func<bool> current)
    {
        ClayPresenceKey key;
        try { key = ClayPresenceKey.Create("inventory", "phone-code", code); }
        catch (ArgumentException) { return null; }
        return Create(db, employeeId, key, editing, current,
            async (connection, token) => string.Equals(
                (await TerritorialEntityData.ReadPhoneAsync(connection, code, token))?.Code, code, StringComparison.Ordinal),
            access => access.PhoneUpdate);
    }

    private static ClayPresenceTarget? Create(DbManager db, int employeeId, ClayPresenceKey key, bool editing, Func<bool> current,
        Func<DbManager, CancellationToken, Task<bool>> canReadExact,
        Func<TerritorialEntityAccess, bool> canEdit)
    {
        var connectionString = db.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        return new(key, async (principal, token) =>
        {
            var identity = principal.Identity;
            if (!current() || identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(identity.Name))
                return null;

            // Independently recheck presence without raising page-level business error banners.
            using var connection = new DbManager(connectionString);
            var actor = await Employee.GetCurrentWithRolesAsync(connection, identity.Name, token);
            if (!current() || actor is null || actor.Id != employeeId || !await canReadExact(connection, token))
                return null;

            var access = await TerritorialEntityData.ReadAccessAsync(connection, actor, token);
            token.ThrowIfCancellationRequested();
            if (!current()) return null;
            return new(actor.Id, actor.Name,
                editing && canEdit(access) ? ClayPresenceActivity.Editing : ClayPresenceActivity.Viewing);
        });
    }
}
