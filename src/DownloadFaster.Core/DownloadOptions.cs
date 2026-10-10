namespace DownloadFaster.Core;

public enum BalanceMode
{
    /// <summary>Every adapter pulls work as fast as it can. Gives the highest total speed.</summary>
    Auto,

    /// <summary>Work is handed out in proportion to each adapter's <see cref="RouteSettings.Share"/>.</summary>
    FixedSplit,
}

/// <summary>
/// Engine settings. One instance can be shared by every download; <see cref="Mode"/>
/// is read live, the rest is read when a download (re)starts.
/// </summary>
public sealed class DownloadOptions
{
    public const long MiB = 1024 * 1024;

    private volatile BalanceMode _mode = BalanceMode.Auto;

    public BalanceMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>Size of the pieces the file is cut into before adapters pick them up.</summary>
    public long ChunkSizeBytes { get; set; } = 4 * MiB;

    /// <summary>Smallest piece an idle connection may split off a slower one near the end.</summary>
    public long MinSplitBytes { get; set; } = 1 * MiB;

    public int BufferSize { get; set; } = 128 * 1024;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a connection may stay silent before it is retried.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Failures in a row, across all adapters, before the download gives up.</summary>
    public int MaxConsecutiveErrors { get; set; } = 30;

    public TimeSpan StateSaveInterval { get; set; } = TimeSpan.FromSeconds(2);
}
