using Clayzor.Lib.Web.Controls.Components.Form;

namespace Kesco.App.Web.Inventory.Components.Layout;

/// <summary>Реестр документации маршрутов Inventory.</summary>
public static class InventoryFormHelp
{
    /// <summary>Справка телефонной карточки и её вложенного редактора параллельного вызова.</summary>
    public static ClayFormHelp Tel { get; } = new("Inventory/Telefon/Tel.aspx");

    private static readonly IReadOnlyDictionary<string, ClayFormHelp> Routes = BuildRoutes();

    /// <summary>Находит метаданные по маршруту относительно PathBase приложения.</summary>
    public static ClayFormHelp? Find(string relativeLocation)
    {
        var delimiter = relativeLocation.AsSpan().IndexOfAny('?', '#');
        var route = (delimiter < 0 ? relativeLocation : relativeLocation[..delimiter]).TrimEnd('/');
        return Routes.GetValueOrDefault(route);
    }

    private static Dictionary<string, ClayFormHelp> BuildRoutes()
    {
        var routes = new Dictionary<string, ClayFormHelp>(StringComparer.OrdinalIgnoreCase);
        void Register(ClayFormHelp form, params string[] aliases)
        {
            foreach (var alias in aliases)
                routes.Add(alias.TrimStart('/'), form);
        }

        Register(new("Inventory/default.aspx"), "/");
        Register(new("Inventory/Default/Finder.aspx"), "/Default/Finder", "/Default/Finder.aspx");
        Register(new("Inventory/Default/FinderTel.aspx"), "/Default/FinderTel", "/Default/FinderTel.aspx");
        Register(new("Inventory/Duty/duty_form.aspx"), "/Duty/duty_form", "/Duty/duty_form.aspx");
        Register(new("Inventory/Equipment/Equipment_form.aspx"), "/Equipment/EquipmentForm");
        Register(new("Inventory/Equipment/EquipmentIns.aspx"), "/Equipment/EquipmentIns");
        Register(new("Inventory/Equipment/ModelEquipmentForm.aspx"), "/Equipment/ModelEquipmentForm");
        Register(new("Inventory/Equipment/PhoneTemplateForm.aspx"), "/Equipment/PhoneTemplateForm");
        Register(new("Inventory/Equipment/PhonePoolAtribute_form.aspx"), "/Equipment/PhonePoolAtribute_form", "/Equipment/PhonePoolAtribute_form.aspx");
        Register(new("Inventory/Equipment/PingHost.aspx"), "/Equipment/PingHost", "/Equipment/PingHost.aspx");
        Register(new("Inventory/Equipment/SIM_FMTN.aspx"), "/Equipment/SIM_FMTN", "/Equipment/SIM_FMTN.aspx");
        Register(new("Inventory/Equipment/SlBl_form.aspx"), "/Equipment/SlBl_form", "/Equipment/SlBl_form.aspx");
        Register(new("Inventory/Inv/LocationFact.aspx"), "/Inv/LocationFact", "/Inv/LocationFact.aspx");
        Register(new("Inventory/Maintenance.aspx"), "/Maintenance", "/Maintenance.aspx");
        Register(new("Inventory/Net/Net.aspx"), "/Net/Net");
        Register(new("Inventory/Reports/Modifications.aspx"), "/Reports/Modifications", "/Reports/Modifications.aspx");
        Register(new("Inventory/Telefon/BeelineParams.aspx"), "/Telefon/BeelineParams", "/Telefon/BeelineParams.aspx");
        Register(new("Inventory/Telefon/NumPool.aspx"), "/Telefon/NumPool", "/Telefon/NumPool.aspx");
        Register(Tel, "/Telefon/Tel", "/Telefon/Tel.aspx");
        Register(new("Inventory/TerritorialEntity/PhoneCodeEntity_form.aspx"), "/TerritorialEntity/PhoneCodeEntity_form", "/TerritorialEntity/PhoneCodeEntity_form.aspx");
        Register(new("Inventory/TerritorialEntity/TerritorialEntity_form.aspx"), "/TerritorialEntity/TerritorialEntity_form", "/TerritorialEntity/TerritorialEntity_form.aspx");
        Register(new("Inventory/TerritorialEntity/TerritorialEntity_search.aspx"), "/TerritorialEntity/TerritorialEntity_search", "/TerritorialEntity/TerritorialEntity_search.aspx");
        Register(new("Inventory/TypeEquipment/TypeEquipmentMenu.aspx"), "/TypeEquipment/TypeEquipmentMenu", "/TypeEquipment/TypeEquipmentMenu.aspx");
        return routes;
    }
}
