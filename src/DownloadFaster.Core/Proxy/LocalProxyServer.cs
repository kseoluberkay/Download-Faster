using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DownloadFaster.Core.Proxy;

/// <summary>
/// A local proxy that other apps (browsers, Steam, launchers) use through the Windows proxy
/// setting. Each connection they open is sent out through an adapter picked by the
/// <see cref="ConnectionBalancer"/>, so apps that open several connections use every adapter.
/// One port speaks HTTP (including CONNECT tunnels for HTTPS), SOCKS5 and SOCKS4a.
/// </summary>
public sealed class LocalProxyServer : IAsyncDisposable
{
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleKeepAlive = TimeSpan.FromSeconds(120);

    private readonly ConnectionBalancer _balancer;
    private readonly int _preferredPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private Task _sampler = Task.CompletedTask;

    /// <param name="preferredPort">Port to listen on; the next free one is used if it is taken. 0 picks any.</param>
    public LocalProxyServer(ConnectionBalancer balancer, int preferredPort)
    {
        _balancer = balancer;
        _preferredPort = preferredPort;
    }

    public int Port { get; private set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Connect to services on this PC without picking an adapter. Tests turn it off.</summary>
    internal bool LoopbackGoesDirect { get; set; } = true;

    /// <summary>Connections from apps that are currently open.</summary>
    public int ClientConnections => _clients.Count;

    public ConnectionBalancer Balancer => _balancer;

    public void Start()
    {
        var ports = _preferredPort == 0 ? [0] : Enumerable.Range(_preferredPort, 20);
        SocketException? lastError = null;
        foreach (int port in ports)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start(backlog: 512);
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }
        }

        if (_listener is null)
        {
            throw lastError ?? new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        _acceptLoop = AcceptLoopAsync();
        _sampler = SampleLoopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var socket in _clients.Keys)
        {
            socket.Dispose();
        }

        await _acceptLoop.ConfigureAwait(false);
        await _sampler.ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            socket.NoDelay = true;
            _ = Task.Run(() => HandleClientAsync(socket));
        }
    }

    private async Task SampleLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            _balancer.SampleSpeeds();
            try
            {
                await Task.Delay(250, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(Socket socket)
    {
        _clients.TryAdd(socket, 0);
        var ct = _cts.Token;
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            var client = new ProxyReader(stream);
            int first;
            using (var timeout = Timeout(ct, HeadTimeout))
            {
                first = await client.PeekByteAsync(timeout.Token).ConfigureAwait(false);
            }

            switch (first)
            {
                case -1:
                    return;
                case 5:
                    await HandleSocks5Async(client, ct).ConfigureAwait(false);
                    break;
                case 4:
                    await HandleSocks4Async(client, ct).ConfigureAwait(false);
                    break;
                default:
                    await HandleHttpAsync(client, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
            or InvalidDataException or ObjectDisposedException)
        {
            // The app or the server hung up; nothing else to do.
        }
        finally
        {
            _clients.TryRemove(socket, out _);
        }
    }

    // ---------------------------------------------------------------- HTTP

    private async Task HandleHttpAsync(ProxyReader client, CancellationToken ct)
    {
        Upstream? upstream = null;
        string? upstreamKey = null;
        try
        {
            bool firstRequest = true;
            while (true)
            {
                HttpHead? request;
                using (var timeout = Timeout(ct, firstRequest ? HeadTimeout : IdleKeepAlive))
                {
                    request = await client.ReadHeadAsync(timeout.Token).ConfigureAwait(false);
                }

                if (request is null)
                {
                    return;
                }

                firstRequest = false;
                string[] parts = request.StartLine.Split(' ', 3);
                if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                {
                    await RespondAsync(client.Stream, 400, "Bad Request", "Not an HTTP request.", ct).ConfigureAwait(false);
                    return;
                }

                string method = parts[0];
                string target = parts[1];
                string version = parts[2];

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    upstream?.Dispose();
                    upstream = null;
                    await TunnelAsync(client, target, ct).ConfigureAwait(false);
                    return;
                }

                bool isAbsolute = Uri.TryCreate(target, UriKind.Absolute, out var uri);
                if (target.StartsWith('/') || (isAbsolute && IsSelf(uri!.DnsSafeHost, uri.Port)))
                {
                    // Someone opened the proxy's own address, e.g. in a browser.
                    await RespondAsync(client.Stream, 200, "OK",
                        "Download Faster is running. This is the proxy that spreads app connections over your adapters.", ct).ConfigureAwait(false);
                    return;
                }

                if (!isAbsolute || uri!.Scheme != Uri.UriSchemeHttp)
                {
                    await RespondAsync(client.Stream, 400, "Bad Request", "Only http:// addresses can be forwarded; HTTPS goes through CONNECT.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                bool clientWantsClose = version == "HTTP/1.0"
                    ? !request.HasToken("Connection", "keep-alive") && !request.HasToken("Proxy-Connection", "keep-alive")
                    : request.HasToken("Connection", "close") || request.HasToken("Proxy-Connection", "close");

                request.StartLine = $"{method} {uri.PathAndQuery} {version}";
                request.Remove("Proxy-Connection");
                request.Remove("Proxy-Authorization");
                if (request.Get("Host") is null)
                {
                    request.Set("Host", uri.Authority);
                }

                bool expectContinue = request.HasToken("Expect", "100-continue");
                request.Remove("Expect");
                bool chunkedBody = request.HasToken("Transfer-Encoding", "chunked");
                long bodyLength = chunkedBody ? 0 : long.TryParse(request.Get("Content-Length"), out long cl) ? cl : 0;
                bool hasBody = chunkedBody || bodyLength > 0;
                string key = $"{uri.DnsSafeHost}:{uri.Port}";

                // Reuse the connection to the same server. A request with a body always gets a fresh
                // one, so it never has to be replayed if a reused connection turns out to be dead.
                bool reuse = upstream is not null && upstreamKey == key && !hasBody;
                if (!reuse)
                {
                    upstream?.Dispose();
                    upstream = await ConnectAsync(uri.DnsSafeHost, uri.Port, ct, plainHttp: true).ConfigureAwait(false);
                    upstreamKey = key;
                    if (upstream is null)
                    {
                        await RespondAsync(client.Stream, 502, "Bad Gateway", $"Could not connect to {uri.Authority}.", ct).ConfigureAwait(false);
                        return;
                    }
                }

                if (expectContinue)
                {
                    await client.Stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                }

                HttpHead response;
                try
                {
                    response = await SendRequestAsync(upstream!, request, client, chunkedBody, bodyLength, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (reuse && ex is IOException or SocketException)
                {
                    // The server had closed the idle connection we reused; try once on a new one.
                    upstream!.Dispose();
                    upstream = await ConnectAsync(uri.DnsSafeHost, uri.Port, ct, plainHttp: true).ConfigureAwait(false);
                    if (upstream is null)
                    {
                        await RespondAsync(client.Stream, 502, "Bad Gateway", $"Could not connect to {uri.Authority}.", ct).ConfigureAwait(false);
                        return;
                    }

                    response = await SendRequestAsync(upstream, request, client, false, 0, ct).ConfigureAwait(false);
                }

                var server = upstream!;
                int status = StatusOf(response);
                while (status is >= 100 and < 200 && status != 101)
                {
                    await client.Stream.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false);
                    response = await ReadResponseHeadAsync(server.Reader, ct).ConfigureAwait(false);
                    status = StatusOf(response);
                }

                await client.Stream.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false);
                var counter = Counter(server, ct);

                if (status == 101)
                {
                    // Protocol switch (e.g. WebSocket): from now on just pass bytes both ways.
                    await RelayAsync(client, server, ct).ConfigureAwait(false);
                    return;
                }

                if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || status is 204 or 304)
                {
                    // No body.
                }
                else if (response.HasToken("Transfer-Encoding", "chunked"))
                {
                    await server.Reader.CopyChunkedAsync(client.Stream, counter, ct).ConfigureAwait(false);
                }
                else if (long.TryParse(response.Get("Content-Length"), out long length))
                {
                    await server.Reader.CopyExactAsync(length, client.Stream, counter, ct).ConfigureAwait(false);
                }
                else
                {
                    // The body ends when the server closes the connection.
                    await server.Reader.CopyToEndAsync(client.Stream, counter, ct).ConfigureAwait(false);
                    return;
                }

                bool serverCloses = response.HasToken("Connection", "close")
                    || (response.StartLine.StartsWith("HTTP/1.0", StringComparison.Ordinal) && !response.HasToken("Connection", "keep-alive"));
                if (serverCloses || clientWantsClose)
                {
                    return;
                }
            }
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private static async Task<HttpHead> SendRequestAsync(
        Upstream upstream, HttpHead request, ProxyReader client, bool chunkedBody, long bodyLength, CancellationToken ct)
    {
        await upstream.Stream.WriteAsync(request.ToBytes(), ct).ConfigureAwait(false);
        if (chunkedBody)
        {
            await client.CopyChunkedAsync(upstream.Stream, null, ct).ConfigureAwait(false);
        }
        else if (bodyLength > 0)
        {
            await client.CopyExactAsync(bodyLength, upstream.Stream, null, ct).ConfigureAwait(false);
        }

        return await ReadResponseHeadAsync(upstream.Reader, ct).ConfigureAwait(false);
    }

    private static async Task<HttpHead> ReadResponseHeadAsync(ProxyReader reader, CancellationToken ct)
    {
        using var timeout = Timeout(ct, HeadTimeout);
        return await reader.ReadHeadAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("The server closed the connection without answering.");
    }

    private static int StatusOf(HttpHead response)
    {
        string[] parts = response.StartLine.Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out int status)
            ? status
            : throw new InvalidDataException("Malformed HTTP status line.");
    }

    private async Task TunnelAsync(ProxyReader client, string target, CancellationToken ct)
    {
        if (!Uri.TryCreate("tcp://" + target, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            await RespondAsync(client.Stream, 400, "Bad Request", "CONNECT needs host:port.", ct).ConfigureAwait(false);
            return;
        }

        using var upstream = await ConnectAsync(uri.DnsSafeHost, uri.Port, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            await RespondAsync(client.Stream, 502, "Bad Gateway", $"Could not connect to {target}.", ct).ConfigureAwait(false);
            return;
        }

        await client.Stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await RelayAsync(client, upstream, ct).ConfigureAwait(false);
    }

    private static async Task RespondAsync(Stream stream, int status, string reason, string text, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(text + "\n");
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- SOCKS

    private async Task HandleSocks5Async(ProxyReader client, CancellationToken ct)
    {
        using var handshake = Timeout(ct, HeadTimeout);
        var t = handshake.Token;
        await client.ReadByteAsync(t).ConfigureAwait(false);
        int methodCount = await client.ReadByteAsync(t).ConfigureAwait(false);
        byte[] methods = await client.ReadExactAsync(methodCount, t).ConfigureAwait(false);
        if (!methods.Contains((byte)0))
        {
            await client.Stream.WriteAsync(new byte[] { 5, 0xFF }, ct).ConfigureAwait(false);
            return;
        }

        await client.Stream.WriteAsync(new byte[] { 5, 0 }, ct).ConfigureAwait(false);
        byte[] header = await client.ReadExactAsync(4, t).ConfigureAwait(false);
        string host;
        switch (header[3])
        {
            case 1:
                host = new IPAddress(await client.ReadExactAsync(4, t).ConfigureAwait(false)).ToString();
                break;
            case 3:
                int length = await client.ReadByteAsync(t).ConfigureAwait(false);
                host = Encoding.ASCII.GetString(await client.ReadExactAsync(length, t).ConfigureAwait(false));
                break;
            case 4:
                host = new IPAddress(await client.ReadExactAsync(16, t).ConfigureAwait(false)).ToString();
                break;
            default:
                await Socks5ReplyAsync(client.Stream, 8, ct).ConfigureAwait(false);
                return;
        }

        byte[] portBytes = await client.ReadExactAsync(2, t).ConfigureAwait(false);
        int port = (portBytes[0] << 8) | portBytes[1];
        if (header[1] != 1)
        {
            // Only CONNECT; BIND and UDP ASSOCIATE are not supported.
            await Socks5ReplyAsync(client.Stream, 7, ct).ConfigureAwait(false);
            return;
        }

        using var upstream = await ConnectAsync(host, port, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            await Socks5ReplyAsync(client.Stream, 4, ct).ConfigureAwait(false);
            return;
        }

        await Socks5ReplyAsync(client.Stream, 0, ct).ConfigureAwait(false);
        await RelayAsync(client, upstream, ct).ConfigureAwait(false);
    }

    private static Task Socks5ReplyAsync(Stream stream, byte code, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    private async Task HandleSocks4Async(ProxyReader client, CancellationToken ct)
    {
        using var handshake = Timeout(ct, HeadTimeout);
        var t = handshake.Token;
        byte[] header = await client.ReadExactAsync(8, t).ConfigureAwait(false);
        int port = (header[2] << 8) | header[3];
        await ReadNullTerminatedAsync(client, t).ConfigureAwait(false);

        // SOCKS4a: an address of 0.0.0.x means a host name follows the user id.
        string host = header[4] == 0 && header[5] == 0 && header[6] == 0 && header[7] != 0
            ? await ReadNullTerminatedAsync(client, t).ConfigureAwait(false)
            : new IPAddress(header.AsSpan(4, 4)).ToString();

        using var upstream = header[1] == 1 ? await ConnectAsync(host, port, ct).ConfigureAwait(false) : null;
        byte[] reply = [0, upstream is null ? (byte)0x5B : (byte)0x5A, 0, 0, 0, 0, 0, 0];
        await client.Stream.WriteAsync(reply, ct).ConfigureAwait(false);
        if (upstream is not null)
        {
            await RelayAsync(client, upstream, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadNullTerminatedAsync(ProxyReader client, CancellationToken ct)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = await client.ReadByteAsync(ct).ConfigureAwait(false)) != 0)
        {
            bytes.Add(b);
            if (bytes.Count > 255)
            {
                throw new InvalidDataException("SOCKS4 field too long.");
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    // ---------------------------------------------------------------- connections

    /// <summary>
    /// Opens a connection through the chosen adapter, falling back to the others and finally to
    /// a normal connection, so turning the proxy on never makes a site unreachable.
    /// </summary>
    private async Task<Upstream?> ConnectAsync(string host, int port, CancellationToken ct, bool plainHttp = false)
    {
        if (IsSelf(host, port))
        {
            return null;
        }

        if (LoopbackGoesDirect && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var literal) && IPAddress.IsLoopback(literal))))
        {
            // Local services are not on any adapter.
            return await ConnectDirectAsync(host, port, ct).ConfigureAwait(false);
        }

        foreach (var route in _balancer.Choose(host, port, plainHttp).Take(3))
        {
            using var timeout = Timeout(ct, ConnectTimeout);
            try
            {
                var stream = await BoundHttpClient.ConnectAsync(route.LocalAddresses, new DnsEndPoint(host, port), timeout.Token)
                    .ConfigureAwait(false);
                _balancer.Succeeded(route);
                return new Upstream(route, (NetworkStream)stream, _balancer);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
            {
                // The name does not resolve; another adapter will not change that.
                return null;
            }
            catch (Exception ex) when (ex is SocketException or IOException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _balancer.Failed(route);
            }
        }

        // No adapter ticked, or none could connect: let Windows pick the way, as without the proxy.
        return await ConnectDirectAsync(host, port, ct).ConfigureAwait(false);
    }

    private async Task<Upstream?> ConnectDirectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = Timeout(ct, ConnectTimeout);
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), timeout.Token).ConfigureAwait(false);
            return new Upstream(null, new NetworkStream(socket, ownsSocket: true), _balancer);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Passes bytes both ways until either side closes.</summary>
    private static async Task RelayAsync(ProxyReader client, Upstream upstream, CancellationToken ct)
    {
        var up = PumpAsync(client, upstream.Stream, null, ct);
        var down = PumpAsync(upstream.Reader, client.Stream, Counter(upstream, ct), ct);
        var first = await Task.WhenAny(up, down).ConfigureAwait(false);

        // Pass the half-close on, then give the other direction a moment to finish.
        var finishedTarget = first == up ? upstream.Stream : client.Stream;
        try
        {
            (finishedTarget as NetworkStream)?.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAny(first == up ? down : up, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)).ConfigureAwait(false);
    }

    private static async Task PumpAsync(ProxyReader from, Stream to, Func<int, ValueTask>? onBytes, CancellationToken ct)
    {
        try
        {
            await from.CopyToEndAsync(to, onBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Counts downloaded bytes for the adapter's stats and applies its speed limit.</summary>
    private static Func<int, ValueTask>? Counter(Upstream upstream, CancellationToken ct)
    {
        if (upstream.Route is not { } route)
        {
            return null;
        }

        return count =>
        {
            upstream.Balancer.AddBytes(route, count);
            return route.Limiter.WaitAsync(count, ct);
        };
    }

    private bool IsSelf(string host, int port) =>
        port == Port && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)));

    private static CancellationTokenSource Timeout(CancellationToken ct, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(after);
        return cts;
    }

    /// <summary>An outgoing connection, counted against its adapter while open.</summary>
    private sealed class Upstream : IDisposable
    {
        private int _disposed;

        public Upstream(NetworkRoute? route, NetworkStream stream, ConnectionBalancer balancer)
        {
            Route = route;
            Stream = stream;
            Balancer = balancer;
            Reader = new ProxyReader(stream);
            if (route is not null)
            {
                balancer.Opened(route);
            }
        }

        public NetworkRoute? Route { get; }

        public NetworkStream Stream { get; }

        public ProxyReader Reader { get; }

        public ConnectionBalancer Balancer { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Stream.Dispose();
            if (Route is not null)
            {
                Balancer.Closed(Route);
            }
        }
    }
}
