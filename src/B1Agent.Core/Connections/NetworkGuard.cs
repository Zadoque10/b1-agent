using System.Net;
using System.Net.Sockets;

namespace B1Agent.Core.Connections;

/// <summary>
/// Stops a public deployment from being used to reach private networks (SSRF). Every outgoing connection to a
/// user-supplied Service Layer goes through <see cref="ConnectAsync"/>, which resolves the host and refuses
/// loopback, private, link-local and similar ranges. Checking at connect time (not only when the URL is entered)
/// also defeats DNS rebinding.
/// </summary>
public static class NetworkGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                     (address.GetAddressBytes()[0] & 0xFE) == 0xFC); // fc00::/7 unique local

        var b = address.GetAddressBytes();
        return !(b[0] == 10 ||                                   // 10.0.0.0/8
                 b[0] == 0 ||                                    // 0.0.0.0/8
                 (b[0] == 172 && b[1] is >= 16 and <= 31) ||     // 172.16.0.0/12
                 (b[0] == 192 && b[1] == 168) ||                 // 192.168.0.0/16
                 (b[0] == 169 && b[1] == 254) ||                 // link-local, cloud metadata
                 (b[0] == 100 && b[1] is >= 64 and <= 127) ||    // carrier-grade NAT, Tailscale
                 b[0] >= 224);                                   // multicast and reserved
    }

    /// <summary>A SocketsHttpHandler.ConnectCallback that only opens sockets to allowed addresses.</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);

        var allowed = addresses.Where(a => allowPrivate || IsPublic(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"'{endpoint.Host}' resolves to a private or reserved address, which this server does not connect to.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
