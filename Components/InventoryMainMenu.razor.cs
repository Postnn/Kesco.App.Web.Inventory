using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Navigation;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace Kesco.App.Web.Inventory.Components;

/// <summary>Тонкая Inventory-композиция: загрузка Запросы, права и URL для общей ClayNavigationMenu.</summary>
public partial class InventoryMainMenu
{
    private static readonly string[] SettingsRoles =
    [
        @"BUILTIN\Administrators", @"TEST\Programists", @"EURO\Programists", @"TEST\SupportTeam",
    ];
    private readonly ClayNavigationMenuOptions _options = new() { AriaLabel = "Меню инвентаризации" };
    private IReadOnlyList<ClayNavigationMenuItem> _items = [];
    private int? _loadedQueryId;
    private bool _loadAttempted;
    private bool _loading;
    private string? _error;

    /// <summary>Текущий код запроса для активного пункта и контекстного отчёта.</summary>
    [Parameter] public int? CurrentQueryId { get; set; }

    [Inject] private DbManager Db { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;
    [Inject] private Clayzor.Lib.Web.Controls.ClayErrorService ErrorService { get; set; } = default!;
    [Inject] private ILogger<InventoryMainMenu> Logger { get; set; } = default!;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loading || (_loadAttempted && _loadedQueryId == CurrentQueryId)) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadAttempted = true;
        _loading = true;
        _loadedQueryId = CurrentQueryId;
        _error = null;
        try
        {
            var data = await InventoryMenuData.LoadAsync(Db, CurrentQueryId);
            if (data.Items.Count == 0 && ErrorService.IsCurrentErrorConnectivity)
            {
                _error = "Не удалось загрузить меню из таблицы Запросы. Проверьте соединение и повторите попытку.";
                return;
            }
            var canOpenSettings = SettingsRoles.Any(role => HttpContextAccessor.HttpContext?.User.IsInRole(role) == true);
            _items = InventoryMainMenuComposer.Build(
                data, CurrentQueryId, Configuration, Navigation.Uri, canOpenSettings);
            LogDisabledLinks(_items);
        }
        catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            _error = "Не удалось загрузить меню из таблицы Запросы. Повторите попытку.";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RetryAsync()
    {
        _loadedQueryId = null;
        await LoadAsync();
    }

    private void LogDisabledLinks(IEnumerable<ClayNavigationMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Disabled && item.Title is not null)
                Logger.LogWarning("Пункт меню Inventory недоступен: {Reason}", item.Title);
            LogDisabledLinks(item.Children);
        }
    }
}
