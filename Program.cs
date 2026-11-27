using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Services.PingDiagnostics;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Controls.Themes;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация: appsettings.json + web.config
builder.Configuration.AddWebConfig();
builder.Configuration["URI_user_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_user_form");
builder.Configuration["URI_model_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_model_form");
builder.Configuration["URI_tel_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_tel_form");
builder.Configuration["pickupGroups"] = WebConfigExtensions.ReadAppSettingFromWebConfig("pickupGroups");
builder.Configuration["URI_equipment_form_ins"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_equipment_form_ins");
builder.Configuration["URI_equipment_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_equipment_form");
builder.Configuration["URI_net_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_net_form");
builder.Configuration["URI_area_search_tree"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_area_search_tree");
builder.Configuration["URI_area_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_area_form");
builder.Configuration["URI_telcode_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_telcode_form");
builder.Configuration["URI_sim_fmtn"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_sim_fmtn");
builder.Configuration["URI_inventory_rpt_modifications"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_inventory_rpt_modifications");
builder.Configuration["URI_styles"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_styles");
builder.Configuration["URI_user_search"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_user_search");
builder.Configuration["URI_resource_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_resource_form");
builder.Configuration["URI_resource_search"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_resource_search");
var claySettings = builder.Configuration.BindClaySettings();
builder.Services.AddSingleton(claySettings);
Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();

// Динамический режим ClayGrid
builder.Services.AddClayGridDynamic(builder.Configuration);

// Компонент ClayTreeView (персистентное состояние через SQL)
builder.Services.AddClayTree(builder.Configuration);
builder.Services.AddScoped<IClayTreeStateStore, ClaySqlTreeStateStore>();
// IClayTreeMutations — через ClayTreeOptions.TableName (см. TreeTest.razor)

// Сервис глобального отображения ошибок
builder.Services.AddScoped<ClayErrorService>();
builder.Services.AddScoped<ISqlErrorHandler>(sp => sp.GetRequiredService<ClayErrorService>());

// DALC: scoped (одно подключение на запрос) — внедряем ISqlErrorHandler
builder.Services.AddScoped<DbManager>(sp => new DbManager(claySettings.ConnectionString, sp.GetRequiredService<ISqlErrorHandler>()));

// Общие источники данных ClaySelect
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.EquipmentTypeSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.EquipmentModelSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.PhonePoolSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.PhoneNumberSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.MemoryTypeSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.MobileContractSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.PersonalDataPersonSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.SoftwareEquipmentSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Equipment.AccountingPersonSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Employees.EmployeeSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Locations.LocationSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Locations.NetSocketSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneTypeSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneStationSelectDataSource>();
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource>("phoneGroup", (sp, _) =>
    new Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource(sp.GetRequiredService<DbManager>())
    { Mode = Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectoryMode.Group });
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource>("drivenBy", (sp, _) =>
    new Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource(sp.GetRequiredService<DbManager>())
    { Mode = Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectoryMode.DrivenBy });
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource>("displayedFrom", (sp, _) =>
    new Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectorySelectDataSource(sp.GetRequiredService<DbManager>())
    { Mode = Clayzor.Lib.Web.DBSelect.Phones.PhoneDirectoryMode.DisplayedFrom });
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneEquipmentSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneSocketSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneCustomerSelectDataSource>();
builder.Services.AddScoped<Clayzor.Lib.Web.DBSelect.Phones.PhoneEmployeeSelectDataSource>();
builder.Services.AddScoped(sp =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_resource")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_resource");
    return new Clayzor.Lib.Web.DBSelect.Equipment.ResourceSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()));
});
builder.Services.AddScoped(sp =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_person")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_person");
    return new Clayzor.Lib.Web.DBSelect.Persons.PersonSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()));
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource>("receipt", (sp, _) =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_doc")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_doc");
    return new Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()));
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource>("writeoff", (sp, _) =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_doc")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_doc");
    return new Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()))
    {
        DocumentTypeId = 2365,
    };
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource>("accounting", (sp, _) =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_doc")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_doc");
    return new Clayzor.Lib.Web.DBSelect.Documents.DocumentSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()));
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Equipment.AccountingDocumentSelectDataSource>("accountingReceipt", (sp, _) =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_doc")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_doc");
    return new Clayzor.Lib.Web.DBSelect.Equipment.AccountingDocumentSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()), writeOff: false);
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Equipment.AccountingDocumentSelectDataSource>("accountingWriteOff", (sp, _) =>
{
    var connectionString = WebConfigExtensions.ReadConnectionStringFromWebConfig("DS_doc")
                           ?? throw new InvalidOperationException("Не задана строка подключения DS_doc");
    return new Clayzor.Lib.Web.DBSelect.Equipment.AccountingDocumentSelectDataSource(
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()), writeOff: true);
});
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Equipment.UpsEquipmentSelectDataSource>("ups", (sp, _) =>
    new Clayzor.Lib.Web.DBSelect.Equipment.UpsEquipmentSelectDataSource(
        sp.GetRequiredService<DbManager>(), upsOnly: true));
builder.Services.AddKeyedScoped<Clayzor.Lib.Web.DBSelect.Equipment.UpsEquipmentSelectDataSource>("server", (sp, _) =>
    new Clayzor.Lib.Web.DBSelect.Equipment.UpsEquipmentSelectDataSource(
        sp.GetRequiredService<DbManager>(), upsOnly: false));
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.EquipmentInsService>();
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.EquipmentFormService>();
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.PhoneFormService>();

// Сервис авто-переподключения к БД (оверлей при потере соединения)
builder.Services.AddScoped<ClayDbReconnectService>();
builder.Services.AddScoped<ClayDraftService>();
var presenceConnectionString = builder.Configuration.GetConnectionString("InventoryPresence") ?? claySettings.ConnectionString;
builder.Services.AddScoped<IClayPresenceRegistry>(sp => new Kesco.App.Web.Inventory.Services.InventorySqlPresenceRegistry(
    new DbManager(presenceConnectionString), sp.GetRequiredService<ClayPresenceOptions>()));
builder.Services.AddClayEntityPresence(options => builder.Configuration.GetSection("ClayPresence").Bind(options));
builder.Services.AddScoped<ClaySelectAdvancedSearchService>();

// Аутентификация: Windows (Kerberos/NTLM)
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddInventoryThemePreferences(async sp =>
    (await sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User);

builder.Services.Configure<PingDiagnosticOptions>(builder.Configuration.GetSection("PingDiagnostics"));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<INetworkPingTransport, NetworkPingTransport>();
builder.Services.AddSingleton<PingDiagnosticService>();
builder.Services.AddScoped<PingCircuitLifetime>();
builder.Services.AddScoped<CircuitHandler>(services => services.GetRequiredService<PingCircuitLifetime>());

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MudBlazor
builder.Services.AddMudServices();
builder.Services.AddMudExtensions(cfg => cfg.WithDefaultDialogOptions(d => d.DragMode = MudDialogDragMode.Simple));

// HttpContext для User.Identity
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

Kesco.App.Web.Inventory.Services.DraftKeyEndpoint.Map(app);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

EquipmentPhoneConfigEndpoint.Map(app);
NetworkPingEndpoint.Map(app);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
