using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Navigation;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Прикладной составитель структуры и условий главного меню Inventory.</summary>
public static class InventoryMainMenuComposer
{
    /// <summary>Формирует три DB-раздела, контекстный отчёт и доступные настройки.</summary>
    public static IReadOnlyList<ClayNavigationMenuItem> Build(
        InventoryMenuLoadResult data,
        int? currentQueryId,
        IConfiguration configuration,
        string currentUri,
        bool canOpenSettings)
    {
        var items = new List<ClayNavigationMenuItem>
        {
            BuildRoot("inventory", "Инвентаризация", 1, data.Items, currentQueryId, configuration, currentUri),
            BuildRoot("inconsistencies", "Несоответствия", 2, data.Items, currentQueryId, configuration, currentUri),
            BuildRoot("reports", "Отчёты", 3, data.Items, currentQueryId, configuration, currentUri),
        };

        if (data.CurrentQuery is not null && !string.IsNullOrWhiteSpace(data.CurrentQuery.ReportMenuItem))
        {
            var link = InventoryMenuUrlBuilder.BuildReportLink(data.CurrentQuery, configuration, currentUri);
            items.Add(new ClayNavigationMenuItem
            {
                Id = "context-report",
                Text = data.CurrentQuery.ReportMenuItem,
                Href = link.Href,
                Disabled = link.Error is not null,
                Title = link.Error ?? data.CurrentQuery.ReportMenuItem,
            });
        }

        if (canOpenSettings)
        {
            var link = InventoryMenuUrlBuilder.BuildSettingsLink(configuration, currentQueryId, currentUri);
            items.Add(new ClayNavigationMenuItem
            {
                Id = "settings",
                Text = "Настройки инвентаризации",
                Href = link.Href,
                Disabled = link.Error is not null,
                Title = link.Error ?? "Настройки инвентаризации",
                IconUrl = InventoryMenuUrlBuilder.ResolveIcon("/styles/tools.gif", configuration["URI_styles"]),
            });
        }

        return items;
    }

    private static ClayNavigationMenuItem BuildRoot(
        string id, string caption, int queryType, IReadOnlyList<InventoryMenuQuery> queries,
        int? currentQueryId, IConfiguration configuration, string currentUri)
    {
        var children = queries.Where(query => query.QueryType == queryType).Select(query =>
        {
            var link = InventoryMenuUrlBuilder.BuildQueryLink(query, configuration, currentUri);
            return new ClayNavigationMenuItem
            {
                Id = query.Id.ToString(),
                Text = query.Caption,
                Href = link.Href,
                IconUrl = link.IconUrl,
                Disabled = link.Error is not null,
                Title = link.Error ?? query.Caption,
                IsActive = query.Id == currentQueryId,
            };
        }).ToList();
        return new ClayNavigationMenuItem
        {
            Id = id,
            Text = caption,
            Children = children,
            Disabled = children.Count == 0,
        };
    }
}
