using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace DownloadFaster.Core;

/// <summary>
/// Builds <see cref="HttpClient"/>s whose TCP connections are bound to one adapter's
/// local address. Windows sends packets out of the adapter that owns the source
/// address, which is what lets Wi-Fi and Ethernet download at the same time.
/// </summary>
public static class BoundHttpClient
{
    public static readonly ProductInfoHeaderValue UserAgent = new("DownloadFaster", "1.0");

    public static HttpClient Create(NetworkRoute route, TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            // A system proxy would send everything through one adapter and defeat the binding.
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            MaxConnectionsPerServer = 256,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = connectTimeout,
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(route.LocalAddresses, context.DnsEndPoint, cancellationToken),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            // HTTP/1.1 keeps each segment on its own TCP connection; HTTP/2 would multiplex them onto one.
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
    }

    internal static async ValueTask<Stream> ConnectAsync(
        IReadOnlyList<IPAddress> localAddresses, DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        IPAddress[] remotes = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        foreach (var local in localAddresses)
        {
            foreach (var remote in remotes.Where(r => r.AddressFamily == local.AddressFamily))
            {
                var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(remote, endPoint.Port, cancellationToken).ConfigureAwait(false);
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
        }

        throw lastError ?? new IOException(
            $"{endPoint.Host} has no address this adapter can reach (adapter: {string.Join(", ", localAddresses)}).");
    }
}
