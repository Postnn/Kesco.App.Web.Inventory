namespace Kesco.App.Web.Inventory.Components.Pages.Maintenance;

/// <summary>Правила отображения сроков и адресов инструкций.</summary>
internal static class MaintenancePresentation
{
    internal static Severity DeadlineSeverity(DateTime? deadline, DateTime now) =>
        deadline is null ? Severity.Normal : deadline < now ? Severity.Error :
        deadline.Value - now < TimeSpan.FromHours(1) ? Severity.Warning : Severity.Normal;

    internal static Uri? WebInstruction(string? value)
    {
        var text = value?.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            && text!.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase)
            && !text.Any(char.IsControl)
            ? uri : null;
    }

    internal static bool IsDrivePath(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';

    internal enum Severity { Normal, Warning, Error }
}

