using DownloadFaster.Core;

namespace DownloadFaster.App;

/// <summary>A row in the downloads list.</summary>
internal sealed class DownloadItem(SegmentedDownload download)
{
    public SegmentedDownload Download { get; } = download;

    public CancellationTokenSource? Cts { get; set; }

    public Task Run { get; set; } = Task.CompletedTask;

    public DownloadSnapshot Snapshot { get; set; } = download.GetSnapshot();

    public ListViewItem? Row { get; set; }

    public bool IsRunning => !Run.IsCompleted;

    public bool CanResume => !IsRunning && Snapshot.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Queued;
}

/// <summary>ListView without flicker when rows refresh twice a second.</summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
    }
}
