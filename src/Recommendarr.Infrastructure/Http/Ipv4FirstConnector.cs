using System.Net;
using System.Net.Sockets;

namespace Recommendarr.Infrastructure.Http;

/// <summary>
/// Connects to IPv4 addresses before IPv6 ones. Many home networks have IPv6 routes that accept the
/// TCP connection and then drop it mid-TLS-handshake ("The SSL connection could not be
/// established"). .NET tries addresses in DNS order and can't fall back once TCP has connected, so
/// preferring IPv4 avoids the broken path entirely. IPv6 is still used when it's the only option.
/// </summary>
internal static class Ipv4FirstConnector
{
    public static IEnumerable<IPAddress> Order(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1); // stable: keeps DNS order within a family

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);

        SocketException? lastError = null;
        foreach (var address in Order(addresses))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }
}
