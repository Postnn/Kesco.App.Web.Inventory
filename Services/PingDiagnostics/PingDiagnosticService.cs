using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Kesco.App.Web.Inventory.Services.PingDiagnostics;

internal sealed class PingDiagnosticOptions
{
    public string[] AllowedHosts { get; set; } = [];
    public string[] AllowedAddresses { get; set; } = [];
}

internal enum PingFailureKind { Input, Forbidden, Resolution, Transport }

internal sealed class PingDiagnosticException(string message, PingFailureKind kind) : InvalidOperationException(message)
{
    internal PingFailureKind Kind { get; } = kind;
}

internal sealed record PingTarget(string Host, IPAddress Address, string? DnsName = null)
{
    internal string Header => $"Обмен пакетами с {Host} [{Address}] с {PingDiagnosticService.PayloadSize} байтами данных:";
}

internal sealed record PingLine(bool Success, string Text);

internal sealed class PingDiagnosticService(INetworkPingTransport transport, IOptionsMonitor<PingDiagnosticOptions> options, TimeProvider clock)
{
    internal const int PayloadSize = 32;
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    internal async Task<PingTarget> ResolveAsync(string? input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new PingDiagnosticException("Не указано сетевое имя устройства. Выполнить команду ping невозможно", PingFailureKind.Input);

        var host = input.Trim();
        if (host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) ||
            host.IndexOfAny(['/', '\\', '%', '?', '#', '@', '[', ']']) >= 0)
            throw InvalidHost();

        ct.ThrowIfCancellationRequested();
        if (IPAddress.TryParse(host, out var literal))
        {
            CheckAllowedAddress(literal);
            return new PingTarget(host, literal);
        }

        if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw InvalidHost();
        string dnsName;
        try { dnsName = new IdnMapping().GetAscii(host.TrimEnd('.')); }
        catch (ArgumentException) { throw InvalidHost(); }
        if (!options.CurrentValue.AllowedHosts.Contains(dnsName, StringComparer.OrdinalIgnoreCase))
            throw Forbidden();

        using var deadline = new CancellationTokenSource(ResolutionTimeout, clock);
        using var dnsCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        IPAddress[] addresses;
        try
        {
            addresses = await transport.ResolveAsync(host.EndsWith('.') ? dnsName + "." : dnsName, dnsCancellation.Token);
            if (dnsCancellation.IsCancellationRequested && !ct.IsCancellationRequested) throw DnsFailure(host);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw DnsFailure(host); }
        catch (SocketException) { ct.ThrowIfCancellationRequested(); throw DnsFailure(host); }
        ct.ThrowIfCancellationRequested();

        if (addresses.Length == 0) throw DnsFailure(host);
        CheckAllowedAddress(addresses[0]);
        return new PingTarget(host, addresses[0], dnsName);
    }

    internal async Task<PingLine> AttemptAsync(PingTarget target, CancellationToken ct)
    {
        await Task.Delay(Interval, clock, ct);
        ct.ThrowIfCancellationRequested();
        CheckAllowedAddress(target.Address);
        if (target.DnsName is { } name && !options.CurrentValue.AllowedHosts.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw Forbidden();
        NetworkPingReply? reply;
        try { reply = await transport.SendAsync(target.Address, ct); }
        catch (PlatformNotSupportedException)
        {
            throw new PingDiagnosticException("Транспорт ICMP недоступен в этой среде.", PingFailureKind.Transport);
        }
        catch (Exception e) when (e is PingException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            return new PingLine(false, "Ошибка выполнения: транспорт ICMP не подтвердил отправку или получение ответа.");
        }
        ct.ThrowIfCancellationRequested();

        if (reply is null) return new PingLine(false, "Ошибка по неизвестной причине.");
        if (reply.Status != IPStatus.Success) return new PingLine(false, StatusText(reply.Status));

        var endpoint = reply.Address ?? target.Address;
        var elapsed = reply.Roundtrip == 0 ? "<1" : "=" + reply.Roundtrip.ToString(CultureInfo.InvariantCulture);
        var ttl = reply.Ttl is { } limit ? $" TTL={limit}" : "";
        return new PingLine(true, $"Ответ от {endpoint}: число байт={reply.Bytes} время{elapsed}мс{ttl}");
    }

    private void CheckAllowedAddress(IPAddress address)
    {
        if (!options.CurrentValue.AllowedAddresses.Any(value => IPAddress.TryParse(value, out var allowed) && address.Equals(allowed)))
            throw Forbidden();
    }

    private static PingDiagnosticException InvalidHost() =>
        new("Укажите допустимое сетевое имя или IP-адрес устройства.", PingFailureKind.Input);

    private static PingDiagnosticException Forbidden() =>
        new("Адрес не входит в настроенную область диагностики Inventory.", PingFailureKind.Forbidden);

    private static PingDiagnosticException DnsFailure(string host) =>
        new($"Не удалось определить IP адрес по hostname: {host}", PingFailureKind.Resolution);

    private static string StatusText(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "Превышен интервал ожидания для запроса.",
        IPStatus.DestinationHostUnreachable => "Заданный узел недоступен.",
        IPStatus.DestinationNetworkUnreachable => "Заданная сеть недоступна.",
        IPStatus.DestinationUnreachable => "Пункт назначения недоступен.",
        IPStatus.PacketTooBig => "Пакет слишком большой.",
        IPStatus.TtlExpired or IPStatus.TimeExceeded => "Истекло время жизни пакета.",
        IPStatus.TtlReassemblyTimeExceeded => "Истекло время сборки пакета.",
        IPStatus.BadRoute => "Ошибка маршрута.",
        _ => $"Ошибка ICMP ({status}).",
    };
}

