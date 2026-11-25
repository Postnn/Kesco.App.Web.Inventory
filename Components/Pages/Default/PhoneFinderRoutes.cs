using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Kesco.App.Web.Inventory.Components.Pages.Default;

/// <summary>Строгое чтение контекста и формирование адресов окон выбора.</summary>
public static class PhoneFinderRoutes
{
    /// <summary>Отсутствующее значение и ровно «0» означают SQL NULL.</summary>
    public static bool TryId(string? input, out int? value)
    {
        value = null;
        if (input is null or "0") return true;
        if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
        value = id;
        return true;
    }

    /// <summary>Читает ограниченный URL-список исключений из современного вызова.</summary>
    public static bool TryExclusions(string? input, out int[] ids)
    {
        ids = [];
        if (input is null) return true;
        if (input.Length > 11_000) return false;
        var values = input.Split(',');
        if (values.Length > 1000) return false;
        var parsed = new HashSet<int>();
        foreach (var text in values)
        {
            if (!TryId(text, out var id) || id is not > 0) return false;
            parsed.Add(id.Value);
        }
        ids = parsed.Order().ToArray();
        return true;
    }

    /// <summary>Сохраняет реальный нулевой код справочника отдельно от legacy-сентинела.</summary>
    public static string? Id(int? id) => id is 0 ? "00" : id?.ToString(CultureInfo.InvariantCulture);

    /// <summary>Формирует один URL окна с безопасно закодированными полями контекста.</summary>
    public static string Build(string configuredUrl, IEnumerable<KeyValuePair<string, string?>> context, IReadOnlyCollection<int> excluded, string search)
    {
        var values = context.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (excluded.Count != 0) values["exclude"] = string.Join(',', excluded);
        if (!string.IsNullOrWhiteSpace(search)) values["q"] = search;
        return QueryHelpers.AddQueryString(configuredUrl, values);
    }
}
