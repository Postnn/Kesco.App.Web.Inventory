using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string AccountDraftScopePrefix = "child:" + nameof(EquipmentAccountDialog) + ":";
    private static string AccountDraftScope(string login) => AccountDraftScopePrefix + login + ":root";
}
