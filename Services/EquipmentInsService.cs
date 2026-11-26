using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции формы создания оборудования.</summary>
public class EquipmentInsService
{
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public EquipmentInsService(DbManager db) => _db = db;

    /// <summary>Загружает исходное оборудование для копирования.</summary>
    public virtual async Task<Equipment?> GetForCopyAsync(int id) =>
        (await _db.QueryAsync<Equipment>(SQLQueries.SELECT_ОборудованиеДляКопирования, new { Id = id }))
            .SingleOrDefault();

    /// <summary>Получает снимок сотрудника для формы через доверенный principal.</summary>
    public virtual Task<Employee?> GetCurrentUserAsync(string? windowsPrincipal, CancellationToken ct) =>
        Employee.GetCurrentWithRolesAsync(_db, windowsPrincipal, ct);

    /// <summary>Передаёт создание доменной операции с обязательной свежей проверкой сотрудника.</summary>
    public virtual Task<int> CreateAsync(Equipment equipment, string? windowsPrincipal, int expectedEmployeeId, CancellationToken ct,
        Func<Equipment, CancellationToken, Task>? beforeDispatch = null) =>
        equipment.InsertAsync(_db, windowsPrincipal, expectedEmployeeId, ct, beforeDispatch);

    /// <summary>Проверяет доступность бухгалтерского блока для выбранного владельца и снимка сотрудника формы.</summary>
    public virtual Task<bool> CanUseAccountingAsync(Employee? employee, int? personId, CancellationToken ct) =>
        Equipment.CanUseAccountingAsync(_db, employee, personId, ct);
}
