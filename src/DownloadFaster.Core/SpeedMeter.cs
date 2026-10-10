using System.Diagnostics;

namespace DownloadFaster.Core;

/// <summary>
/// Turns a growing byte counter into a smoothed speed using a sliding time window.
/// </summary>
public sealed class SpeedMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private readonly Lock _gate = new();
    private double _bytesPerSecond;

    public SpeedMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
    }

    /// <summary>Records the current value of a monotonically increasing byte counter.</summary>
    public void Sample(long totalBytes)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _samples.Enqueue((now, totalBytes));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Timestamp, now) > _window)
            {
                _samples.Dequeue();
            }

            var (oldestTime, oldestBytes) = _samples.Peek();
            double seconds = Stopwatch.GetElapsedTime(oldestTime, now).TotalSeconds;
            _bytesPerSecond = seconds > 0.05 ? Math.Max(0, totalBytes - oldestBytes) / seconds : 0;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _bytesPerSecond = 0;
        }
    }
}
