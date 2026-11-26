using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages;

/// <summary>Главная страница Inventory: общее меню и один динамический ClayGrid.</summary>
public partial class Home
{
    private const int EquipmentForModelClientId = 95;
    private const string EquipmentModelColumn = "КодМоделиОборудования";

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

    /// <summary>Явный контекст списка оборудования для модели.</summary>
    [SupplyParameterFromQuery(Name = "modelId")] public string? ModelId { get; set; }

    /// <summary>Признак скрытия меню из embedding-ссылки.</summary>
    [SupplyParameterFromQuery(Name = "hidemenu")] public string? HideMenu { get; set; }

    /// <summary>Признак скрытия заголовка грида.</summary>
    [SupplyParameterFromQuery(Name = "hidetitle")] public string? HideTitle { get; set; }

    /// <summary>Legacy return-контекст, скрывающий меню.</summary>
    [SupplyParameterFromQuery(Name = "return")] public string? Return { get; set; }

    private int? CurrentQueryId => int.TryParse(Id, out var id) && id > 0 ? id : null;
    private bool HasModelScope => CurrentQueryId == (int)Запросы.Оборудование && ModelId is not null;
    private int? EquipmentModelContextId => HasModelScope &&
        int.TryParse(ModelId, NumberStyles.None, CultureInfo.InvariantCulture, out var modelId) && modelId > 0
        ? modelId : null;
    private bool HasInvalidModelScope => HasModelScope && EquipmentModelContextId is null;
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
        var modelId = EquipmentModelContextId;
        _gridOptions.DynamicContext = modelId is { } selected
            ? new(EquipmentForModelClientId, EquipmentModelColumn, selected, "modelId")
            : null;
        _gridOptions.ShowAddButton = modelId is null;
        _gridOptions.ShowTitle = !IsLegacyFlag(HideTitle);
    }

    private void OnGridTitleChanged(string title) => _pageTitle = title;

    private static bool IsLegacyFlag(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
