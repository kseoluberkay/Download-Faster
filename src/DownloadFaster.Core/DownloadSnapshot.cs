namespace DownloadFaster.Core;

public enum DownloadState
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Failed,
}

/// <summary>How one adapter is doing within one download.</summary>
public sealed record RouteSnapshot(
    string RouteId,
    string Name,
    AdapterKind Kind,
    long Bytes,
    double BytesPerSecond,
    int Connections,
    string? Problem);

/// <summary>Point-in-time view of a download for the UI.</summary>
public sealed record DownloadSnapshot(
    DownloadState State,
    string? FileName,
    long? TotalBytes,
    long DownloadedBytes,
    double BytesPerSecond,
    bool IsSplit,
    string? Message,
    IReadOnlyList<RouteSnapshot> Routes)
{
    public double? Fraction => State == DownloadState.Completed
        ? 1
        : TotalBytes is > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Eta => State == DownloadState.Downloading && TotalBytes is long total && BytesPerSecond > 1
        ? TimeSpan.FromSeconds(Math.Max(0, total - DownloadedBytes) / BytesPerSecond)
        : null;
}
