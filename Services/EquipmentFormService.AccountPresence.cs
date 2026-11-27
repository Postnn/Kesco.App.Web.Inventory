using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Проверяет присутствие в сохранённой учётной записи без чтения её пароля.</summary>
public partial class EquipmentFormService
{
    internal const string AccountPresenceKeySetting = "ClayPresence:AccountIdentityKey";

    internal EquipmentChildPresence? CreateAccountPresence(int equipmentId, string canonicalLogin, string? encodedKey)
    {
        if (equipmentId <= 0 || string.IsNullOrWhiteSpace(canonicalLogin) || canonicalLogin.Length > 50 ||
            string.IsNullOrWhiteSpace(encodedKey))
            return null;

        Span<byte> key = stackalloc byte[32];
        string opaqueIdentity;
        try
        {
            if (!Convert.TryFromBase64String(encodedKey, key, out var bytesWritten) || bytesWritten != key.Length)
                return null;

            var identity = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Purpose = "clayzor:inventory:equipment-account:presence:v1",
                EquipmentId = equipmentId,
                Login = canonicalLogin,
            });
            try { opaqueIdentity = Convert.ToHexString(HMACSHA256.HashData(key, identity)); }
            finally { CryptographicOperations.ZeroMemory(identity); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        return new(ClayPresenceKey.Create("inventory", "equipment-account", opaqueIdentity),
            async (connection, principal, employeeId, token) =>
            {
                var actor = await RequireAccountAccessAsync(connection, null, principal, employeeId, equipmentId, token);
                var stored = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                    EquipmentChildPresenceQueries.AccountPresenceLogin,
                    new { EquipmentId = equipmentId, Login = new DbString { Value = canonicalLogin, IsAnsi = true, Length = 50 } },
                    cancellationToken: token));
                return string.Equals(stored, canonicalLogin, StringComparison.Ordinal) ? actor : null;
            });
    }
}
