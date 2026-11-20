using System.Web;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Результат безопасного построения ссылки пункта меню.</summary>
public sealed class InventoryMenuLink
{
    /// <summary>URL перехода либо null при недоступности.</summary>
    public string? Href { get; init; }

    /// <summary>Причина недоступности без секретных значений конфигурации.</summary>
    public string? Error { get; init; }

    /// <summary>Разрешённый URL пиктограммы.</summary>
    public string? IconUrl { get; init; }
}

/// <summary>Формирует URL меню Inventory по подтверждённым legacy-правилам.</summary>
public static class InventoryMenuUrlBuilder
{
    /// <summary>Строит ссылку обычного запроса: внутренний ?id= либо внешний idQuery=.</summary>
    public static InventoryMenuLink BuildQueryLink(
        InventoryMenuQuery query, IConfiguration configuration, string currentUri)
    {
        var icon = ResolveIcon(query.Icon, configuration["URI_styles"]);
        if (string.IsNullOrWhiteSpace(query.ListForm))
            return new InventoryMenuLink { Href = $"/?id={query.Id}", IconUrl = icon };

        var resolved = ResolveForm(query.ListForm, configuration, currentUri, out var error);
        if (resolved is null)
            return new InventoryMenuLink { Error = error, IconUrl = icon };

        return new InventoryMenuLink
        {
            Href = AddQueryParameter(resolved, "idQuery", query.Id.ToString()),
            IconUrl = icon,
        };
    }

    /// <summary>Строит ссылку контекстного отчёта по ключу конфигурации без idQuery.</summary>
    public static InventoryMenuLink BuildReportLink(InventoryMenuQuery query, IConfiguration configuration, string currentUri)
    {
        var key = query.ReportForm.Trim().TrimStart('@');
        if (key.Length == 0)
            return new InventoryMenuLink { Error = "В настройках запроса не указана форма отчёта." };
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return new InventoryMenuLink { Error = $"Не найден параметр конфигурации «{key}»." };

        var resolved = ResolveAbsoluteOrRelative(value, currentUri, out var error);
        return new InventoryMenuLink { Href = resolved, Error = error };
    }

    /// <summary>Добавляет к настройкам Inventory выбранный _idQuery с суффиксом E.</summary>
    public static InventoryMenuLink BuildSettingsLink(IConfiguration configuration, int? currentQueryId, string currentUri)
    {
        const string key = "URI_inventory_queries";
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return new InventoryMenuLink { Error = $"Не найден параметр конфигурации «{key}»." };
        var resolved = ResolveAbsoluteOrRelative(value, currentUri, out var error);
        if (resolved is null)
            return new InventoryMenuLink { Error = error };
        if (currentQueryId is > 0)
            resolved = AddQueryParameter(resolved, "_idQuery", $"{currentQueryId.Value}E");
        return new InventoryMenuLink { Href = resolved };
    }

    /// <summary>Разрешает пиктограмму из БД относительно подтверждённого URI_styles.</summary>
    public static string? ResolveIcon(string icon, string? stylesUri)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var value = icon.Trim();
        if (IsHttp(value)) return value;
        if (value.Contains(':') || string.IsNullOrWhiteSpace(stylesUri) || !IsHttp(stylesUri)) return null;
        var baseUri = new Uri(stylesUri.TrimEnd('/') + "/", UriKind.Absolute);
        return value.StartsWith("/styles/", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseUri, value).AbsoluteUri
            : new Uri(baseUri, value.TrimStart('/')).AbsoluteUri;
    }

    private static string? ResolveForm(
        string form, IConfiguration configuration, string currentUri, out string? error)
    {
        var value = form.Trim();
        if (value.StartsWith('@'))
        {
            var key = value[1..];
            value = configuration[key] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"Не найден параметр конфигурации «{key}».";
                return null;
            }
        }
        return ResolveAbsoluteOrRelative(value, currentUri, out error);
    }

    private static string? ResolveAbsoluteOrRelative(string value, string currentUri, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            error = "Адрес формы не задан или содержит недопустимые символы.";
            return null;
        }
        if (IsHttp(value)) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            error = "Разрешены только HTTP(S) и относительные адреса.";
            return null;
        }
        if (!Uri.TryCreate(currentUri, UriKind.Absolute, out var baseUri))
        {
            error = "Не удалось определить базовый адрес приложения.";
            return null;
        }
        return new Uri(baseUri, value).AbsoluteUri;
    }

    private static string AddQueryParameter(string url, string name, string value)
    {
        var builder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query.Remove(name);
        query[name] = value;
        builder.Query = query.ToString();
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
