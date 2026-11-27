using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Подтверждает присутствие по сохранённому строковому ключу ТфОП или FMTN.</summary>
internal static class PhoneNumberCardPresence
{
    internal static ClayPresenceTarget? NumPool(DbManager db, int employeeId, string number, bool editing, Func<bool> current) =>
        Create(db, employeeId, "num-pool", number, editing, current, async (connection, actor, token) =>
        {
            var access = await NumPoolData.ReadAccessAsync(connection, actor, token);
            if (!access.CanRead) return null;
            var saved = await NumPoolData.ReadAsync(connection, number, token);
            return saved?.Header.Number == number ? access.CanUpdate : null;
        });

    internal static ClayPresenceTarget? SimFmtn(DbManager db, int employeeId, string prefix, bool editing, Func<bool> current) =>
        Create(db, employeeId, "sim-fmtn", prefix, editing, current, async (connection, actor, token) =>
        {
            var access = await SimFmtnData.ReadAccessAsync(connection, actor, token);
            if (!access.CanRead) return null;
            var saved = await SimFmtnData.ReadAsync(connection, prefix, token);
            return saved?.Prefix == prefix ? access.CanUpdate : null;
        });

    private static ClayPresenceTarget? Create(DbManager db, int employeeId, string entity, string key, bool editing, Func<bool> current,
        Func<DbManager, Employee, CancellationToken, Task<bool?>> readExactWriteAccess)
    {
        var connectionString = db.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        ClayPresenceKey targetKey;
        try { targetKey = ClayPresenceKey.Create("inventory", entity, key); }
        catch (ArgumentException) { return null; }

        return new(targetKey, async (principal, token) =>
        {
            var identity = principal.Identity;
            if (!current() || identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(identity.Name))
                return null;

            // Keep heartbeat failures independent of the form's business error handler.
            using var connection = new DbManager(connectionString);
            var actor = await Employee.GetCurrentWithRolesAsync(connection, identity.Name, token);
            if (!current() || actor is null || actor.Id != employeeId) return null;
            var mayEdit = await readExactWriteAccess(connection, actor, token);
            token.ThrowIfCancellationRequested();
            if (!current() || !mayEdit.HasValue) return null;
            return new(actor.Id, actor.Name, editing && mayEdit.Value ? ClayPresenceActivity.Editing : ClayPresenceActivity.Viewing);
        });
    }
}
