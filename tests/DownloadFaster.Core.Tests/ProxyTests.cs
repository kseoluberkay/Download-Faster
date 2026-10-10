using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DownloadFaster.Core;
using DownloadFaster.Core.Proxy;

namespace DownloadFaster.Core.Tests;

/// <summary>
/// Tests for the all-apps proxy. As in the download tests, 127.0.0.1 and 127.0.0.2 stand in
/// for Ethernet and Wi-Fi, and the server tells them apart by client address.
/// </summary>
public sealed class ProxyTests : IAsyncLifetime
{
    private const string Ethernet = "127.0.0.1";
    private const string WiFi = "127.0.0.2";
    private const long MB = DownloadOptions.MiB;

    private readonly DownloadOptions _options = new();
    private readonly List<NetworkRoute> _routes = [Route(Ethernet), Route(WiFi)];
    private RangeServer _server = null!;
    private LocalProxyServer _proxy = null!;
    private ConnectionBalancer _balancer = null!;

    public async Task InitializeAsync()
    {
        var content = new byte[4 * MB];
        new Random(7).NextBytes(content);
        _server = await RangeServer.StartAsync(content);
        _balancer = new ConnectionBalancer(() => _routes, _options) { Policy = SpreadPolicy.PerConnection };
        _proxy = new LocalProxyServer(_balancer, 0) { LoopbackGoesDirect = false };
        _proxy.Start();
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _server.DisposeAsync();
    }

    private int ServerPort => _server.Url.Port;

    [Fact]
    public async Task Https_style_tunnels_are_spread_over_both_adapters()
    {
        for (int i = 0; i < 10; i++)
        {
            byte[] body = await TunnelGetAsync("bytes=0-1023");
            Assert.Equal(_server.Content.AsSpan(0, 1024).ToArray(), body);
        }

        // Usually 5 and 5; a tunnel that has not quite closed yet can tip one choice the other way.
        Assert.InRange(_server.RequestsByClient.GetValueOrDefault(Ethernet), 3, 7);
        Assert.Equal(10, _server.RequestsByClient.GetValueOrDefault(Ethernet) + _server.RequestsByClient.GetValueOrDefault(WiFi));
    }

    [Fact]
    public async Task Plain_http_works_with_keep_alive_bodies_and_chunking()
    {
        using var client = ProxiedClient();
        string baseUrl = $"http://127.0.0.1:{ServerPort}";

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(_server.Content, await client.GetByteArrayAsync($"{baseUrl}/files/test.bin"));
        }

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/files/test.bin"));
        Assert.Equal(_server.Content.Length, head.Content.Headers.ContentLength);

        Assert.Equal(_server.Content.AsSpan(0, 300 * 1024).ToArray(), await client.GetByteArrayAsync($"{baseUrl}/chunked"));

        var payload = Encoding.UTF8.GetBytes(new string('x', 100_000));
        using var echo = await client.PostAsync($"{baseUrl}/echo", new ByteArrayContent(payload));
        Assert.Equal(payload, await echo.Content.ReadAsByteArrayAsync());

        // After all that, the next request on the same kept-alive connection still works.
        Assert.Equal(_server.Content, await client.GetByteArrayAsync($"{baseUrl}/files/test.bin"));

        // And it all really went through the proxy and its adapters.
        Assert.True(_balancer.GetTraffic().Sum(t => t.TotalBytes) >= 4 * _server.Content.Length);
    }

    [Fact]
    public async Task Many_parallel_downloads_like_Steam_use_both_adapters_and_stay_intact()
    {
        _server.ThrottleByClient[Ethernet] = 8 * MB;
        _server.ThrottleByClient[WiFi] = 4 * MB;
        using var client = ProxiedClient();
        string url = $"http://127.0.0.1:{ServerPort}/files/test.bin";

        // 16 chunk requests, 8 at a time, the way a game launcher fetches a depot.
        const int Chunk = 256 * 1024;
        using var gate = new SemaphoreSlim(8);
        var chunks = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(i * Chunk, ((i + 1) * Chunk) - 1);
                using var response = await client.SendAsync(request);
                return (Index: i, Bytes: await response.Content.ReadAsByteArrayAsync());
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (index, bytes) in chunks)
        {
            Assert.Equal(_server.Content.AsSpan(index * Chunk, Chunk).ToArray(), bytes);
        }

        Assert.True(_server.BytesFrom(Ethernet) > 1 * MB, $"Ethernet sent {_server.BytesFrom(Ethernet)}");
        Assert.True(_server.BytesFrom(WiFi) > 1 * MB, $"Wi-Fi sent {_server.BytesFrom(WiFi)}");
    }

    [Fact]
    public async Task One_app_connection_can_talk_to_two_servers_in_turn()
    {
        await using var second = await RangeServer.StartAsync(Encoding.ASCII.GetBytes("second server"));
        using var socket = await ConnectToProxyAsync();
        using var stream = new NetworkStream(socket);

        foreach (var (port, expected) in new[] { (ServerPort, _server.Content), (second.Url.Port, second.Content), (ServerPort, _server.Content) })
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{port}/files/test.bin HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"));
            var (status, body) = await ReadResponseAsync(stream);
            Assert.Equal(200, status);
            Assert.Equal(expected, body);
        }
    }

    [Fact]
    public async Task Socks5_and_socks4a_clients_are_served()
    {
        using (var socket = await ConnectToProxyAsync())
        using (var stream = new NetworkStream(socket))
        {
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            Assert.Equal(new byte[] { 5, 0 }, await ReadExactAsync(stream, 2));
            byte[] host = Encoding.ASCII.GetBytes("127.0.0.1");
            byte[] request = [5, 1, 0, 3, (byte)host.Length, .. host, (byte)(ServerPort >> 8), (byte)ServerPort];
            await stream.WriteAsync(request);
            byte[] reply = await ReadExactAsync(stream, 10);
            Assert.Equal(0, reply[1]);
            await AssertHttpGetThroughAsync(stream);
        }

        using (var socket = await ConnectToProxyAsync())
        using (var stream = new NetworkStream(socket))
        {
            byte[] request = [4, 1, (byte)(ServerPort >> 8), (byte)ServerPort, 0, 0, 0, 1, 0, .. Encoding.ASCII.GetBytes("127.0.0.1"), 0];
            await stream.WriteAsync(request);
            byte[] reply = await ReadExactAsync(stream, 8);
            Assert.Equal(0x5A, reply[1]);
            await AssertHttpGetThroughAsync(stream);
        }
    }

    [Fact]
    public async Task Fixed_split_hands_out_connections_by_share()
    {
        _options.Mode = BalanceMode.FixedSplit;
        _routes[0].Settings.Share = 75;
        _routes[1].Settings.Share = 25;

        var tunnels = new List<Socket>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                tunnels.Add(await OpenTunnelAsync());
            }

            var traffic = _balancer.GetTraffic().ToDictionary(t => t.RouteId, t => t.ActiveConnections);
            Assert.Equal(6, traffic[_routes[0].Id]);
            Assert.Equal(2, traffic[_routes[1].Id]);
        }
        finally
        {
            tunnels.ForEach(t => t.Dispose());
        }
    }

    [Fact]
    public async Task An_adapter_that_cannot_connect_is_skipped()
    {
        // No machine owns 198.51.100.77, so binding to it always fails.
        _routes[1] = new NetworkRoute("broken", "Broken", AdapterKind.WiFi, [IPAddress.Parse("198.51.100.77")]);

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(1024, (await TunnelGetAsync("bytes=0-1023")).Length);
        }

        Assert.Equal(6, _server.RequestsByClient.GetValueOrDefault(Ethernet));
    }

    [Fact]
    public async Task Turned_off_adapters_are_not_used()
    {
        _routes[1].Settings.Enabled = false;

        for (int i = 0; i < 4; i++)
        {
            await TunnelGetAsync("bytes=0-1023");
        }

        Assert.Equal(4, _server.RequestsByClient.GetValueOrDefault(Ethernet));
        Assert.Equal(0, _server.RequestsByClient.GetValueOrDefault(WiFi));
    }

    [Fact]
    public async Task With_no_adapter_ticked_apps_still_get_through()
    {
        _routes.ForEach(r => r.Settings.Enabled = false);

        Assert.Equal(1024, (await TunnelGetAsync("bytes=0-1023")).Length);
    }

    [Fact]
    public async Task Speed_limit_applies_to_app_traffic()
    {
        _routes[0].Settings.SpeedLimitBytesPerSecond = 1 * MB;
        _routes[1].Settings.Enabled = false;
        using var client = ProxiedClient();

        var clock = Stopwatch.StartNew();
        await client.GetByteArrayAsync($"http://127.0.0.1:{ServerPort}/files/test.bin");

        // 4 MB at 1 MB/s, minus the small burst allowance.
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(3), $"Took only {clock.Elapsed}");
    }

    [Fact]
    public async Task Requests_for_the_proxy_itself_do_not_loop()
    {
        using var client = ProxiedClient();
        string text = await client.GetStringAsync($"http://127.0.0.1:{_proxy.Port}/");
        Assert.Contains("Download Faster", text);

        using var socket = await ConnectToProxyAsync();
        using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{_proxy.Port} HTTP/1.1\r\n\r\n"));
        var (status, _) = await ReadResponseAsync(stream);
        Assert.Equal(502, status);
    }

    [Fact]
    public void Downloads_only_keeps_everything_but_downloads_on_one_adapter()
    {
        _balancer.Policy = SpreadPolicy.DownloadsOnly;

        var firstChoices = Enumerable.Range(0, 6).Select(i => _balancer.Choose($"site{i}.example", 443)[0].Id).Distinct().ToList();
        Assert.Single(firstChoices);

        Assert.Null(_balancer.StickyKey("cache4-fra1.steamcontent.com", 443));
        Assert.Null(_balancer.StickyKey("anything.example", 80));
        Assert.Null(_balancer.StickyKey("cdn.example", 8080, plainHttp: true));
        Assert.Equal("*", _balancer.StickyKey("mail.example", 443));
    }

    [Fact]
    public void Per_site_keeps_each_site_on_its_adapter_but_spreads_sites()
    {
        _balancer.Policy = SpreadPolicy.PerSite;

        var bySite = Enumerable.Range(0, 6).Select(i => _balancer.Choose($"site{i}.example", 443)[0].Id).ToList();
        Assert.Equal(2, bySite.Distinct().Count());
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(bySite[i], _balancer.Choose($"site{i}.example", 443)[0].Id);
        }
    }

    [Fact]
    public void Windows_proxy_settings_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var original = WindowsSystemProxy.Query();
        try
        {
            WindowsSystemProxy.Apply(SystemProxySettings.ForLocalProxy(48999));
            var applied = WindowsSystemProxy.Query();
            Assert.True(applied.UsesProxyServer);
            Assert.Equal("127.0.0.1:48999", applied.ProxyServer);
            Assert.Equal(SystemProxySettings.LocalBypass, applied.Bypass);
        }
        finally
        {
            WindowsSystemProxy.Apply(original);
        }

        Assert.Equal(original, WindowsSystemProxy.Query());
    }

    // ---------------------------------------------------------------- helpers

    private static NetworkRoute Route(string address) =>
        new($"route-{address}", $"Adapter {address}", AdapterKind.Ethernet, [IPAddress.Parse(address)]);

    private HttpClient ProxiedClient() => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{_proxy.Port}") { BypassProxyOnLocal = false },
        UseProxy = true,
    });

    private async Task<Socket> ConnectToProxyAsync()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        return socket;
    }

    /// <summary>Opens a CONNECT tunnel to the test server and leaves it open.</summary>
    private async Task<Socket> OpenTunnelAsync()
    {
        var socket = await ConnectToProxyAsync();
        var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{ServerPort} HTTP/1.1\r\nHost: 127.0.0.1:{ServerPort}\r\n\r\n"));
        var (status, _) = await ReadHeadAsync(stream);
        Assert.Equal(200, status);
        return socket;
    }

    /// <summary>Fetches part of the test file through a fresh CONNECT tunnel, like a browser on HTTPS.</summary>
    private async Task<byte[]> TunnelGetAsync(string range)
    {
        using var socket = await OpenTunnelAsync();
        using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /files/test.bin HTTP/1.1\r\nHost: 127.0.0.1:{ServerPort}\r\nRange: {range}\r\nConnection: close\r\n\r\n"));
        var (status, body) = await ReadResponseAsync(stream);
        Assert.Equal(206, status);
        return body;
    }

    private async Task AssertHttpGetThroughAsync(NetworkStream stream)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /files/test.bin HTTP/1.1\r\nHost: 127.0.0.1:{ServerPort}\r\nRange: bytes=0-99\r\n\r\n"));
        var (status, body) = await ReadResponseAsync(stream);
        Assert.Equal(206, status);
        Assert.Equal(_server.Content.AsSpan(0, 100).ToArray(), body);
    }

    private static async Task<(int Status, Dictionary<string, string> Headers)> ReadHeadAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            head.Add((await ReadExactAsync(stream, 1))[0]);
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return (int.Parse(lines[0].Split(' ')[1]), headers);
    }

    private static async Task<(int Status, byte[] Body)> ReadResponseAsync(NetworkStream stream)
    {
        var (status, headers) = await ReadHeadAsync(stream);
        int length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
        return (status, await ReadExactAsync(stream, length));
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
