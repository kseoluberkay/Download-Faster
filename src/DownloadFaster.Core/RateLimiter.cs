using System.Diagnostics;

namespace DownloadFaster.Core;

/// <summary>
/// Token bucket that lets callers spend bytes first and then pay back the debt by
/// waiting. The limit is read on every call, so it can be changed at any time.
/// </summary>
public sealed class RateLimiter
{
    private const int MinBurstBytes = 64 * 1024;

    private readonly Func<long> _bytesPerSecond;
    private readonly Lock _gate = new();
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    public RateLimiter(Func<long> bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
    }

    public ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        long limit = _bytesPerSecond();
        if (limit <= 0)
        {
            return ValueTask.CompletedTask;
        }

        double delaySeconds;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;

            // Allow roughly a quarter second of burst so short pauses are not penalised.
            double burst = Math.Max(limit / 4.0, MinBurstBytes);
            _tokens = Math.Min(_tokens + (elapsed * limit), burst);
            _tokens -= bytes;
            delaySeconds = _tokens < 0 ? -_tokens / limit : 0;
        }

        return delaySeconds > 0
            ? new ValueTask(Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken))
            : ValueTask.CompletedTask;
    }
}
