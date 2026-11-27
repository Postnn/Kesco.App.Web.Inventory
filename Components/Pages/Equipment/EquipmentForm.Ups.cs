using System.Globalization;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private static string UpsDraftScope(int id) => $"child:{nameof(EquipmentUpsDialog)}:{id.ToString(CultureInfo.InvariantCulture)}:root";
}
