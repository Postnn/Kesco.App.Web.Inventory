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

    /// <summary>Создаёт оборудование и фиксирует подтверждение точной попытки в той же транзакции.</summary>
    public virtual Task<int> CreateJournaledAsync(Equipment equipment, string? windowsPrincipal, int expectedEmployeeId,
        Guid attemptId, CancellationToken ct, Func<Equipment, CancellationToken, Task>? beforeDispatch = null) =>
        equipment.InsertJournaledAsync(_db, windowsPrincipal, expectedEmployeeId, attemptId, ct, beforeDispatch);

    /// <summary>Читает наличие незавершённой серверной попытки до нового создания.</summary>
    public virtual Task<bool> ReadCreationPendingAsync(string? principal, int employeeId, CancellationToken ct) =>
        Equipment.ReadCreationPendingAsync(_db, principal, employeeId, ct);

    /// <summary>Читает последний серверный исход собственного создания оборудования.</summary>
    public virtual Task<InventoryOperationReceipt?> ReadLatestCreationReceiptAsync(string? principal, int employeeId,
        CancellationToken ct) => Equipment.ReadLatestCreationReceiptAsync(_db, principal, employeeId, ct);

    /// <summary>Сверяет подтверждение точной попытки без повторного вызова процедуры.</summary>
    public virtual Task<InventoryOperationReceipt?> ReadCreationReceiptAsync(string? principal, int employeeId,
        Guid attemptId, CancellationToken ct) =>
        Equipment.ReadCreationReceiptAsync(_db, principal, employeeId, attemptId, ct);

    /// <summary>Передаёт серверу сведения о незавершённых прежних попытках создания.</summary>
    public virtual Task ImportLegacyCreationOperationsAsync(string? principal, int employeeId,
        IReadOnlyCollection<Guid> attempts, CancellationToken ct) =>
        Equipment.ImportLegacyCreationOperationsAsync(_db, principal, employeeId, attempts, ct);

    /// <summary>Проверяет доступность бухгалтерского блока для выбранного владельца и снимка сотрудника формы.</summary>
    public virtual Task<bool> CanUseAccountingAsync(Employee? employee, int? personId, CancellationToken ct) =>
        Equipment.CanUseAccountingAsync(_db, employee, personId, ct);
}
