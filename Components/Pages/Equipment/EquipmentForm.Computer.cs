using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string ComputerDraftScope = "child:" + nameof(EquipmentComputerDialog) + ":new:root";
}
