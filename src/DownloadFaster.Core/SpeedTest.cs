using System.Buffers;
using System.Diagnostics;

namespace DownloadFaster.Core;

/// <summary>Measures how fast one adapter can download on its own.</summary>
public static class SpeedTest
{
    public static readonly Uri DefaultUrl = new("https://speed.cloudflare.com/__down?bytes=25000000");

    /// <returns>Average bytes per second over the test.</returns>
    public static async Task<double> RunAsync(
        NetworkRoute route,
        Uri testUrl,
        int connections,
        TimeSpan duration,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var client = BoundHttpClient.Create(route, TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(duration);

        long total = 0;
        var clock = Stopwatch.StartNew();
        Exception? lastError = null;

        async Task PumpAsync()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead, stop.Token)
                            .ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                        await using (stream.ConfigureAwait(false))
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                            {
                                Interlocked.Add(ref total, read);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        lastError = ex;
                        await Task.Delay(250, stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var pumps = Enumerable.Range(0, Math.Max(1, connections)).Select(_ => Task.Run(PumpAsync)).ToArray();
        var all = Task.WhenAll(pumps);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            progress?.Report(Interlocked.Read(ref total) / Math.Max(0.001, clock.Elapsed.TotalSeconds));
        }

        await all.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        long bytes = Interlocked.Read(ref total);
        if (bytes == 0 && lastError is not null)
        {
            throw new HttpRequestException(SegmentedDownload.Describe(lastError), lastError);
        }

        return bytes / Math.Max(0.001, clock.Elapsed.TotalSeconds);
    }
}
