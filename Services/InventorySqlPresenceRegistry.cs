using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Services;

internal sealed class InventorySqlPresenceRegistry(DbManager db, ClayPresenceOptions options) :
    IClayPresenceRegistry, IDisposable
{
    /// <inheritdoc />
    public async ValueTask<ClayPresenceSnapshot?> HeartbeatAsync(ClayPresenceKey key, ClayPresenceAccess access,
        Guid? lease, CancellationToken cancellationToken)
    {
        if (access.EmployeeId <= 0 || string.IsNullOrWhiteSpace(access.DisplayName) ||
            access.DisplayName.Length > 256 || !Enum.IsDefined(access.Activity) || lease == Guid.Empty)
            throw new ArgumentException("Некорректный контекст присутствия.", nameof(access));

        var token = lease ?? Guid.NewGuid();
        var lifetime = checked((int)Math.Ceiling(options.LeaseLifetime.TotalMilliseconds));
        var heartbeat = new InventoryPresenceData.Heartbeat(token, lease.HasValue, EntityHash(key),
            access.EmployeeId, access.DisplayName, (byte)access.Activity, lifetime);
        var rows = (await InventoryPresenceData.HeartbeatAsync(db, heartbeat, cancellationToken)).ToArray();
        if (rows.Length == 0)
            return null;
        if (rows.Count(row => row.EmployeeId == access.EmployeeId) != 1 ||
            rows.Select(row => row.EmployeeId).Distinct().Count() != rows.Length ||
            rows.Any(row => row.EmployeeId <= 0 || string.IsNullOrWhiteSpace(row.DisplayName) ||
                !Enum.IsDefined((ClayPresenceActivity)row.Activity)))
            throw new InvalidOperationException("Хранилище не подтвердило состав присутствующих.");

        var participants = rows.Where(row => row.EmployeeId != access.EmployeeId)
            .OrderBy(row => row.DisplayName, StringComparer.Ordinal).ThenBy(row => row.EmployeeId)
            .Select(row => new ClayPresenceAccess(row.EmployeeId, row.DisplayName, (ClayPresenceActivity)row.Activity))
            .ToArray();
        return new ClayPresenceSnapshot(token, participants);
    }

    /// <inheritdoc />
    public async ValueTask LeaveAsync(Guid lease)
    {
        if (lease != Guid.Empty)
            await InventoryPresenceData.LeaveAsync(db, lease, CancellationToken.None);
    }

    internal static byte[] EntityHash(ClayPresenceKey key) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new[] { key.DataScope, key.EntityType, key.SerializedKey }));

    /// <inheritdoc />
    public void Dispose() => db.Dispose();
}
