using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private static string SoftwareDraftScope(int installationId) =>
        $"child:{nameof(EquipmentSoftwareDialog)}:{(installationId == 0 ? "new" : installationId.ToString(CultureInfo.InvariantCulture))}:root";

}
