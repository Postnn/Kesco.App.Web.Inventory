using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Dapper;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

internal sealed class TerritorialEntityTreeSource(DbManager db, IReadOnlyList<int>? roots, Action<int?> setFilterTotal)
    : IClayTreeDataSource
{
    private bool _filterFollowup;

    public async Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
    {
        try
        {
            // The filtered result already contains the matching roots and all in-scope ancestors.
            if (request.Parent is null && _filterFollowup)
            {
                _filterFollowup = false;
                return new([]);
            }
            var parentId = request.Parent?.RawId is int id ? id : (int?)null;
            var rows = await TerritorialEntityData.ReadTreeLevelAsync(db, parentId, roots, ct);
            if (request.Parent is null) setFilterTotal(null);
            return new(rows.Select(row => Map(row, request.Parent)).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new([], "Не удалось загрузить территории. Повторите загрузку.");
        }
    }

    public async Task<ClayTreeLoadResult> LoadFilteredAsync(string whereClause, DynamicParameters dp, int max, CancellationToken ct = default)
    {
        try
        {
            var result = await TerritorialEntityData.ReadTreeFilterAsync(db, whereClause, dp, roots, Math.Min(max, 100), ct);
            ct.ThrowIfCancellationRequested();
            _filterFollowup = true;
            setFilterTotal(result.Total);
            return new(result.Rows.Select(row => Map(row, null, filtered: true)).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new([], "Не удалось отфильтровать территории. Повторите поиск.");
        }
    }

    private static ClayTreeNode Map(TerritorialEntityTreeRow row, ClayTreeNode? parent, bool filtered = false) => new()
    {
        Id = row.Id.ToString(CultureInfo.InvariantCulture), RawId = row.Id, ParentId = row.ParentId,
        Text = row.Text, Left = row.Left, Right = row.Right, HasChildren = row.HasChildren,
        Level = parent is null ? 0 : parent.Level + 1, Parent = parent,
        IsMatch = row.IsMatch, HasMatchChildren = row.HasMatchChildren,
        IsLoaded = filtered && row.HasMatchChildren, IsExpanded = filtered && row.HasMatchChildren,
        Raw = new Dictionary<string, object?> { [nameof(row.Type)] = row.Type },
    };
}
