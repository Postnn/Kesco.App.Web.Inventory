namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Операция с ответственным сотрудником.</summary>
public enum EquipmentResponsibleDialogMode
{
    /// <summary>Выдать оборудование.</summary>
    Issue,
    /// <summary>Изыять оборудование.</summary>
    Withdraw,
    /// <summary>Изменить срок и примечания.</summary>
    Edit,
}

/// <summary>Результат формы ответственного.</summary>
/// <param name="EmployeeId">Сотрудник.</param>
/// <param name="FromLocal">Начало периода.</param>
/// <param name="ToLocal">Окончание периода.</param>
/// <param name="ReturnDate">Срок возврата.</param>
/// <param name="Notes">Примечания.</param>
/// <param name="LocationId">Новое расположение.</param>
public sealed record EquipmentResponsibleDialogResult(
    int? EmployeeId,
    DateTime? FromLocal,
    DateTime? ToLocal,
    DateTime? ReturnDate,
    string Notes,
    int? LocationId);
