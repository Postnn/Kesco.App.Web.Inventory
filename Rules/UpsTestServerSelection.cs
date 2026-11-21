using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Rules;

/// <summary>Строка выбора сервера, получившего уведомление при тестировании UPS.</summary>
public sealed record UpsTestServerSelectionItem(
    string Name,
    int? EquipmentId,
    bool IsRecognized,
    bool IsChecked);

/// <summary>Legacy-совместимые правила списка серверов UPS-теста.</summary>
public static class UpsTestServerSelection
{
    /// <summary>Разбирает сохранённый список, сохраняя исторические нераспознанные имена.</summary>
    public static List<UpsTestServerSelectionItem> ParseSelected(
        string? formattedNames,
        IEnumerable<string> availableNames)
    {
        var available = availableNames.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var recognizedDuplicates = new HashSet<string>();
        var result = new List<UpsTestServerSelectionItem>();
        if (string.IsNullOrWhiteSpace(formattedNames))
            return result;
        foreach (var name in formattedNames.Split(',').Select(item => item.Trim()))
        {
            var recognized = available.Contains(name);
            if (recognized && !recognizedDuplicates.Add(name))
                continue;
            result.Add(new UpsTestServerSelectionItem(name, null, recognized, true));
        }
        return result;
    }

    /// <summary>Возвращает имена компьютеров, доступных для выбора в UPS-тесте.</summary>
    public static IEnumerable<string> AvailableNames(IEnumerable<UpsServerLink> servers) =>
        servers.Where(server => server.EquipmentTypeId == (int)ТипыОборудования.Компьютер && !string.IsNullOrEmpty(server.ServerName))
            .Select(server => server.ServerName);

    /// <summary>Строит доступные компьютерные серверы и read-only исторические имена.</summary>
    public static IReadOnlyList<UpsTestServerSelectionItem> BuildRows(
        IEnumerable<UpsTestServerSelectionItem> selected,
        IEnumerable<UpsServerLink> servers)
    {
        var selectedItems = selected.ToList();
        var available = servers
            .Where(server => server.EquipmentTypeId == (int)ТипыОборудования.Компьютер && !string.IsNullOrEmpty(server.ServerName))
            .OrderBy(server => server.ServerName)
            .Select(server => new UpsTestServerSelectionItem(
                server.ServerName,
                server.ServerEquipmentId,
                true,
                selectedItems.Any(item => item.IsRecognized && string.Equals(item.Name, server.ServerName, StringComparison.InvariantCultureIgnoreCase))))
            .ToList();
        available.AddRange(selectedItems
            .Where(item => !item.IsRecognized)
            .OrderBy(item => item.Name)
            .Select(item => item with { IsChecked = false }));
        return available;
    }

    /// <summary>Добавляет либо удаляет распознанное имя из выбранного списка.</summary>
    public static void Toggle(
        IList<UpsTestServerSelectionItem> selected,
        UpsTestServerSelectionItem row)
    {
        if (!row.IsRecognized)
            return;
        var existing = selected.FirstOrDefault(item => string.Equals(item.Name, row.Name, StringComparison.InvariantCultureIgnoreCase));
        if (existing is not null)
        {
            selected.Remove(existing);
            return;
        }
        selected.Add(new UpsTestServerSelectionItem(row.Name, row.EquipmentId, true, true));
    }

    /// <summary>Формирует строку БД с разделителем-запятой, сохраняя нераспознанные имена.</summary>
    public static string Format(IEnumerable<UpsTestServerSelectionItem> selected) =>
        string.Join(",", selected.Select(item => item.Name));

    /// <summary>Дописывает неотмеченные серверы в Issues без повторения уже присутствующих имён.</summary>
    public static string PatchIssues(
        string? issues,
        IEnumerable<UpsTestServerSelectionItem> rows)
    {
        var current = issues ?? string.Empty;
        var uncheckedNames = rows
            .Where(row => row.EquipmentId is not null && row.IsRecognized && !row.IsChecked && !string.IsNullOrEmpty(row.Name))
            .Select(row => row.Name)
            .Where(name => !current.Contains(name))
            .ToList();
        if (uncheckedNames.Count == 0)
            return current;
        var warning = $"Сервера, не получившие уведомление: {string.Join(";", uncheckedNames)}";
        return (string.IsNullOrWhiteSpace(current) ? current : $"{current}. ") + warning;
    }
}
