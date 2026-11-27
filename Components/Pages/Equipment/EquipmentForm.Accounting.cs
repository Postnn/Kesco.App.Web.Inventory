using System.Globalization;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private static string AccountingDraftScope(int id) =>
        $"child:{nameof(EquipmentAccountingDialog)}:{(id == 0 ? "new" : id.ToString(CultureInfo.InvariantCulture))}:root";
}
