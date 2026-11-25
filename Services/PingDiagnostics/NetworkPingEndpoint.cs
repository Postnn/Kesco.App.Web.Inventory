using System.Text.Json.Serialization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Microsoft.Data.SqlClient;

namespace Kesco.App.Web.Inventory.Services.PingDiagnostics;

internal sealed record LegacyPingResult(
    [property: JsonPropertyName("Result")] string Result,
    [property: JsonPropertyName("Details")] IReadOnlyDictionary<string, string> Details)
{
    internal static LegacyPingResult FromAttempts(IReadOnlyList<PingLine> lines)
    {
        var details = new Dictionary<string, string>(lines.Count);
        var errors = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Success) errors++;
            details[(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = line.Success ? "success" : line.Text;
        }

        return new(lines.Count == 0 || errors * 2 >= lines.Count ? "error" : errors > 0 ? "warning" : "success", details);
    }
}

internal static class NetworkPingEndpoint
{
    internal static void Map(WebApplication app) =>
        app.MapPost("/Ping.ashx", HandleAsync).RequireAuthorization();

    internal static async Task<IResult> HandleAsync(HttpContext context, DbManager db, PingDiagnosticService ping)
    {
        var request = context.Request;
        if (!IsSameOriginOrNonBrowser(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var host = request.Query["host"];
        if (host.Count > 1) return Results.BadRequest("Передайте один параметр host.");

        var identity = context.User.Identity;
        if (identity?.IsAuthenticated != true) return Results.Unauthorized();
        var ct = context.RequestAborted;
        try
        {
            if (await Employee.GetCurrentAsync(db, identity.Name, ct) is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException)
        {
            return Results.Problem("Не удалось подтвердить текущего сотрудника.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var target = await ping.ResolveAsync(host.ToString(), ct);
            var result = await ping.AttemptAsync(target, ct);
            return Results.Json(LegacyPingResult.FromAttempts([result]));
        }
        catch (PingDiagnosticException e) when (e.Kind == PingFailureKind.Input)
        {
            return Results.BadRequest(e.Message);
        }
        catch (PingDiagnosticException e) when (e.Kind == PingFailureKind.Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (PingDiagnosticException e)
        {
            return Results.Json(LegacyPingResult.FromAttempts([new PingLine(false, e.Message)]));
        }
    }

    private static bool IsSameOriginOrNonBrowser(HttpRequest request)
    {
        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            return false;
        var origin = request.Headers.Origin;
        return origin.Count == 0 ||
            origin.Count == 1 && string.Equals(origin[0], $"{request.Scheme}://{request.Host.ToUriComponent()}", StringComparison.OrdinalIgnoreCase);
    }
}

