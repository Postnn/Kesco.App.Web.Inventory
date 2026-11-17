using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции чтения и сохранения основной записи оборудования.</summary>
public class EquipmentFormService
{
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public EquipmentFormService(DbManager db) => _db = db;

    /// <summary>Загружает основную запись оборудования для редактирования.</summary>
    public virtual async Task<Equipment?> GetAsync(int id) =>
        (await _db.QueryAsync<Equipment>(SQLQueries.SELECT_ID_Оборудование, new { Id = id }))
            .SingleOrDefault();

    /// <summary>Сохраняет 15 основных полей существующего оборудования.</summary>
    public virtual Task UpdateAsync(Equipment equipment) => equipment.UpdateAsync(_db);

    /// <summary>Проверяет роль текущего пользователя с учётом замещений.</summary>
    public virtual async Task<bool> HasRoleAsync(int roleId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ТекущийПользовательИмеетРоль,
            new { RoleId = roleId },
            commandType: CommandType.Text) == 1;
}
