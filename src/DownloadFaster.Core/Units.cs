using System.Globalization;

namespace DownloadFaster.Core;

/// <summary>Human-readable sizes, speeds and durations.</summary>
public static class Units
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        // Move up a unit from 1000 so values never show four digits ("1003.8 KB").
        while (value >= 1000 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {ByteUnits[unit]}");
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    /// <summary>Speed in megabits per second, the unit internet plans are sold in.</summary>
    public static string Mbps(double bytesPerSecond) =>
        string.Create(CultureInfo.CurrentCulture, $"{bytesPerSecond * 8 / 1_000_000:0} Mbps");

    public static string LinkSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "",
        >= 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{bitsPerSecond / 1e9:0.#} Gbps link"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bitsPerSecond / 1e6:0} Mbps link"),
    };

    public static string Duration(TimeSpan time) => time switch
    {
        { TotalHours: >= 1 } => $"{(int)time.TotalHours}h {time.Minutes:00}m",
        { TotalMinutes: >= 1 } => $"{time.Minutes}m {time.Seconds:00}s",
        _ => $"{Math.Max(0, (int)Math.Ceiling(time.TotalSeconds))}s",
    };
}
