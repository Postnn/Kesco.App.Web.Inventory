namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат редактирования учётной записи оборудования.</summary>
/// <param name="Login">Логин.</param>
/// <param name="Password">Пароль, передаваемый только в памяти текущего circuit.</param>
/// <param name="LimitedRights">Признак ограниченных прав.</param>
/// <param name="Notes">Примечание.</param>
public sealed record EquipmentAccountDialogResult(
    string Login,
    string Password,
    bool LimitedRights,
    string Notes);
