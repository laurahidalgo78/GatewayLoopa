using System.Net;
using System.Net.Sockets;

namespace ApiGatewayPay.Infrastructure.Http;

/// <summary>
/// Defensa SSRF a nivel de socket: valida las IP resueltas justo antes de conectar, de modo que un
/// DNS rebinding no puede llevar la conexión a la red interna ni al endpoint de metadatos cloud.
/// </summary>
public static class SsrfGuard
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),   // CGNAT; incluye metadatos de Alibaba (100.100.100.200)
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),  // link-local; metadatos AWS/Azure/GCP (169.254.169.254)
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("64:ff9b::/96"),    // NAT64: puede mapear a IPv4 internas
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),       // 6to4
        IPNetwork.Parse("fc00::/7"),        // ULA; incluye metadatos AWS IPv6 (fd00:ec2::254)
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return BlockedNetworks.Any(n => n.BaseAddress.AddressFamily == address.AddressFamily && n.Contains(address));
    }

    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(bool allowPrivateNetworks) =>
        async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) throw new OutboundRequestBlockedException("sin resolución DNS");
            // Si CUALQUIER IP resuelta es interna se rechaza todo (respuestas DNS mixtas son una técnica de rebinding).
            if (!allowPrivateNetworks && addresses.Any(IsBlocked))
                throw new OutboundRequestBlockedException("destino en red interna o reservada");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
}
