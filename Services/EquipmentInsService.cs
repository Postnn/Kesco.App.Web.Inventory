using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции формы создания оборудования.</summary>
public class EquipmentInsService
{
    private const int AccountantRoleId = 66;
    private const int ChiefAccountantRoleId = 68;
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public EquipmentInsService(DbManager db) => _db = db;

    /// <summary>Загружает исходное оборудование для копирования.</summary>
    public virtual async Task<Equipment?> GetForCopyAsync(int id) =>
        (await _db.QueryAsync<Equipment>(SQLQueries.SELECT_ОборудованиеДляКопирования, new { Id = id }))
            .SingleOrDefault();

    /// <summary>Создаёт оборудование через хранимую процедуру.</summary>
    public virtual Task<int> CreateAsync(Equipment equipment) => equipment.InsertAsync(_db);

    /// <summary>Проверяет роль текущего пользователя с учётом замещений.</summary>
    public virtual async Task<bool> HasRoleAsync(int roleId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ТекущийПользовательИмеетРоль,
            new { RoleId = roleId },
            commandType: CommandType.Text) == 1;

    /// <summary>Проверяет доступность бухгалтерского блока для выбранного владельца.</summary>
    public virtual async Task<bool> CanUseAccountingAsync(int? personId)
    {
        if (personId is null or <= 0)
            return false;
        var hasRole = await HasRoleForPersonAsync(AccountantRoleId, personId.Value) ||
                      await HasRoleForPersonAsync(ChiefAccountantRoleId, personId.Value);
        if (!hasRole)
            return false;
        return await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
            new { PersonId = personId.Value },
            commandType: CommandType.Text) == 1;
    }

    private async Task<bool> HasRoleForPersonAsync(int roleId, int personId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ТекущийПользовательИмеетРольПоЛицу,
            new { RoleId = roleId, PersonId = personId },
            commandType: CommandType.Text) == 1;
}
