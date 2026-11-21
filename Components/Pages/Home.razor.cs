using Clayzor.Lib.Web.Controls.Components.Grid;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages;

/// <summary>Главная страница Inventory: общее меню и один динамический ClayGrid.</summary>
public partial class Home
{
    [CascadingParameter] private Layout.MainLayout? ParentLayout { get; set; }

    private readonly ClayGridOptions _gridOptions = new()
    {
        Dynamic = true,
        Id = "clay-grid-root",
        ShowPagination = true,
        ColumnMenuMode = ColumnMenuMode.Always,
        SelectVisible = true,
        ShowPrint = true,
        ShowExcel = true,
        FilterTrayExpandedByDefault = true,
        FillAvailableHeight = true,
        CompactLayout = true,
    };

    private string _pageTitle = string.Empty;
    private int? _titleQueryId;

    /// <summary>Строковый код запроса из URL.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }

    /// <summary>Признак скрытия меню из embedding-ссылки.</summary>
    [SupplyParameterFromQuery(Name = "hidemenu")] public string? HideMenu { get; set; }

    /// <summary>Признак скрытия заголовка грида.</summary>
    [SupplyParameterFromQuery(Name = "hidetitle")] public string? HideTitle { get; set; }

    /// <summary>Legacy return-контекст, скрывающий меню.</summary>
    [SupplyParameterFromQuery(Name = "return")] public string? Return { get; set; }

    private int? CurrentQueryId => int.TryParse(Id, out var id) && id > 0 ? id : null;
    private bool HasInvalidQueryId => !string.IsNullOrWhiteSpace(Id) && CurrentQueryId is null;
    private bool HideNavigationMenu => Return is not null || IsLegacyFlag(HideMenu);

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_titleQueryId != CurrentQueryId)
        {
            _titleQueryId = CurrentQueryId;
            _pageTitle = AppSettings.ApplicationName;
        }
        _gridOptions.DynamicGridId = CurrentQueryId;
        _gridOptions.ShowTitle = !IsLegacyFlag(HideTitle);
    }

    private void OnGridTitleChanged(string title) => _pageTitle = title;

    private static bool IsLegacyFlag(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
