using Kesco.App.Web.Inventory.Components;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Authentication.Negotiate;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация: appsettings.json + web.config
builder.Configuration.AddWebConfig();
var claySettings = builder.Configuration.BindClaySettings();
builder.Services.AddSingleton(claySettings);

// Динамический режим ClayGrid
builder.Services.AddClayGridDynamic(builder.Configuration);

// Сервис глобального отображения ошибок
builder.Services.AddScoped<ClayErrorService>();
builder.Services.AddScoped<ISqlErrorHandler>(sp => sp.GetRequiredService<ClayErrorService>());

// DALC: scoped (одно подключение на запрос) — внедряем ISqlErrorHandler
builder.Services.AddScoped<DbManager>(sp => new DbManager(claySettings.ConnectionString, sp.GetRequiredService<ISqlErrorHandler>()));

// Сервис авто-переподключения к БД (оверлей при потере соединения)
builder.Services.AddScoped<ClayDbReconnectService>();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
