using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Dapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Default;

/// <summary>Жизненный цикл и ClayGrid-операции обеих read-only форм выбора.</summary>
public abstract class PhoneFinderPageBase<TRow, TKey> : ComponentBase, IDisposable where TRow : class
{
    private const int DefaultPageSize = 35;
    private CancellationTokenSource? _contextLifetime;
    private CancellationTokenSource? _read;
    private ClayDataQuery _query = new() { PageSize = DefaultPageSize };
    private string? _uri;
    private bool _loadPending;
    private bool _disposed;
    private bool _returning;
    private bool _sent;

    [Inject] protected DbManager Db { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] protected AuthenticationStateProvider Authentication { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "return")] protected string? ReturnMode { get; set; }
    [SupplyParameterFromQuery(Name = "requestId")] protected string? RequestId { get; set; }
    [SupplyParameterFromQuery(Name = "returnOrigin")] protected string? ReturnOrigin { get; set; }
    [SupplyParameterFromQuery(Name = "mvc")] protected string? LegacyMode { get; set; }
    [SupplyParameterFromQuery(Name = "exclude")] protected string? Exclude { get; set; }
    [SupplyParameterFromQuery(Name = "q")] protected string? Search { get; set; }

    protected Employee? CurrentUser { get; private set; }
    protected IReadOnlyList<TRow> Rows { get; private set; } = [];
    protected int Total { get; private set; }
    protected int Page => _query.PageNumber;
    protected bool Loading { get; private set; } = true;
    protected bool CanRetry { get; private set; }
    protected string? Error { get; set; }
    protected string? ReturnMessage { get; private set; }
    protected IReadOnlyList<int> Excluded { get; private set; } = [];
    protected bool CanChoose => IsModernReturn && CurrentUser is not null && !Loading && !(_returning || _sent) && Error is null;
    private bool IsModernReturn => ReturnMode == "1" && LegacyMode is null && Guid.TryParseExact(RequestId, "N", out _) &&
        string.Equals(ReturnOrigin, new Uri(Navigation.BaseUri).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    protected ClayGridOptions Options { get; private set; } = null!;
    protected abstract string GridId { get; }
    protected abstract string Title { get; }
    protected abstract string DefaultOrder { get; }
    protected abstract string[] SearchColumns { get; }
    protected abstract IReadOnlyDictionary<string, ColumnType> Columns { get; }
    protected abstract bool TryReadContext();
    protected abstract (string Sql, DynamicParameters Parameters) BuildSource();
    protected abstract TKey GetKey(TRow row);
    protected abstract Task<bool> IsAvailableAsync(TKey key, CancellationToken ct);
    protected virtual Task LoadLookupsAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_uri == Navigation.Uri) return;
        _uri = Navigation.Uri;
        _contextLifetime?.Cancel();
        _contextLifetime?.Dispose();
        _contextLifetime = new();
        _sent = _returning = false;
        Rows = [];
        Total = 0;
        CurrentUser = null;
        Error = null;
        CanRetry = _loadPending = Loading = PhoneFinderRoutes.TryExclusions(Exclude, out var excluded) && TryReadContext();
        Excluded = excluded;
        if (!_loadPending) Error = "Некорректные параметры поиска. Проверьте ссылку на окно выбора.";
        ReturnMessage = IsModernReturn ? null : ReturnMode is null && LegacyMode is null
            ? "Окно открыто для просмотра. Для выбора откройте его из поля формы."
            : "Возврат по этой ссылке недоступен. Откройте окно выбора из Clayzor.";
        Options = new()
        {
            Id = GridId,
            Title = Title,
            PageSize = DefaultPageSize,
            InitialSearchText = Search,
            FilterColumnTypes = Columns,
            ColumnMenuMode = ColumnMenuMode.Always,
            ShowAddButton = false,
            CompactLayout = true,
        };
        _query = new() { PageSize = DefaultPageSize, SearchText = Search };
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_loadPending || _contextLifetime is null) return;
        _loadPending = false;
        var ct = _contextLifetime.Token;
        try
        {
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            var user = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, ct);
            if (ct.IsCancellationRequested) return;
            if (user is null) { Error = "Не удалось подтвердить текущего сотрудника."; return; }
            await LoadLookupsAsync(ct);
            if (ct.IsCancellationRequested) return;
            CurrentUser = user;
            await ReadAsync(_query);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { Error = "Не удалось загрузить список выбора."; }
        finally
        {
            if (!_disposed && !ct.IsCancellationRequested)
            {
                Loading = false;
                StateHasChanged();
            }
        }
    }

    protected async Task ReadAsync(ClayDataQuery query)
    {
        if (CurrentUser is null || _contextLifetime is null) return;
        _read?.Cancel();
        _read?.Dispose();
        _read = CancellationTokenSource.CreateLinkedTokenSource(_contextLifetime.Token);
        var ct = _read.Token;
        _query = query;
        Loading = true;
        Error = null;
        try
        {
            var (sql, parameters) = BuildSource();
            var where = BuildWhere(query, parameters, query.CompositeFilter);
            var order = query.BuildOrderBy(DefaultOrder, Columns.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (query.SortColumns.Count != 0 && order != DefaultOrder) order += ", " + DefaultOrder;
            var size = Math.Clamp(query.PageSize, 1, 999);
            var page = await PhoneFinderData.ReadPageAsync<TRow>(Db, sql, where, order, parameters, query.PageNumber, size, ct);
            if (ct.IsCancellationRequested) return;
            var last = Math.Max(1, (int)Math.Ceiling((double)page.Total / size));
            if (query.PageNumber > last)
            {
                query.PageNumber = last;
                page = await PhoneFinderData.ReadPageAsync<TRow>(Db, sql, where, order, parameters, last, size, ct);
            }
            if (!ct.IsCancellationRequested) { Rows = page.Rows; Total = page.Total; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Rows = [];
            Total = 0;
            Error = "Не удалось получить строки. Повторите загрузку.";
        }
        finally { if (!ct.IsCancellationRequested) Loading = false; }
    }

    protected async Task<DistinctValuesResult> ReadValuesAsync(string column, ClayDataQuery query, int limit)
    {
        if (CurrentUser is null || _contextLifetime is null || !Columns.TryGetValue(column, out var type)) return new();
        var (sql, parameters) = BuildSource();
        var filter = WithoutColumn(query.CompositeFilter, column);
        var where = BuildWhere(query, parameters, filter);
        var raw = await PhoneFinderData.ReadDistinctAsync(Db, sql, where, column, parameters, limit + 3, _contextLifetime.Token);
        var values = raw.Where(value => value is not null && !(type == ColumnType.Text && value is "")).ToArray();
        var capped = values.Length > limit;
        return new() { Values = capped ? [] : values, Capped = capped, HasBlanks = values.Length != raw.Count, TotalDistinct = capped ? 0 : values.Length };
    }

    private string? BuildWhere(ClayDataQuery query, DynamicParameters parameters, ClayFilterGroupNode? filter)
    {
        parameters.Add("search", $"%{query.SearchText}%");
        return ClayDataQuery.CombineWhere(query.BuildWhereClause(SearchColumns),
            ClayCompositeSqlBuilder.Build(filter, parameters, Columns.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }

    private static ClayFilterGroupNode? WithoutColumn(ClayFilterGroupNode? root, string column)
    {
        if (root is null) return null;
        var nodes = root.Nodes.Select(node => node is ClayFilterGroupNode group ? WithoutColumn(group, column) : node)
            .Where(node => node is not null && node switch
            {
                ColumnFilter condition => !string.Equals(condition.Column, column, StringComparison.OrdinalIgnoreCase),
                ValueFilter value => !string.Equals(value.Column, column, StringComparison.OrdinalIgnoreCase),
                _ => true,
            }).Cast<IClayFilterNode>().ToList();
        return nodes.Count == 0 ? null : new() { Logic = root.Logic, Nodes = nodes };
    }

    protected async Task ChooseAsync(TRow row)
    {
        if (!CanChoose || _contextLifetime is null) return;
        var ct = _contextLifetime.Token;
        var request = RequestId;
        _returning = true;
        try
        {
            var key = GetKey(row);
            if (!await IsAvailableAsync(key, ct)) { Error = "Выбранная запись стала недоступна. Повторите поиск."; return; }
            if (ct.IsCancellationRequested) return;
            _sent = await JS.InvokeAsync<bool>("claySelectAdvancedSearch.returnValue", ct, request, key);
            if (!_sent) ReturnMessage = "Вызывающее окно недоступно. Откройте поиск из поля формы.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { Error = "Не удалось проверить или вернуть выбранную запись."; }
        finally { if (!ct.IsCancellationRequested) _returning = false; }
    }

    protected Task RetryAsync() => !CanRetry ? Task.CompletedTask : CurrentUser is null ? ResetAsync() : ReadAsync(_query);

    private Task ResetAsync() { Error = null; Loading = _loadPending = true; return Task.CompletedTask; }
    protected Task CloseAsync() => JS.InvokeVoidAsync("claySelectAdvancedSearch.close").AsTask();

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _read?.Cancel();
        _read?.Dispose();
        _contextLifetime?.Cancel();
        _contextLifetime?.Dispose();
    }
}
