using System.Globalization;
using Clayzor.Lib.Entities.TerritorialEntity;
using Microsoft.AspNetCore.WebUtilities;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Канонические маршруты и строгий разбор контекста формы.</summary>
public static class TerritorialEntityRoutes
{
    /// <summary>Карточка территории.</summary>
    public const string Territory = "TerritorialEntity/TerritorialEntity_form";
    /// <summary>Карточка телефонного кода.</summary>
    public const string Phone = "TerritorialEntity/PhoneCodeEntity_form";
    /// <summary>Дерево территорий.</summary>
    public const string Search = "TerritorialEntity/TerritorialEntity_search";

    /// <summary>Проверенный клиентский контекст вложенного списка телефонных кодов.</summary>
    public const int PhoneListContext = 85;

    /// <summary>Парсит ключ территории. Открытие без ID намеренно отделено от создания с ID 0.</summary>
    public static bool TryTerritoryId(string? id, out int value) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;

    /// <summary>Проверяет положительный идентификатор записи без локализованных разделителей.</summary>
    public static bool TryPositiveId(string? id, out int value) => TryTerritoryId(id, out value) && value > 0;

    /// <summary>Разрешает callback только с действительным requestId и точным origin приложения.</summary>
    public static bool IsLocalReturn(string? requestId, string? returnOrigin, string baseUrl) =>
        Guid.TryParseExact(requestId, "N", out _) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) &&
        string.Equals(returnOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>Читает rootids, root, parent в порядке приоритета, отвергая повреждённый scope.</summary>
    public static bool TryRoots(string? rootids, string? root, string? parent, out IReadOnlyList<int>? roots)
    {
        roots = null;
        var source = rootids ?? root ?? parent;
        if (source is null) return true;
        if (source.Length > 11000) return false;
        var values = source.Split(',');
        if (values.Length > 1000 || rootids is null && values.Length != 1) return false;
        var result = new HashSet<int>();
        foreach (var raw in values)
        {
            if (!TryPositiveId(raw.Trim(), out var id)) return false;
            result.Add(id);
        }
        roots = result.Order().ToArray();
        return true;
    }

    /// <summary>Читает дедуплицированный отсортированный набор положительных ID территорий.</summary>
    public static bool TrySelectedIds(string? source, out IReadOnlyList<int> ids)
    {
        ids = [];
        if (string.IsNullOrEmpty(source)) return true;
        if (!TryRoots(source, null, null, out var parsed)) return false;
        ids = parsed!;
        return true;
    }

    /// <summary>Формирует URL редактирования с закодированным строковым ключом.</summary>
    public static string PhoneEdit(string code) => QueryHelpers.AddQueryString(Phone, "id", code);

    /// <summary>Формирует URL создания, не превращая код 0 в признак новой записи.</summary>
    public static string PhoneCreate(int territoryId) => QueryHelpers.AddQueryString(Phone, "areaid", Number(territoryId));

    /// <summary>Формирует URL карточки узла без внешнего callback URL.</summary>
    public static string TerritoryEdit(int id, bool addChild = false) => QueryHelpers.AddQueryString(Territory,
        new Dictionary<string, string?>
        {
            ["id"] = Number(addChild ? 0 : id), ["parent"] = addChild ? Number(id) : null, ["fromTree"] = "true",
        });

    /// <summary>URL проверенного metadata-грида в отдельной области с принудительным фильтром.</summary>
    public static string Grid(Запросы query, int territoryId, int? clientId = null) => QueryHelpers.AddQueryString("./",
        new Dictionary<string, string?>
        {
            ["id"] = Number((int)query), ["CLID"] = clientId is int id ? Number(id) : null,
            [query == Запросы.Территории ? "Parent" : "areaid"] = $"eq~{Number(territoryId)}",
            ["hidemenu"] = "1", ["hidetitle"] = "1",
        });

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
