using System.Net;
using System.Net.Sockets;
using Recommendarr.Infrastructure.Http;

namespace Recommendarr.Infrastructure.Tests;

public sealed class Ipv4FirstConnectorTests
{
    [Fact]
    public void Ipv4_addresses_come_first_in_dns_order()
    {
        IPAddress[] dns = [IPAddress.Parse("2600:9000::1"), IPAddress.Parse("18.0.0.1"), IPAddress.Parse("2600:9000::2"), IPAddress.Parse("18.0.0.2")];

        Assert.Equal(["18.0.0.1", "18.0.0.2", "2600:9000::1", "2600:9000::2"], Ipv4FirstConnector.Order(dns).Select(a => a.ToString()));
    }

    [Fact]
    public async Task Connects_over_ipv4_when_the_host_also_has_ipv6()
    {
        // "localhost" resolves to both ::1 and 127.0.0.1; only IPv4 is listening.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = Ipv4FirstConnector.ConnectAsync });
        var request = http.GetAsync($"http://localhost:{port}/");
        using var server = await listener.AcceptTcpClientAsync();

        Assert.Equal(AddressFamily.InterNetwork, ((IPEndPoint)server.Client.RemoteEndPoint!).AddressFamily);
        server.Client.Send("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await request).StatusCode);
    }

    [Fact]
    public async Task Falls_back_to_ipv6_when_ipv4_refuses()
    {
        if (!Socket.OSSupportsIPv6)
            return;
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = Ipv4FirstConnector.ConnectAsync });
        var request = http.GetAsync($"http://localhost:{port}/");
        using var server = await listener.AcceptTcpClientAsync();

        server.Client.Send("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await request).StatusCode);
    }
}
