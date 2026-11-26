using Clayzor.Lib.Web.Controls.Components.Form;

namespace Kesco.App.Web.Inventory.Components.Layout;

/// <summary>Реестр документации маршрутов Inventory.</summary>
public static class InventoryFormHelp
{
    private static readonly ClayFormHelp PingHost = new("Inventory/Equipment/PingHost.aspx");

    /// <summary>Находит метаданные по маршруту относительно PathBase приложения.</summary>
    public static ClayFormHelp? Find(string relativeLocation)
    {
        var delimiter = relativeLocation.AsSpan().IndexOfAny('?', '#');
        var route = (delimiter < 0 ? relativeLocation : relativeLocation[..delimiter]).TrimEnd('/');
        return route.Equals("Equipment/PingHost", StringComparison.OrdinalIgnoreCase) ||
               route.Equals("Equipment/PingHost.aspx", StringComparison.OrdinalIgnoreCase)
            ? PingHost
            : null;
    }
}
