namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат формы расположения оборудования.</summary>
/// <param name="LocationId">Идентификатор расположения.</param>
/// <param name="FromLocal">Локальные дата и время начала.</param>
/// <param name="ReturnDate">Срок возврата.</param>
/// <param name="Notes">Примечания.</param>
/// <param name="EmployeeId">Ответственный, создаваемый вместе с расположением.</param>
public sealed record EquipmentLocationDialogResult(
    int LocationId,
    DateTime FromLocal,
    DateTime? ReturnDate,
    string Notes,
    int? EmployeeId)
{
    /// <summary>Immutable original V1 для CAS; fresh closure не заменяет это значение.</summary>
    public EquipmentPeriodOriginal? Original { get; init; }
}
