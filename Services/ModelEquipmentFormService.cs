using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции формы создания и редактирования модели оборудования.</summary>
public class ModelEquipmentFormService
{
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public ModelEquipmentFormService(DbManager db) => _db = db;

    /// <summary>Загружает модель для редактирования.</summary>
    public virtual Task<EquipmentModel?> GetModelAsync(int id) => EquipmentModel.GetForEditAsync(_db, id);

    /// <summary>Проверяет уникальность пары «модель + PN».</summary>
    public virtual Task<bool> HasDuplicateAsync(EquipmentModel model) => EquipmentModel.HasDuplicateAsync(_db, model);

    /// <summary>Получает количество связанного оборудования.</summary>
    public virtual Task<EquipmentModelUsage> GetUsageAsync(int id, int? newEquipmentTypeId = null) =>
        EquipmentModel.GetUsageAsync(_db, id, newEquipmentTypeId);

    /// <summary>Создаёт или обновляет модель.</summary>
    public virtual Task<int> SaveAsync(EquipmentModel model) => model.SaveAsync(_db);

    /// <summary>Сохраняет только шаблон телефона.</summary>
    public virtual Task SavePhoneTemplateAsync(EquipmentModel model) => model.SavePhoneTemplateAsync(_db);

    /// <summary>Проверяет роль текущего пользователя с учётом замещений.</summary>
    public virtual async Task<bool> HasRoleAsync(int roleId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ТекущийПользовательИмеетРоль,
            new { RoleId = roleId },
            commandType: CommandType.Text) == 1;
}
