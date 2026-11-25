using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Dapper;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages.Default;

/// <summary>Расширенный выбор номера для черновика оборудования.</summary>
public partial class FinderTel
{
    private FinderTelContext _context = new(null, null, null, null, null);
    private IReadOnlyDictionary<int, FinderTelType> _types = new Dictionary<int, FinderTelType>();
    protected override string GridId => "finder-tel-grid";
    protected override string Title => "Выбор телефонного номера";
    protected override string DefaultOrder => "FullNumber, ShortNumber, Id, ConnectionEquipmentId";
    protected override string[] SearchColumns { get; } = ["FullNumber", "ShortNumber", "Subscriber"];
    protected override IReadOnlyDictionary<string, ColumnType> Columns { get; } = new Dictionary<string, ColumnType>
    {
        [nameof(FinderTelRow.PhoneTypeId)] = ColumnType.Number, [nameof(FinderTelRow.FullNumber)] = ColumnType.Text,
        [nameof(FinderTelRow.ShortNumber)] = ColumnType.Text, [nameof(FinderTelRow.Subscriber)] = ColumnType.Text,
        [nameof(FinderTelRow.ConnectionEquipmentId)] = ColumnType.Number,
    };

    [SupplyParameterFromQuery(Name = "fLocation")] public string? LocationQuery { get; set; }
    [SupplyParameterFromQuery(Name = "fEmployee")] public string? EmployeeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "fTypeEquipment")] public string? TypeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "fMainNumber")] public string? MainNumberQuery { get; set; }
    [SupplyParameterFromQuery(Name = "fEquipment")] public string? EquipmentQuery { get; set; }
    [Inject] private IConfiguration Configuration { get; set; } = null!;

    protected override bool TryReadContext()
    {
        if (!PhoneFinderRoutes.TryId(LocationQuery, out var location) || !PhoneFinderRoutes.TryId(EmployeeQuery, out var employee) ||
            !PhoneFinderRoutes.TryId(TypeQuery, out var type) || !PhoneFinderRoutes.TryId(MainNumberQuery, out var phone) ||
            !PhoneFinderRoutes.TryId(EquipmentQuery, out var equipment)) return false;
        _context = new(location, employee, type, phone, equipment);
        return true;
    }

    protected override (string Sql, DynamicParameters Parameters) BuildSource() => PhoneFinderData.BuildTelQuery(_context, Excluded);
    protected override int GetKey(FinderTelRow row) => row.Id;
    protected override async Task<bool> IsAvailableAsync(int key, CancellationToken ct) =>
        await PhoneFinderData.ResolveTelAsync(Db, _context, Excluded, key, ct) is not null;

    protected override async Task LoadLookupsAsync(CancellationToken ct)
    {
        var types = await PhoneFinderData.ReadTypesAsync(Db, ct);
        if (!ct.IsCancellationRequested) _types = types.ToDictionary(type => type.Id);
    }

    private string TypeLabel(int id) => _types.GetValueOrDefault(id)?.Name is { Length: > 0 } name
        ? name : id.ToString(CultureInfo.InvariantCulture);

    private string? TypeIcon(int id)
    {
        var icon = _types.GetValueOrDefault(id)?.Icon?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(icon) || icon.StartsWith('/') || icon.Contains(':') || icon.Contains("..", StringComparison.Ordinal)
            ? null : InventoryMenuUrlBuilder.ResolveIcon(icon, Configuration["URI_styles"]);
    }
}
