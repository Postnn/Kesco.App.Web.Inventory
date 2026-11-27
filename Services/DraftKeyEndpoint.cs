using System.Globalization;
using System.Security.Cryptography;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;

namespace Kesco.App.Web.Inventory.Services;

internal static class DraftKeyEndpoint
{
    internal const string Route = "/api/drafts/key";
    internal const string RequestHeader = "X-Clay-Draft-Key";
    internal const string EmployeeHeader = "X-Clay-Draft-Employee";

    internal static void Map(WebApplication app)
    {
        // This must run before HTTPS redirection and authentication, including rejected calls.
        app.UseWhen(context => context.Request.Path.StartsWithSegments(Route),
            branch => branch.Use(next => context => ProtectResponseAsync(context, next)));
        app.MapPost(Route, HandleAsync).RequireAuthorization();
    }

    internal static async Task ProtectResponseAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });

        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, DbManager db)
    {
        var request = context.Request;
        var identity = context.User.Identity;
        if (identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(identity.Name))
            return Results.Unauthorized();

        if (!IsSameOriginRequest(request))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.QueryString.HasValue || request.ContentLength is > 0 ||
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Results.BadRequest();

        try
        {
            var employee = await Employee.GetCurrentAsync(db, identity.Name, context.RequestAborted);
            if (employee is not { Id: > 0 })
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            context.Response.Headers[EmployeeHeader] = employee.Id.ToString(CultureInfo.InvariantCulture);
            return Results.Bytes(RandomNumberGenerator.GetBytes(32), "application/octet-stream");
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static bool IsSameOriginRequest(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        var site = request.Headers["Sec-Fetch-Site"];
        var marker = request.Headers[RequestHeader];
        return request.IsHttps && request.Host.HasValue &&
            origin.Count == 1 && string.Equals(origin[0], $"https://{request.Host.ToUriComponent()}", StringComparison.OrdinalIgnoreCase) &&
            (site.Count == 0 || site.Count == 1 && string.Equals(site[0], "same-origin", StringComparison.Ordinal)) &&
            marker.Count == 1 && marker[0] == "1";
    }
}
