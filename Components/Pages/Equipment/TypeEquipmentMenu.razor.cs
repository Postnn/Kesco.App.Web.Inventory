using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Dapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Выбор типа оборудования и просмотр моделей только выбранного типа.</summary>
public partial class TypeEquipmentMenu : IDisposable
{
    private const int ModelSettingsClientId = 87;
    private const string DefaultTypeOrder = EquipmentType.IdColumnName + " ASC";
    private static readonly HashSet<string> TypeFilterColumns = new(
        [EquipmentType.IdColumnName, EquipmentType.MenuNameColumnName], StringComparer.Ordinal);
    private static readonly HashSet<string> TypeNameContextColumns = new([EquipmentType.IdColumnName], StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ClayGridOptions _typesOptions = new()
    {
        Id = "equipment-type-list",
        ShowToolbar = true,
        ShowTitle = false,
        ShowAddButton = false,
        ShowPagination = false,
        AllowSorting = true,
        AllowColumnReorder = true,
        SearchColumns = [EquipmentType.MenuNameColumnName],
        ColumnMenuMode = ColumnMenuMode.Hidden,
        FilterTrayExpandedByDefault = true,
        FillAvailableHeight = true,
        CompactLayout = true,
        Breakpoint = Breakpoint.None,
        PageSize = 1000,
        EmptyText = "Типы оборудования не найдены",
        FilterColumnTypes = new Dictionary<string, ColumnType>
        {
            [EquipmentType.IdColumnName] = ColumnType.Number,
        },
    };

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;

    private Employee? CurrentUser { get; set; }
    private EquipmentType[] _types = [];
    private EquipmentType? _selected;
    private ClayGridOptions? _modelsOptions;
    private string? _error;
    private bool _loading = true;
    private bool _disposed;
    private long _typesRequestVersion;

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) => firstRender ? LoadTypesAsync() : Task.CompletedTask;

    private async Task LoadTypesAsync()
    {
        if (_disposed) return;
        var version = ++_typesRequestVersion;
        _loading = true;
        _error = null;
        try
        {
            var cancellationToken = _lifetime.Token;
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            if (!IsCurrentTypesRequest(version)) return;
            var employee = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, cancellationToken);
            if (!IsCurrentTypesRequest(version)) return;
            CurrentUser = employee;
            if (employee is null)
            {
                _error = "Не удалось подтвердить текущего сотрудника.";
                return;
            }

            var rows = await EquipmentType.ReadMenuAsync(Db, cancellationToken, employee: employee);
            if (!IsCurrentTypesRequest(version)) return;
            _types = rows.ToArray();
            _typesOptions.PageSize = Math.Max(1000, _types.Length);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception)
        {
            if (IsCurrentTypesRequest(version))
                _error = "Не удалось загрузить типы оборудования. Повторите загрузку.";
        }
        finally
        {
            if (IsCurrentTypesRequest(version))
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task OnTypesQueryChangedAsync(ClayDataQuery query)
    {
        if (_disposed) return;
        var version = ++_typesRequestVersion;
        try
        {
            var parameters = new DynamicParameters();
            parameters.Add("search", $"%{query.SearchText}%");
            var searchPredicate = query.BuildWhereClause(_typesOptions.SearchColumns);
            var filterPredicate = ClayCompositeSqlBuilder.Build(query.CompositeFilter, parameters, TypeFilterColumns);
            var predicate = ClayDataQuery.CombineWhere(searchPredicate, filterPredicate);
            var orderBy = query.BuildOrderBy(DefaultTypeOrder, TypeFilterColumns);
            var rows = await EquipmentType.ReadMenuAsync(Db, _lifetime.Token, predicate, parameters, orderBy, CurrentUser);
            if (!IsCurrentTypesRequest(version)) return;
            _types = rows.ToArray();
            _typesOptions.PageSize = Math.Max(1000, _types.Length);
            _error = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception)
        {
            if (IsCurrentTypesRequest(version))
                _error = "Не удалось применить фильтр или сортировку типов оборудования.";
        }
        finally
        {
            if (IsCurrentTypesRequest(version))
                _loading = false;
        }
    }

    private bool IsCurrentTypesRequest(long version) => !_disposed && version == _typesRequestVersion;

    private async Task<DistinctValuesResult> LoadTypeNamesAsync(string column, ClayDataQuery query, int limit)
    {
        if (_disposed || CurrentUser is not { } employee || column != EquipmentType.MenuNameColumnName)
            return new DistinctValuesResult();

        var parameters = new DynamicParameters();
        parameters.Add("search", $"%{query.SearchText}%");
        var search = query.BuildWhereClause(_typesOptions.SearchColumns);
        var filter = ClayCompositeSqlBuilder.Build(query.CompositeFilter, parameters, TypeNameContextColumns);
        var predicate = ClayDataQuery.CombineWhere(search, filter);
        // NULL сортируется первым. Оставляем место для него и для признака ограничения.
        var values = (await EquipmentType.ReadDistinctMenuNamesAsync(
            Db, employee, limit + 2, predicate, parameters, _lifetime.Token)).ToArray();
        var nonBlank = values.OfType<string>().ToArray();
        var hasBlanks = values.Contains(null);
        return nonBlank.Length > limit
            ? new DistinctValuesResult { Capped = true, HasBlanks = hasBlanks }
            : new DistinctValuesResult { Values = nonBlank, HasBlanks = hasBlanks, TotalDistinct = nonBlank.Length };
    }

    private bool IsSelected(EquipmentType type) => _selected?.Id == type.Id;
    private string? GetTypeRowClass(EquipmentType type) => IsSelected(type) ? "type-equipment-selected" : null;

    private void SelectType(EquipmentType type)
    {
        if (IsSelected(type)) return;

        _selected = type;
        _modelsOptions = new ClayGridOptions
        {
            Id = "equipment-model-list",
            Dynamic = true,
            DynamicGridId = (int)Запросы.Модели_оборудования,
            DynamicContext = new(ModelSettingsClientId, EquipmentType.IdColumnName, type.Id, "codetype"),
            ShowTitle = false,
            ShowPagination = true,
            FilterTrayExpandedByDefault = true,
            ColumnMenuMode = ColumnMenuMode.Always,
            SelectVisible = true,
            ShowPrint = true,
            ShowExcel = true,
            FillAvailableHeight = true,
            CompactLayout = true,
            Breakpoint = Breakpoint.None,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
