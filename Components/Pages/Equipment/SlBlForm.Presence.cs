using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Показывает присутствие в доступном для записи почтовом списке.</summary>
public partial class SlBlForm
{
    private (Guid Instance, int EmployeeId, ClayPresenceTarget Target)? _presence;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (_disposed || _editorClosed || !_available || _lists is null ||
            _address is not { Length: > 0 } address || _kind is not { } kind ||
            CurrentUser is not { Id: > 0 } employee ||
            Services.GetService<ISlBlMailboxWritePolicy>() is not { } policy)
        {
            _presence = null;
            return null;
        }

        var instance = _draftInstance;
        var employeeId = employee.Id;
        if (_presence is { } current && current.Instance == instance && current.EmployeeId == employeeId)
            return current.Target;

        var addressHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToUpperInvariant())));
        var key = ClayPresenceKey.Create("inventory", "slbl-list", addressHash, 1 + (byte)kind);
        var connectionString = Db.ConnectionString;
        ClayPresenceTarget? target = null;
        target = new ClayPresenceTarget(key, async (principal, token) =>
        {
            bool Current() => ReferenceEquals(_presence?.Target, target) && _available && !_editorClosed &&
                IsCurrentListEditor(employeeId, address, kind, instance, token);
            if (!Current())
                return null;

            using var connection = new DbManager(connectionString);
            var access = await AuthorizePresenceAsync(connection, policy, principal, employeeId, address, kind, token);
            return Current() ? access : null;
        });
        _presence = (instance, employeeId, target);
        return target;
    }

    internal static async Task<ClayPresenceAccess?> AuthorizePresenceAsync(DbManager db, ISlBlMailboxWritePolicy policy,
        ClaimsPrincipal principal, int employeeId, string address, SlBlKind kind, CancellationToken token)
    {
        if (principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
            return null;

        var actor = await Employee.GetCurrentAsync(db, login, token);
        if (actor?.Id != employeeId || !await policy.AllowsAsync(actor, address, kind, token) ||
            await SlBlData.FindAddressAsync(db, address, token) != address)
            return null;

        token.ThrowIfCancellationRequested();
        return new(actor.Id, actor.Name, ClayPresenceActivity.Editing);
    }
}
