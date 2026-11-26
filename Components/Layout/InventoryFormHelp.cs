using Clayzor.Lib.Web.Controls.Components.Form;

namespace Kesco.App.Web.Inventory.Components.Layout;

/// <summary>Реестр документации маршрутов Inventory.</summary>
public static class InventoryFormHelp
{
    /// <summary>Справка рабочего места и встроенного подтверждения оборудования на расположении.</summary>
    public static ClayFormHelp LocationFact { get; } = new("Inventory/Inv/LocationFact.aspx");

    /// <summary>Справка телефонной карточки и её вложенного редактора параллельного вызова.</summary>
    public static ClayFormHelp Tel { get; } = new("Inventory/Telefon/Tel.aspx");

    /// <summary>Справка дочернего редактора расположения оборудования.</summary>
    public static ClayFormHelp EquipmentLocation { get; } = new("Inventory/Equipment/LocationEquipment_form.aspx");

    /// <summary>Справка дочернего редактора ответственного за оборудование.</summary>
    public static ClayFormHelp EquipmentResponsible { get; } = new("Inventory/Equipment/ResponsibleEquipment_form.aspx");

    /// <summary>Справка дочернего редактора учётной записи оборудования.</summary>
    public static ClayFormHelp EquipmentAccount { get; } = new("Inventory/Equipment/EquipmentLogin_form.aspx");

    /// <summary>Справка дочернего редактора конфигурации компьютера.</summary>
    public static ClayFormHelp EquipmentComputer { get; } = new("Inventory/Equipment/Computer_form.aspx");

    /// <summary>Справка дочернего редактора установленного ПО.</summary>
    public static ClayFormHelp EquipmentSoftware { get; } = new("Inventory/Equipment/InstallSoft.aspx");

    /// <summary>Справка дочернего редактора бухгалтерского учёта.</summary>
    public static ClayFormHelp EquipmentAccounting { get; } = new("Inventory/Equipment/EquipmentBuxMove_form.aspx");

    /// <summary>Справка дочернего редактора SIM-карты.</summary>
    public static ClayFormHelp EquipmentSim { get; } = new("Inventory/Equipment/SIM_form.aspx");

    /// <summary>Справка дочернего редактора дополнительной услуги SIM.</summary>
    public static ClayFormHelp EquipmentSimService { get; } = new("Inventory/Equipment/AdditionalService_form.aspx");

    /// <summary>Справка дочернего редактора лицензии.</summary>
    public static ClayFormHelp EquipmentLicense { get; } = new("Inventory/Equipment/Soft_form.aspx");

    /// <summary>Справка карточки оборудования и встроенного редактора привязки UPS.</summary>
    public static ClayFormHelp Equipment { get; } = new("Inventory/Equipment/Equipment_form.aspx");

    /// <summary>Справка редактора подключений UPS и его вложенного редактора тестирования.</summary>
    public static ClayFormHelp EquipmentUps { get; } = new("Inventory/Equipment/LinkUps.aspx");

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
        Register(Equipment, "/Equipment/EquipmentForm");
        Register(new("Inventory/Equipment/EquipmentIns.aspx"), "/Equipment/EquipmentIns");
        Register(new("Inventory/Equipment/ModelEquipmentForm.aspx"), "/Equipment/ModelEquipmentForm");
        Register(new("Inventory/Equipment/PhoneTemplateForm.aspx"), "/Equipment/PhoneTemplateForm");
        Register(new("Inventory/Equipment/PhonePoolAtribute_form.aspx"), "/Equipment/PhonePoolAtribute_form", "/Equipment/PhonePoolAtribute_form.aspx");
        Register(new("Inventory/Equipment/PingHost.aspx"), "/Equipment/PingHost", "/Equipment/PingHost.aspx");
        Register(new("Inventory/Equipment/SIM_FMTN.aspx"), "/Equipment/SIM_FMTN", "/Equipment/SIM_FMTN.aspx");
        Register(new("Inventory/Equipment/SlBl_form.aspx"), "/Equipment/SlBl_form", "/Equipment/SlBl_form.aspx");
        Register(LocationFact, "/Inv/LocationFact", "/Inv/LocationFact.aspx");
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
