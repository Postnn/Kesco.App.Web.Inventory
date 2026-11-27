using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities;
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
public partial class TerritorialEntitySearch : IAsyncDisposable, Clayzor.Lib.Web.Controls.Components.Tree.State.IClayTreeStateStore
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
    private readonly SemaphoreSlim _viewRefresh = new(1, 1);
    private readonly string _registrationId = Guid.NewGuid().ToString("N");
    private DotNetObjectReference<TerritorialEntitySearch>? _reference;
    private Employee? CurrentUser { get; set; }
    private ClayTreeOptions? _treeOptions;
    private ClayTreeView? _tree;
    private TerritorialEntityTreeSource? _source;
    private ClayTreeNode? _active;
    private string? _gridUrl;
    private ElementReference _gridFrame;
    private string? _message;
    private string? _treeRefreshError;
    private string? _paneRefreshError;
    private int? _total;
    private bool _loading = true;
    private bool _phoneCodes;
    private bool _canAddTerritory;
    private bool _disposed;
    private bool _loadPending;
    [SupplyParameterFromQuery(Name = "selectedids")] public string? SelectedIdsQuery { get; set; }
    private readonly SortedSet<int> _checked = [];
    private string? _selectionStorageKey;
    private TerritoryViewState? _viewState;
    private int _clid;
    private bool _persistView;
    private string? _windowViewKey;
    private long _generation;
    private string? _context;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        var context = Navigation.Uri;
        if (_context == context) return;
        _context = context;
        _generation++;
        _windowViewKey = null;
        _persistView = false;
        _clid = 0;
        _viewState = null;
        _checked.Clear();
        _selectionStorageKey = null;
        _loadPending = true;
        _loading = true;
        _message = null;
        _treeRefreshError = null;
        _paneRefreshError = null;
        _total = null;
        _canAddTerritory = false;
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
        if (!IsCurrent(generation)) return;
        try
        {
            if (!TerritorialEntityRoutes.TryRoots(RootIds, Root, Parent, out var roots))
            {
                _message = "Некорректное ограничение корней дерева.";
                return;
            }
            if (ReturnMode is not (null or "1" or "2"))
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
            if (ReturnMode == "2")
            {
                if (!TerritorialEntityRoutes.IsLocalReturn(RequestId, ReturnOrigin, Navigation.BaseUri) ||
                    !TerritorialEntityRoutes.TrySelectedIds(SelectedIdsQuery, out var initial))
                {
                    _message = "Некорректный запрос множественного выбора.";
                    return;
                }
                var storageKey = $"territory-selection:{CurrentUser.Id}:{RequestId}:{string.Join(',', roots ?? [])}";
                var stored = await JS.InvokeAsync<string?>("sessionStorage.getItem", _lifetime.Token, storageKey);
                if (stored is not null && !TerritorialEntityRoutes.TrySelectedIds(stored, out initial) ||
                    !await TerritorialEntityData.SelectionInScopeAsync(Db, initial, roots, _lifetime.Token))
                {
                    _message = "Сохранённый выбор содержит недоступную территорию.";
                    return;
                }
                if (_disposed || generation != _generation) return;
                _checked.UnionWith(initial);
                _selectionStorageKey = storageKey;
            }
            List<ClayTreeMenuItem> actions = [];
            if (ReturnMode is null && roots is null && access.TerritoryInsert)
            {
                _canAddTerritory = true;
                actions.Add(new()
                {
                    Label = "Добавить дочернюю",
                    Icon = Icons.Material.Filled.Add,
                    OnExecute = node => node.RawId is int id && id > 0 ? OpenNewTerritoryAsync(id) : Task.CompletedTask,
                });
            }
            if (ReturnMode is null && (access.TerritoryUpdate || access.TerritoryDelete))
                actions.Add(new() { Label = access.TerritoryUpdate ? "Редактировать" : "Карточка",
                    Icon = Icons.Material.Filled.Edit, OnExecute = OpenTerritoryAsync });
            if (ReturnMode == "1" && TerritorialEntityRoutes.IsLocalReturn(RequestId, ReturnOrigin, Navigation.BaseUri))
                actions.Add(new() { Label = "Выбрать значение", Icon = Icons.Material.Filled.Check, OnExecute = ReturnAsync });
            else if (ReturnMode == "1") _message = "Не подтверждён запрос окна выбора. Вернитесь в вызывающую форму.";

            _source = new(Db, roots, total => _ = InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                _total = total;
                _treeRefreshError = null;
                if (total is not null) ResetNode();
                StateHasChanged();
            }));
            if (_disposed || generation != _generation) return;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
            _persistView = ReturnMode is null && roots is null && query.Keys.All(key =>
                string.Equals(key, "CLID", StringComparison.OrdinalIgnoreCase)) &&
                (!query.TryGetValue("CLID", out var clid) || int.TryParse(clid, out _clid) && _clid >= 0);
            if (!_persistView)
            {
                var contextHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Navigation.Uri)));
                _windowViewKey = $"territory-view:{CurrentUser.Id}:{contextHash}";
            }
            {
                var state = _windowViewKey is null ? await TerritorialEntityData.LoadViewStateAsync(Db, _clid, _lifetime.Token)
                    : TerritoryViewState.Read(await JS.InvokeAsync<string?>("sessionStorage.getItem", _lifetime.Token, _windowViewKey));
                if (_disposed || generation != _generation) return;
                var ids = new[] { state?.Anchor, state?.Selected }.OfType<int>().Distinct().ToArray();
                if (state is not null && await TerritorialEntityData.SelectionInScopeAsync(Db, ids, roots, _lifetime.Token))
                {
                    var item = state.Selected is int key ? await TerritorialEntityData.FindTerritoryAsync(Db, key, _lifetime.Token) : null;
                    if (_disposed || generation != _generation) return;
                    _viewState = state;
                    if (item is not null)
                    {
                        _active = ToPaneNode(item);
                        _phoneCodes = IsCity(_active) || state.PhoneCodes;
                        UpdateGrid(item.Id);
                    }
                }
            }
            if (_disposed || generation != _generation) return;
            _treeOptions = new()
            {
                SelectionMode = ReturnMode == "2" ? ClayTreeSelectionMode.Multiple : ClayTreeSelectionMode.Single,
                IsChecked = node => node.RawId is int key && _checked.Contains(key),
                ToggleCheckedAsync = ToggleCheckedAsync,
                EnableDragDrop = ReturnMode is null && roots is null && access.TerritoryUpdate,
                ReparentNodeAsync = ReparentNodeAsync,
                TreeId = "territorial-search", SelectSql = TerritorialEntityData.TreeSource,
                HierarchyMode = ClayTreeHierarchyMode.ParentKey,
                Schema = new() { IdColumn = "Id", TextColumn = "Text", ParentColumn = "ParentId", LeftColumn = "Left", RightColumn = "Right" },
                LoadStatePathAsync = key => TerritorialEntityRoutes.TryPositiveId(key, out var id)
                    ? TerritorialEntityData.ReadPathAsync(Db, id, roots, _lifetime.Token)
                    : Task.FromResult<IReadOnlyList<string>>([]),
                PersistExpandedState = true, OrderBy = "[Text], [Id]", ShowLines = true, CustomMenuItems = actions,
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

    private static ClayTreeNode ToPaneNode(TerritorialEntityLookup item) => new()
    {
        Id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RawId = item.Id,
        Text = item.Name,
        Raw = new Dictionary<string, object?> { ["Type"] = item.Type },
    };

    private Task SelectNodeAsync(ClayTreeNode node)
    {
        if (node.RawId is not int id || id <= 0) return Task.CompletedTask;
        _active = node;
        _paneRefreshError = null;
        if (IsCity(node)) _phoneCodes = true;
        UpdateGrid(id);
        return SavePaneAsync();
    }

    private void ResetNode()
    {
        _active = null;
        _gridUrl = null;
        _phoneCodes = false;
        _paneRefreshError = null;
    }

    private async Task ShowPhoneCodes(bool enabled)
    {
        if (_active?.RawId is not int id) return;
        _phoneCodes = enabled;
        _paneRefreshError = null;
        UpdateGrid(id);
        await SavePaneAsync();
    }

    private void UpdateGrid(int id) => _gridUrl = Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Grid(
        _phoneCodes ? Запросы.Телефонные_коды : Запросы.Территории, id)).AbsoluteUri;

    private Task OpenRootTerritoryAsync() => OpenNewTerritoryAsync(null);

    private async Task OpenNewTerritoryAsync(int? parent)
    {
        if (!_canAddTerritory || _loading || _disposed) return;
        var generation = _generation;
        await JS.InvokeVoidAsync("clayFormWindow.registerRefresh", _lifetime.Token, _registrationId,
            _reference ??= DotNetObjectReference.Create(this));
        if (_disposed || generation != _generation) return;
        var route = TerritorialEntityRoutes.TerritoryEdit(parent ?? 0, addChild: parent.HasValue);
        if (!await WindowService.OpenAsync(Navigation.ToAbsoluteUri(route).AbsoluteUri,
                $"territory-create-{parent ?? 0}", _lifetime.Token))
            _message = "Браузер заблокировал окно территории.";
    }

    private async Task OpenTerritoryAsync(ClayTreeNode node)
    {
        if (node.RawId is not int id || id <= 0) return;
        await JS.InvokeVoidAsync("clayFormWindow.registerRefresh", _lifetime.Token, _registrationId,
            _reference ??= DotNetObjectReference.Create(this));
        if (!await WindowService.OpenAsync(Navigation.ToAbsoluteUri(TerritorialEntityRoutes.TerritoryEdit(id)).AbsoluteUri,
                "territory-form", _lifetime.Token))
            _message = "Браузер заблокировал окно территории.";
    }

    private async Task ToggleCheckedAsync(ClayTreeNode node)
    {
        if (_returning || _selectionStorageKey is null || node.RawId is not int id || id <= 0) return;
        if (!_checked.Remove(id))
        {
            if (_checked.Count >= 1000) { _message = "Можно выбрать до 1000 территорий."; return; }
            _checked.Add(id);
        }
        try { await JS.InvokeVoidAsync("sessionStorage.setItem", _lifetime.Token, _selectionStorageKey, string.Join(',', _checked)); }
        catch (JSException) { _message = "Выбор доступен в окне, но сохранить его в браузере не удалось."; }
        StateHasChanged();
    }

    private async Task ReturnMultipleAsync()
    {
        if (_returning || _selectionStorageKey is null || ReturnMode != "2") return;
        var ids = _checked.ToArray();
        var generation = _generation;
        _returning = true;
        try
        {
            if (!TerritorialEntityRoutes.TryRoots(RootIds, Root, Parent, out var roots) ||
                !await TerritorialEntityData.SelectionInScopeAsync(Db, ids, roots, _lifetime.Token))
            {
                _message = "Одна из выбранных территорий больше недоступна.";
                return;
            }
            if (generation != _generation || _disposed) return;
            if (!await JS.InvokeAsync<bool>("claySelectAdvancedSearch.returnValue", _lifetime.Token, RequestId, ids))
                _message = "Не удалось вернуть выбор в вызывающее окно.";
        }
        catch (Exception) when (!_disposed) { _message = "Не удалось проверить или передать выбранные территории."; }
        finally { _returning = false; }
    }

    Task<ClayTreeState?> Clayzor.Lib.Web.Controls.Components.Tree.State.IClayTreeStateStore.LoadAsync(string treeId, CancellationToken ct) =>
        Task.FromResult(_viewState is null ? null : new ClayTreeState
        {
            LastExpandedId = _viewState.Anchor?.ToString(),
            SelectedIds = _viewState.Selected is int selected ? [selected.ToString()] : [],
        });

    async Task Clayzor.Lib.Web.Controls.Components.Tree.State.IClayTreeStateStore.SaveAsync(string treeId, ClayTreeState state, CancellationToken ct)
    {
        if ((!_persistView && _windowViewKey is null) || _total.HasValue || _disposed) return;
        _viewState = new(int.TryParse(state.LastExpandedId, out var anchor) ? anchor : null,
            int.TryParse(state.SelectedIds.FirstOrDefault(), out var selected) ? selected : null, _phoneCodes);
        await PersistViewAsync(_viewState);
    }

    private async Task SavePaneAsync()
    {
        if ((!_persistView && _windowViewKey is null) || _total.HasValue || _disposed) return;
        _viewState = new(_viewState?.Anchor, _active?.RawId as int?, _phoneCodes);
        await PersistViewAsync(_viewState);
    }

    private async Task PersistViewAsync(TerritoryViewState state)
    {
        try
        {
            if (_windowViewKey is { } key) await JS.InvokeVoidAsync("sessionStorage.setItem", _lifetime.Token, key, state.Serialize());
            else await TerritorialEntityData.SaveViewStateAsync(Db, _clid, state, _lifetime.Token);
        }
        catch (Exception) when (!_disposed) { _message = "Не удалось сохранить состояние дерева."; }
    }

    private async Task ResetViewAsync()
    {
        ResetNode();
        _viewState = null;
        await SavePaneAsync();
    }

    private bool _returning;

    private async Task ReturnAsync(ClayTreeNode node)
    {
        if (_returning || node.RawId is not int id || id <= 0 ||
            !TerritorialEntityRoutes.IsLocalReturn(RequestId, ReturnOrigin, Navigation.BaseUri)) return;
        var generation = _generation;
        _returning = true;
        try
        {
            if (!TerritorialEntityRoutes.TryRoots(RootIds, Root, Parent, out var roots) ||
                !await TerritorialEntityData.SelectionInScopeAsync(Db, [id], roots, _lifetime.Token))
            {
                if (!_disposed && generation == _generation) _message = "Выбранная территория больше недоступна.";
                return;
            }
            if (_disposed || generation != _generation) return;
            if (!await JS.InvokeAsync<bool>("claySelectAdvancedSearch.returnValue", _lifetime.Token, RequestId, id))
                _message = "Не удалось передать выбранную территорию в вызывающее окно.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) when (!_disposed && generation == _generation)
        {
            _message = "Не удалось проверить или передать выбранную территорию.";
        }
        finally { _returning = false; }
    }

    private async Task CloseAsync() =>
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token);

    private async Task ReloadTreeAsync() =>
        await RefreshViewAsync(_generation, reloadTree: true);

    private bool IsCurrent(long generation) => !_disposed && generation == _generation;

    private async Task RefreshViewAsync(long generation, bool reloadTree)
    {
        await _viewRefresh.WaitAsync();
        try
        {
            if (!IsCurrent(generation)) return;
            await RefreshActivePaneAsync(generation);
            if (!reloadTree || !IsCurrent(generation) || _tree is not IClayTreeView tree) return;

            // ReloadAsync reloads roots and would replace the current filtered projection.
            _treeRefreshError = _total.HasValue
                ? "Чтобы обновить отфильтрованное дерево, примените фильтр повторно."
                : null;
            if (_treeRefreshError is not null) return;
            try { await tree.ReloadAsync(); }
            catch (Exception)
            {
                if (IsCurrent(generation)) _treeRefreshError = "Не удалось обновить дерево территорий. Повторите обновление.";
            }
        }
        finally { _viewRefresh.Release(); }
    }

    private async Task RefreshActivePaneAsync(long generation)
    {
        var active = _active;
        if (active?.RawId is not int id) return;
        string? expectedUrl = null;
        try
        {
            if (!TerritorialEntityRoutes.TryRoots(RootIds, Root, Parent, out var roots)) return;
            var item = await TerritorialEntityData.SelectionInScopeAsync(Db, [id], roots, _lifetime.Token)
                ? await TerritorialEntityData.FindTerritoryAsync(Db, id, _lifetime.Token)
                : null;
            if (!IsCurrent(generation) || !ReferenceEquals(active, _active)) return;

            if (item is null)
            {
                ResetNode();
                if (_viewState?.Selected == id)
                    _viewState = _viewState with { Selected = null, PhoneCodes = false };
                return;
            }

            var previousUrl = _gridUrl;
            active = _active = ToPaneNode(item);
            if (IsCity(active)) _phoneCodes = true;
            UpdateGrid(id);
            _paneRefreshError = null;
            // A changed URL creates a new frame. Otherwise ask the current grid to reload its data.
            if (_gridUrl is not { } url || url != previousUrl) return;
            expectedUrl = url;
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", _lifetime.Token,
                "./js/territorialEntitySearch.js");
            if (IsCurrent(generation) && ReferenceEquals(active, _active) && _gridUrl == url)
            {
                if (!await module.InvokeAsync<bool>("refreshMetadataFrame", _lifetime.Token, _gridFrame, url))
                    SetPaneRefreshError(generation, active, url);
            }
        }
        catch (Exception)
        {
            SetPaneRefreshError(generation, active, expectedUrl);
        }
    }

    private void SetPaneRefreshError(long generation, ClayTreeNode active, string? expectedUrl)
    {
        if (IsCurrent(generation) && ReferenceEquals(active, _active) &&
            (expectedUrl is null || _gridUrl == expectedUrl))
            _paneRefreshError = "Не удалось обновить список и сведения о территории. Повторите обновление.";
    }

    /// <summary>Обновляет дерево и данные выбранной территории в вызвавшем окне после сохранения карточки.</summary>
    [JSInvokable]
    public Task RefreshFromChildAsync() => InvokeAsync(async () =>
    {
        var generation = _generation;
        await RefreshViewAsync(generation, reloadTree: true);
        if (IsCurrent(generation)) StateHasChanged();
    });

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
