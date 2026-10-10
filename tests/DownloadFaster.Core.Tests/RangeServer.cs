using System.Collections.Concurrent;
using System.Net;
using DownloadFaster.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DownloadFaster.Core.Tests;

/// <summary>
/// Local HTTP server that serves one file with optional range support. It tells clients
/// apart by source address, which is how the tests check which "adapter" did the work.
/// </summary>
internal sealed class RangeServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RangeServer(WebApplication app, byte[] content)
    {
        _app = app;
        Content = content;
    }

    public byte[] Content { get; set; }

    public bool SupportRanges { get; set; } = true;

    public string? ContentDispositionName { get; set; }

    /// <summary>Bytes sent per client address.</summary>
    public ConcurrentDictionary<string, long> BytesByClient { get; } = new();

    /// <summary>Speed caps per client address, in bytes per second.</summary>
    public ConcurrentDictionary<string, long> ThrottleByClient { get; } = new();

    /// <summary>Number of upcoming responses per client address to cut off halfway.</summary>
    public ConcurrentDictionary<string, int> DropsByClient { get; } = new();

    /// <summary>Number of upcoming requests (from anyone) to answer with 503 Service Unavailable.</summary>
    public int RejectNextRequests;

    public Uri Url { get; private set; } = null!;

    private ConcurrentDictionary<string, RateLimiter> Limiters { get; } = new();

    public long BytesFrom(string client) => BytesByClient.GetValueOrDefault(client);

    public long TotalBytesSent => BytesByClient.Values.Sum();

    public static async Task<RangeServer> StartAsync(byte[] content)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new RangeServer(app, content);
        app.Run(server.HandleAsync);
        await app.StartAsync();
        server.Url = new Uri(app.Urls.First().TrimEnd('/') + "/files/test.bin");
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        string client = context.Connection.RemoteIpAddress!.MapToIPv4().ToString();
        if (Interlocked.Decrement(ref RejectNextRequests) >= 0)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        long length = Content.Length;
        long start = 0;
        long end = length - 1;
        bool partial = false;

        string range = context.Request.Headers.Range.ToString();
        if (SupportRanges && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            string[] parts = range["bytes=".Length..].Split('-');
            start = long.Parse(parts[0]);
            end = parts[1].Length > 0 ? Math.Min(long.Parse(parts[1]), length - 1) : length - 1;
            if (start >= length)
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.ContentRange = $"bytes */{length}";
                return;
            }

            partial = true;
        }

        context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentLength = end - start + 1;
        if (SupportRanges)
        {
            context.Response.Headers.AcceptRanges = "bytes";
        }

        if (partial)
        {
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        }

        if (ContentDispositionName is not null)
        {
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{ContentDispositionName}\"";
        }

        bool drop = false;
        DropsByClient.AddOrUpdate(client, 0, (_, remaining) =>
        {
            drop = remaining > 0;
            return Math.Max(0, remaining - 1);
        });

        var limiter = Limiters.GetOrAdd(client, c => new RateLimiter(() => ThrottleByClient.GetValueOrDefault(c)));
        long half = start + ((end - start + 1) / 2);
        try
        {
            for (long position = start; position <= end;)
            {
                int count = (int)Math.Min(16 * 1024, end - position + 1);
                await limiter.WaitAsync(count, context.RequestAborted);
                await context.Response.Body.WriteAsync(Content.AsMemory((int)position, count), context.RequestAborted);
                BytesByClient.AddOrUpdate(client, count, (_, total) => total + count);
                position += count;

                if (drop && position >= half)
                {
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    context.Abort();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client hung up, which happens whenever a segment is split.
        }
    }
}
