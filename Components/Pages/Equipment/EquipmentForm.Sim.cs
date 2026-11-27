using System.Globalization;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string SimDraftScope = "child:" + nameof(EquipmentSimDialog) + ":new:root";
    private static string SimServiceDraftScope(int id) =>
        $"child:{nameof(EquipmentSimServiceDialog)}:{(id == 0 ? "new" : id.ToString(CultureInfo.InvariantCulture))}:root";
}
