namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат редактирования срока возврата телефонной связи.</summary>
/// <param name="ReturnDate">Срок возврата.</param>
/// <param name="Notes">Примечание.</param>
public sealed record EquipmentPhoneReturnResult(DateTime? ReturnDate, string Notes);
