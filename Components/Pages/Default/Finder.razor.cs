using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Dapper;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages.Default;

/// <summary>Расширенный выбор оборудования для телефонного номера.</summary>
public partial class Finder
{
    private FinderEquipmentContext _context = new(null, null, null, null, null);
    protected override string GridId => "finder-equipment-grid";
    protected override string Title => "Выбор оборудования";
    protected override string DefaultOrder => "Model, EquipmentId, ConnectionEquipmentId, LocationPath, SerialNumber";
    protected override string[] SearchColumns { get; } = ["Model", "SerialNumber", "LocationPath"];
    protected override IReadOnlyDictionary<string, ColumnType> Columns { get; } = new Dictionary<string, ColumnType>
    {
        [nameof(FinderEquipmentRow.Model)] = ColumnType.Text, [nameof(FinderEquipmentRow.EquipmentId)] = ColumnType.Number,
        [nameof(FinderEquipmentRow.SerialNumber)] = ColumnType.Text, [nameof(FinderEquipmentRow.LocationPath)] = ColumnType.Text,
        [nameof(FinderEquipmentRow.ConnectionEquipmentId)] = ColumnType.Number,
    };

    [SupplyParameterFromQuery(Name = "teltype")] public string? TypeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "station")] public string? StationQuery { get; set; }
    [SupplyParameterFromQuery(Name = "socket")] public string? SocketQuery { get; set; }
    [SupplyParameterFromQuery(Name = "employee")] public string? EmployeeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "telid")] public string? PhoneQuery { get; set; }
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;

    protected override bool TryReadContext()
    {
        if (!PhoneFinderRoutes.TryId(TypeQuery, out var type) || !PhoneFinderRoutes.TryId(StationQuery, out var station) ||
            !PhoneFinderRoutes.TryId(SocketQuery, out var socket) || !PhoneFinderRoutes.TryId(EmployeeQuery, out var employee) ||
            !PhoneFinderRoutes.TryId(PhoneQuery, out var phone)) return false;
        _context = new(type, station, socket, employee, phone);
        return true;
    }

    protected override (string Sql, DynamicParameters Parameters) BuildSource() => PhoneFinderData.BuildEquipmentQuery(_context, Excluded);
    protected override PhoneEquipmentKey GetKey(FinderEquipmentRow row) => row.Key;
    protected override async Task<bool> IsAvailableAsync(PhoneEquipmentKey key, CancellationToken ct) =>
        await PhoneFinderData.ResolveEquipmentAsync(Db, _context, Excluded, key, ct) is not null;

    private string EquipmentUrl(int id) => Navigation.ToAbsoluteUri($"Equipment/EquipmentForm?id={id}").AbsoluteUri;

    private async Task OpenEquipmentAsync(int id)
    {
        if (!await Windows.OpenAsync(EquipmentUrl(id), $"equipment-form:{id}")) Error = "Браузер заблокировал открытие карточки.";
    }
}
