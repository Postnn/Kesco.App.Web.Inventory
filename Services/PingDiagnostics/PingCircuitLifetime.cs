using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Kesco.App.Web.Inventory.Services.PingDiagnostics;

internal sealed class PingCircuitLifetime : CircuitHandler
{
    internal event Action? Disconnected;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Disconnected?.Invoke();
        return Task.CompletedTask;
    }
}

