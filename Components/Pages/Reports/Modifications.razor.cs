using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Employees;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kesco.App.Web.Inventory.Components.Pages.Reports;

/// <summary>Отчёт по изменениям, зафиксированным в базе Inventory.</summary>
public partial class Modifications : IDisposable
{
    private const int PageSize = 25;
    private static readonly ClaySelectOptions EmployeeOptions = new() { Label = "Изменил", AutoSelectSingle = true };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private ClayPeriod _period = new(ClayPeriodKind.Day, DateTime.Today, DateTime.Today);
    private IReadOnlyList<KeyValuePair<int, string>> _options = [];
    private IReadOnlySet<int> _selected = new HashSet<int>();
    private IReadOnlyList<Section> _sections = [];
    private int? _employeeId;
    private Snapshot? _snapshot;
    private string? _message;
    private bool _loading = true;
    private bool _ready;
    private bool _busy;
    private bool _disposed;
    private Employee? CurrentUser { get; set; }
    private bool FiltersChanged => _snapshot is { } snapshot &&
        (snapshot.Period != _period || snapshot.EmployeeId != _employeeId || !_selected.SetEquals(snapshot.Ids));

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private EmployeeSelectDataSource Employees { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            var definitions = await LoadDefinitionsAsync();
            _options = definitions.Select(item => new KeyValuePair<int, string>(item.Id, item.Name)).ToList();
            _selected = _options.Select(item => item.Key).ToHashSet();
            _ready = true;
        }
        catch (Exception) when (!_disposed) { _message = "Не удалось загрузить список разделов отчёта."; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _loading = false; if (!_disposed) StateHasChanged(); }
    }

    private async Task GenerateAsync()
    {
        if (!_ready || _busy) return;
        _message = null;
        _sections = [];
        _snapshot = null;
        if (!_period.IsComplete) { _message = "Не определен период."; return; }
        if (_selected.Count == 0) return;

        var snapshot = new Snapshot(_period, _employeeId, _selected.ToHashSet());
        _busy = true;
        try
        {
            var definitions = (await LoadDefinitionsAsync()).Where(item => snapshot.Ids.Contains(item.Id)).ToList();
            if (definitions.Count != snapshot.Ids.Count)
            {
                _message = "Состав выбранных разделов изменился. Откройте отчёт заново.";
                return;
            }
            List<Section> sections = [];
            foreach (var definition in definitions)
            {
                var section = new Section(definition, _instance);
                section.LinkError = InventoryMenuUrlBuilder.BuildCardLink(definition.FormReference, "0", Config, Navigation.Uri).Error;
                await ReadSectionAsync(section, snapshot, 1);
                sections.Add(section);
            }
            if (_disposed) return;
            _snapshot = snapshot;
            _sections = sections.OrderBy(section => section.Result?.Total > 0 ? 0 : section.Error is null ? 2 : 1).ToList();
        }
        catch (Exception) when (!_disposed) { _message = "Не удалось сформировать отчёт."; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private async Task PageAsync(Section section, int delta)
    {
        if (_busy || _snapshot is not { } snapshot) return;
        _busy = true;
        try
        {
            var current = (await LoadDefinitionsAsync()).SingleOrDefault(item => item.Id == section.Definition.Id);
            if (current != section.Definition)
            {
                section.Result = null;
                section.Error = "Определение раздела изменилось. Сформируйте отчёт заново.";
                return;
            }
            await ReadSectionAsync(section, snapshot, section.Page + delta);
        }
        catch (Exception) when (!_disposed)
        {
            section.Result = null;
            section.Error = "Не удалось проверить права или перечитать раздел.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private async Task ReadSectionAsync(Section section, Snapshot snapshot, int page)
    {
        try
        {
            section.Result = await ModificationReportData.ReadAsync(Db, section.Definition, snapshot.Period.From!.Value,
                snapshot.Period.To!.Value, snapshot.EmployeeId, page, PageSize, _lifetime.Token);
            section.Page = page;
            section.Error = null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            section.Error = error is InvalidDataException ? error.Message : "Не удалось получить данные раздела.";
            section.Result = null;
        }
    }

    private async Task<IEnumerable<ModificationReportDefinition>> LoadDefinitionsAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        CurrentUser = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, _lifetime.Token);
        if (CurrentUser is null) throw new UnauthorizedAccessException();
        return await ModificationReportData.ListAsync(Db, _lifetime.Token);
    }

    private string? EntryLink(Section section, string first, string column, IReadOnlyDictionary<string, object?> row)
    {
        if (column == first && row[column] is { } key)
            return InventoryMenuUrlBuilder.BuildCardLink(section.Definition.FormReference, Convert.ToString(key, CultureInfo.InvariantCulture) ?? "", Config, Navigation.Uri).Href;
        if (column is "ФИО" or "ИзменилФИО" or "Изменил ФИО" && row["Изменил"] is { } employee)
            return InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", Convert.ToString(employee, CultureInfo.InvariantCulture) ?? "", Config, Navigation.Uri).Href;
        return null;
    }

    private static bool IsVisible(string name) => !name.Equals("Изменил", StringComparison.OrdinalIgnoreCase);
    private static bool IsChangedDate(string name) => name.Equals("Изменено", StringComparison.OrdinalIgnoreCase);
    private static string? CellText(IReadOnlyDictionary<string, object?> row, string name) => Convert.ToString(row[name], CultureInfo.CurrentCulture);

    private async Task OpenAsync(string url)
    {
        if (!await Windows.OpenAsync(url, "report:" + url, _lifetime.Token)) _message = "Браузер заблокировал открытие карточки.";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record Snapshot(ClayPeriod Period, int? EmployeeId, IReadOnlySet<int> Ids);

    private sealed class Section(ModificationReportDefinition definition, string instance)
    {
        public ModificationReportDefinition Definition { get; } = definition;
        public ClayGridOptions Options { get; } = new()
        {
            Id = $"modifications-{instance}-{definition.Id}", ShowToolbar = false, ShowAddButton = false,
            ShowPagination = false, AllowSorting = false, AllowColumnReorder = false, EnableValueFilter = false,
            ColumnMenuMode = ColumnMenuMode.Hidden, EmptyText = "Ничего не найдено."
        };
        public ModificationReportPage? Result { get; set; }
        public int Page { get; set; } = 1;
        public string? Error { get; set; }
        public string? LinkError { get; set; }
    }
}
