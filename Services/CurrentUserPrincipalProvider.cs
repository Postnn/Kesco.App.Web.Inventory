using Microsoft.AspNetCore.Components.Authorization;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Предоставляет подтверждённое имя Windows-пользователя текущего серверного вызова.</summary>
public interface ICurrentUserPrincipalProvider
{
    /// <summary>Возвращает имя только для аутентифицированной идентичности.</summary>
    Task<string?> GetWindowsPrincipalAsync();
}

/// <summary>Получает идентичность инициализированного интерактивного Blazor circuit.</summary>
public sealed class CircuitCurrentUserPrincipalProvider(AuthenticationStateProvider authenticationStateProvider) : ICurrentUserPrincipalProvider
{
    /// <inheritdoc />
    public async Task<string?> GetWindowsPrincipalAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true ? state.User.Identity.Name : null;
    }
}

/// <summary>Получает идентичность обычного HTTP-запроса без обращения к Blazor circuit.</summary>
public sealed class HttpCurrentUserPrincipalProvider(HttpContext httpContext) : ICurrentUserPrincipalProvider
{
    /// <inheritdoc />
    public Task<string?> GetWindowsPrincipalAsync() =>
        Task.FromResult(httpContext.User.Identity?.IsAuthenticated == true ? httpContext.User.Identity.Name : null);
}
