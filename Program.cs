using Kesco.App.Web.Inventory.Components;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Authentication.Negotiate;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация: appsettings.json + web.config
builder.Configuration.AddWebConfig();
builder.Configuration["URI_user_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_user_form");
builder.Configuration["URI_model_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_model_form");
builder.Configuration["URI_equipment_form_ins"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_equipment_form_ins");
builder.Configuration["URI_equipment_form"] = WebConfigExtensions.ReadAppSettingFromWebConfig("URI_equipment_form");
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
        new DbManager(connectionString, sp.GetRequiredService<ISqlErrorHandler>()));
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
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.ModelEquipmentFormService>();
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.EquipmentInsService>();
builder.Services.AddScoped<Kesco.App.Web.Inventory.Services.EquipmentFormService>();

// Сервис авто-переподключения к БД (оверлей при потере соединения)
builder.Services.AddScoped<ClayDbReconnectService>();
builder.Services.AddScoped<ClaySelectAdvancedSearchService>();

// Аутентификация: Windows (Kerberos/NTLM)
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});

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

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/Equipment/PhoneConfigHandler.ashx", async (string? mac, Kesco.App.Web.Inventory.Services.EquipmentFormService service) =>
{
    if (string.IsNullOrWhiteSpace(mac) || mac.Length != 12)
        return Results.BadRequest("Укажите MAC-адрес из 12 символов.");
    try
    {
        var config = await service.GetPhoneConfigAsync(mac);
        var contentType = config.Contains("<?") && config.Contains("?>") ? "text/xml" : "text/plain";
        return Results.Text(config, contentType);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
