using System.Net;
using System.Net.NetworkInformation;

namespace Kesco.App.Web.Inventory.Services.PingDiagnostics;

internal sealed record NetworkPingReply(IPStatus Status, IPAddress? Address, int Bytes, long Roundtrip, int? Ttl);

internal interface INetworkPingTransport
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
    Task<NetworkPingReply?> SendAsync(IPAddress address, CancellationToken ct);
}

internal sealed class NetworkPingTransport : INetworkPingTransport
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);

    public async Task<NetworkPingReply?> SendAsync(IPAddress address, CancellationToken ct)
    {
        using var ping = new Ping();
        // Don't Fragment is an IPv4 option. Some IPv6 replies do not contain TTL.
        var options = new PingOptions(128, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), new byte[PingDiagnosticService.PayloadSize], options, ct);
        return new NetworkPingReply(reply.Status, reply.Address, reply.Buffer?.Length ?? 0, reply.RoundtripTime, reply.Options?.Ttl);
    }
}

