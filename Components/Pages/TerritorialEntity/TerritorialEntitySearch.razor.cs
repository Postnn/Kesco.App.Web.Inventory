using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Ленивая навигация по территориям с metadata-списками выбранного узла.</summary>
public partial class TerritorialEntitySearch : IAsyncDisposable
{
    /// <summary>Режим вызова поиска.</summary>
    [SupplyParameterFromQuery(Name = "return")] public string? ReturnMode { get; set; }
    /// <summary>Один или несколько ограничивающих корней.</summary>
    [SupplyParameterFromQuery(Name = "rootids")] public string? RootIds { get; set; }
    /// <summary>Один ограничивающий корень.</summary>
    [SupplyParameterFromQuery(Name = "root")] public string? Root { get; set; }
    /// <summary>Старый псевдоним корня.</summary>
    [SupplyParameterFromQuery(Name = "parent")] public string? Parent { get; set; }
    /// <summary>Корреляция с ожидающим общим окном выбора.</summary>
    [SupplyParameterFromQuery(Name = "requestId")] public string? RequestId { get; set; }
    /// <summary>Origin ожидающего окна. Принимается только точный origin данного приложения.</summary>
    [SupplyParameterFromQuery(Name = "returnOrigin")] public string? ReturnOrigin { get; set; }

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private ClayFormWindowService WindowService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _registrationId = Guid.NewGuid().ToString("N");
    private DotNetObjectReference<TerritorialEntitySearch>? _reference;
    private Employee? CurrentUser { get; set; }
    private ClayTreeOptions? _treeOptions;
    private ClayTreeView? _tree;
    private TerritorialEntityTreeSource? _source;
    private ClayTreeNode? _active;
    private string? _gridUrl;
    private string? _message;
    private int? _total;
    private bool _loading = true;
    private bool _phoneCodes;
    private bool _disposed;
    private bool _loadPending;
    private long _generation;
    private (string? Mode, string? Roots, string? Root, string? Parent, string? Request, string? Origin)? _context;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        var context = (ReturnMode, RootIds, Root, Parent, RequestId, ReturnOrigin);
        if (_context == context) return;
        _context = context;
        _generation++;
        _loadPending = true;
        _loading = true;
        _message = null;
        _total = null;
        _treeOptions = null;
        _source = null;
        ResetNode();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_loadPending) return;
        _loadPending = false;
        var generation = _generation;
        try
        {
            if (!TerritorialEntityRoutes.TryRoots(RootIds, Root, Parent, out var roots))
            {
                _message = "Некорректное ограничение корней дерева.";
                return;
            }
            if (ReturnMode is "2")
            {
                _message = "Возврат нескольких территорий пока недоступен: общий компонент дерева поддерживает одиночный выбор.";
                return;
            }
            if (ReturnMode is not (null or "1"))
            {
                _message = "Неизвестный режим выбора территории.";
                return;
            }
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            CurrentUser = await Employee.GetCurrentWithRolesAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, _lifetime.Token);
            if (_disposed || generation != _generation) return;
            if (CurrentUser is null)
            {
                _message = "Не удалось подтвердить текущего сотрудника.";
                return;
            }
            var access = await TerritorialEntityData.ReadAccessAsync(Db, CurrentUser, _lifetime.Token);
            if (_disposed || generation != _generation) return;
            List<ClayTreeMenuItem> actions = [];
            if (ReturnMode is null && access.TerritoryUpdate)
                actions.Add(new() { Label = "Редактировать", Icon = Icons.Material.Filled.Edit, OnExecute = OpenTerritoryAsync });
            if (ReturnMode == "1" && TerritorialEntityRoutes.IsLocalReturn(RequestId, ReturnOrigin, Navigation.BaseUri))
                actions.Add(new() { Label = "Выбрать значение", Icon = Icons.Material.Filled.Check, OnExecute = ReturnAsync });
            else if (ReturnMode == "1") _message = "Не подтверждён запрос окна выбора. Вернитесь в вызывающую форму.";

            _source = new(Db, roots, total => _ = InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                _total = total;
                if (total is not null) ResetNode();
                StateHasChanged();
            }));
            _treeOptions = new()
            {
                TreeId = "territorial-search", SelectSql = TerritorialEntityData.TreeSource,
                HierarchyMode = ClayTreeHierarchyMode.ParentKey,
                Schema = new() { IdColumn = "Id", TextColumn = "Text", ParentColumn = "ParentId", LeftColumn = "Left", RightColumn = "Right" },
                PersistExpandedState = false, OrderBy = "[Text], [Id]", ShowLines = true, CustomMenuItems = actions,
                FilterColumns =
                [
                    new() { SqlName = "Id", DisplayName = "Код территории", ColumnType = ColumnType.Number, UrlKey = "codearea" },
                    new() { SqlName = "Text", DisplayName = "Территория", ColumnType = ColumnType.Text, UrlKey = "search" },
                ],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _generation) _message = "Не удалось загрузить дерево территорий. Откройте страницу повторно.";
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private static bool IsCity(ClayTreeNode node) => node.Raw.TryGetValue("Type", out var type) &&
        type is ТипыТерриитории.Населённые_пункты;

    private Task SelectNodeAsync(ClayTreeNode node)
    {
        if (node.RawId is not int id || id <= 0) return Task.CompletedTask;
        _active = node;
        if (IsCity(node)) _phoneCodes = true;
        UpdateGrid(id);
        return Task.CompletedTask;
    }

    private void ResetNode()
    {
        _active = null;
        _gridUrl = null;
        _phoneCodes = false;
    }

    private void ShowPhoneCodes(bool enabled)
    {
        if (_active?.RawId is not int id) return;
        _phoneCodes = enabled;
        UpdateGrid(id);
    }

    private void UpdateGrid(int id) => _gridUrl = Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Grid(
        _phoneCodes ? Запросы.Телефонные_коды : Запросы.Территории, id)).AbsoluteUri;

    private async Task OpenTerritoryAsync(ClayTreeNode node)
    {
        if (node.RawId is not int id || id <= 0) return;
        await JS.InvokeVoidAsync("clayFormWindow.registerRefresh", _lifetime.Token, _registrationId,
            _reference ??= DotNetObjectReference.Create(this));
        if (!await WindowService.OpenAsync(Navigation.ToAbsoluteUri(TerritorialEntityRoutes.TerritoryEdit(id)).AbsoluteUri,
                "territory-form", _lifetime.Token))
            _message = "Браузер заблокировал окно территории.";
    }

    private async Task ReturnAsync(ClayTreeNode node)
    {
        if (node.RawId is not int id || id <= 0 || !TerritorialEntityRoutes.IsLocalReturn(RequestId, ReturnOrigin, Navigation.BaseUri)) return;
        if (!await JS.InvokeAsync<bool>("claySelectAdvancedSearch.returnValue", _lifetime.Token, RequestId, id))
            _message = "Не удалось передать выбранную территорию в вызывающее окно.";
    }

    private Task CloseAsync() => JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token).AsTask();

    private async Task ReloadTreeAsync()
    {
        if (_tree is not IClayTreeView tree) return;
        await tree.ReloadAsync();
        if (_active is not null && !tree.SelectedIds.Contains(_active.Id)) ResetNode();
    }

    /// <summary>Повторно читает дерево в вызвавшем окне после сохранения карточки.</summary>
    [JSInvokable]
    public Task RefreshFromChildAsync() => InvokeAsync(async () => { await ReloadTreeAsync(); StateHasChanged(); });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_reference is not null)
        {
            try { await JS.InvokeVoidAsync("clayFormWindow.unregisterRefresh", _registrationId); }
            catch (JSDisconnectedException) { }
            _reference.Dispose();
        }
        _lifetime.Dispose();
    }
}
