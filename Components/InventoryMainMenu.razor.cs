using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Navigation;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kesco.App.Web.Inventory.Components;

/// <summary>Тонкая Inventory-композиция: загрузка Запросы, права и URL для общей ClayNavigationMenu.</summary>
public partial class InventoryMainMenu : IDisposable
{
    private static readonly string[] SettingsRoles =
    [
        @"BUILTIN\Administrators", @"TEST\Programists", @"EURO\Programists", @"TEST\SupportTeam",
    ];
    private readonly ClayNavigationMenuOptions _options = new() { AriaLabel = "Меню инвентаризации" };
    private IReadOnlyList<ClayNavigationMenuItem> _items = [];
    private InventoryMenuLoadResult? _menuData;
    private int? _loadedQueryId;
    private bool _loadAttempted;
    private bool _loading;
    private string? _error;

    /// <summary>Текущий код запроса для активного пункта и контекстного отчёта.</summary>
    [Parameter] public int? CurrentQueryId { get; set; }

    /// <summary>Дополнительные действия внутри полосы меню.</summary>
    [Parameter] public RenderFragment? Actions { get; set; }

    [Inject] private DbManager Db { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private Clayzor.Lib.Web.Controls.ClayErrorService ErrorService { get; set; } = default!;
    [Inject] private ILogger<InventoryMainMenu> Logger { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() =>
        AuthenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loading || (_loadAttempted && _loadedQueryId == CurrentQueryId)) return;
        await LoadAsync();
    }

    internal static bool CanOpenSettings(System.Security.Claims.ClaimsPrincipal? user)
    {
        var name = user?.Identity?.Name;
        var isDevadmin = string.Equals(name, "devadmin", StringComparison.OrdinalIgnoreCase)
            || name?.EndsWith(@"\devadmin", StringComparison.OrdinalIgnoreCase) == true;
        return isDevadmin || SettingsRoles.Any(role => user?.IsInRole(role) == true);
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
            _menuData = data;
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            ApplyMenu(authState.User);
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

    private async void OnAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        try
        {
            var authState = await authenticationStateTask;
            if (_menuData is null || _loading) return;
            ApplyMenu(authState.User);
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Не удалось обновить права меню Inventory после изменения состояния аутентификации");
        }
    }

    private void ApplyMenu(System.Security.Claims.ClaimsPrincipal user)
    {
        _items = InventoryMainMenuComposer.Build(
            _menuData!, CurrentQueryId, Configuration, Navigation.Uri, CanOpenSettings(user));
        LogDisabledLinks(_items);
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

    /// <summary>Отписывается от изменений состояния аутентификации.</summary>
    public void Dispose() => AuthenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}
