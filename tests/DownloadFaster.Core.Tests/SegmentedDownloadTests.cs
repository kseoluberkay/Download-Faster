using System.Diagnostics;
using System.Net;
using DownloadFaster.Core;

namespace DownloadFaster.Core.Tests;

/// <summary>
/// End-to-end tests against a local server. 127.0.0.1 and 127.0.0.2 stand in for the
/// Ethernet and Wi-Fi adapters: binding to each one gives the server a different client address.
/// </summary>
public sealed class SegmentedDownloadTests : IAsyncLifetime
{
    private const string Ethernet = "127.0.0.1";
    private const string WiFi = "127.0.0.2";
    private const long MB = DownloadOptions.MiB;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "df-tests-" + Guid.NewGuid().ToString("N"));
    private RangeServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _server = await RangeServer.StartAsync(RandomBytes(16 * MB));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Uses_both_adapters_and_writes_an_identical_file()
    {
        _server.ThrottleByClient[Ethernet] = 12 * MB;
        _server.ThrottleByClient[WiFi] = 8 * MB;
        var download = NewDownload([Route(Ethernet), Route(WiFi)]);

        await download.RunAsync();

        AssertCompleted(download);
        Assert.True(_server.BytesFrom(Ethernet) > 2 * MB, $"Ethernet sent {_server.BytesFrom(Ethernet)} bytes");
        Assert.True(_server.BytesFrom(WiFi) > 2 * MB, $"Wi-Fi sent {_server.BytesFrom(WiFi)} bytes");

        var snapshot = download.GetSnapshot();
        Assert.True(snapshot.IsSplit);
        Assert.Equal(_server.Content.Length, snapshot.Routes.Sum(r => r.Bytes));
    }

    [Fact]
    public async Task Falls_back_to_one_connection_when_the_server_cannot_send_parts()
    {
        _server.SupportRanges = false;
        var download = NewDownload([Route(Ethernet), Route(WiFi)]);

        await download.RunAsync();

        AssertCompleted(download);
        Assert.False(download.GetSnapshot().IsSplit);
        Assert.Equal(0, _server.BytesFrom(WiFi));
    }

    [Fact]
    public async Task Fixed_split_follows_the_share_sliders()
    {
        _server.Content = RandomBytes(32 * MB);
        _server.ThrottleByClient[Ethernet] = 20 * MB;
        _server.ThrottleByClient[WiFi] = 20 * MB;
        var ethernet = Route(Ethernet, share: 75);
        var wifi = Route(WiFi, share: 25);
        var download = NewDownload([ethernet, wifi], new DownloadOptions { Mode = BalanceMode.FixedSplit, ChunkSizeBytes = 1 * MB });

        await download.RunAsync();

        AssertCompleted(download);
        var routes = download.GetSnapshot().Routes;
        double wifiShare = (double)routes.Single(r => r.RouteId == wifi.Id).Bytes / _server.Content.Length;
        Assert.InRange(wifiShare, 0.17, 0.33);
    }

    [Fact]
    public async Task Fixed_split_does_not_starve_an_adapter_that_ran_alone_earlier()
    {
        _server.Content = RandomBytes(40 * MB);
        _server.ThrottleByClient[Ethernet] = 3 * MB;
        _server.ThrottleByClient[WiFi] = 3 * MB;
        var ethernet = Route(Ethernet);
        var wifi = Route(WiFi);
        wifi.Settings.Enabled = false;
        var download = NewDownload([ethernet, wifi], new DownloadOptions { Mode = BalanceMode.FixedSplit, ChunkSizeBytes = 1 * MB });

        var run = download.RunAsync();
        await Task.Delay(3000);
        wifi.Settings.Enabled = true;

        // Ethernet's in-flight pieces are done by now; it must keep getting new ones.
        await Task.Delay(2000);
        long before = EthernetBytes();
        await Task.Delay(1500);
        long after = EthernetBytes();
        await run;

        AssertCompleted(download);
        Assert.True(after - before > 2 * MB, $"Ethernet only got {after - before} bytes after Wi-Fi came back");

        long EthernetBytes() => download.GetSnapshot().Routes.Single(r => r.RouteId == ethernet.Id).Bytes;
    }

    [Fact]
    public async Task Keeps_going_when_one_adapter_cannot_connect()
    {
        // 198.51.100.0/24 is reserved for documentation, so no machine owns this address and binding fails.
        var broken = Route("198.51.100.77");
        var download = NewDownload([broken, Route(Ethernet)]);

        await download.RunAsync();

        AssertCompleted(download);
        var brokenSnapshot = download.GetSnapshot().Routes.Single(r => r.RouteId == broken.Id);
        Assert.Equal(0, brokenSnapshot.Bytes);
    }

    [Fact]
    public async Task Retries_connections_that_drop_halfway()
    {
        _server.DropsByClient[Ethernet] = 4;
        _server.DropsByClient[WiFi] = 4;
        var download = NewDownload([Route(Ethernet), Route(WiFi)]);

        await download.RunAsync();

        AssertCompleted(download);
    }

    [Fact]
    public async Task Retries_when_the_very_first_request_fails_on_every_adapter()
    {
        _server.RejectNextRequests = 2;
        var download = NewDownload([Route(Ethernet), Route(WiFi)]);

        await download.RunAsync();

        AssertCompleted(download);
    }

    [Fact]
    public async Task Pause_keeps_the_partial_file_and_resume_finishes_it()
    {
        _server.ThrottleByClient[Ethernet] = 4 * MB;
        var download = NewDownload([Route(Ethernet)]);

        using (var pause = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
        {
            await download.RunAsync(pause.Token);
        }

        var paused = download.GetSnapshot();
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.InRange(paused.DownloadedBytes, 1, _server.Content.Length - 1);
        Assert.True(File.Exists(download.FilePath + SegmentedDownload.PartSuffix));
        Assert.True(File.Exists(download.FilePath + SegmentedDownload.StateSuffix));

        // A fresh object (as after restarting the app) finds the partial file by name.
        _server.ThrottleByClient[Ethernet] = 0;
        var resumed = NewDownload([Route(Ethernet)], fileName: download.FileName);
        var restored = resumed.GetSnapshot();
        Assert.Equal(_server.Content.Length, restored.TotalBytes);
        Assert.InRange(restored.DownloadedBytes, 1, paused.DownloadedBytes);
        await resumed.RunAsync();

        AssertCompleted(resumed);
        Assert.True(
            _server.TotalBytesSent < _server.Content.Length + (6 * MB),
            $"Resume re-downloaded too much: {_server.TotalBytesSent} bytes sent");
        Assert.False(File.Exists(resumed.FilePath + SegmentedDownload.StateSuffix));
    }

    [Fact]
    public async Task Same_link_resumes_into_the_same_file_without_being_told_the_name()
    {
        _server.ThrottleByClient[Ethernet] = 4 * MB;
        var first = NewDownload([Route(Ethernet)]);
        using (var pause = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await first.RunAsync(pause.Token);
        }

        _server.ThrottleByClient[Ethernet] = 0;
        var second = NewDownload([Route(Ethernet)]);
        await second.RunAsync();

        AssertCompleted(second);
        Assert.Equal(first.FileName, second.FileName);
    }

    [Fact]
    public async Task A_slow_adapter_does_not_hold_up_the_finish()
    {
        _server.ThrottleByClient[WiFi] = 256 * 1024;
        var download = NewDownload(
            [Route(Ethernet, connections: 2), Route(WiFi, connections: 2)],
            new DownloadOptions { ChunkSizeBytes = 4 * MB });

        var clock = Stopwatch.StartNew();
        await download.RunAsync();

        AssertCompleted(download);

        // Without splitting, the slow adapter's two 4 MB chunks alone would take 32 seconds,
        // and leaving it even 1 MB would take 8.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task Speed_limit_slows_the_adapter_down()
    {
        _server.Content = RandomBytes(6 * MB);
        var route = Route(Ethernet);
        route.Settings.SpeedLimitBytesPerSecond = 2 * MB;
        var download = NewDownload([route]);

        var clock = Stopwatch.StartNew();
        await download.RunAsync();

        AssertCompleted(download);
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(2.2), $"Took only {clock.Elapsed}");
    }

    [Fact]
    public async Task Turning_an_adapter_off_mid_download_moves_its_work_to_the_other()
    {
        _server.ThrottleByClient[Ethernet] = 3 * MB;
        _server.ThrottleByClient[WiFi] = 3 * MB;
        var wifi = Route(WiFi);
        var download = NewDownload([Route(Ethernet), wifi], new DownloadOptions { ChunkSizeBytes = 1 * MB });

        var run = download.RunAsync();
        await Task.Delay(1500);
        wifi.Settings.Enabled = false;
        await Task.Delay(500);
        long wifiBytesAfterOff = WiFiBytes();
        await run;

        AssertCompleted(download);
        Assert.True(wifiBytesAfterOff > 0);
        Assert.Equal(wifiBytesAfterOff, WiFiBytes());

        long WiFiBytes() => download.GetSnapshot().Routes.Single(r => r.RouteId == wifi.Id).Bytes;
    }

    [Fact]
    public async Task Uses_the_name_the_server_suggests_and_never_overwrites()
    {
        _server.ContentDispositionName = "report final.pdf";
        await File.WriteAllTextAsync(Path.Combine(_folder, "report final.pdf"), "keep me");
        var download = NewDownload([Route(Ethernet)]);

        await download.RunAsync();

        AssertCompleted(download);
        Assert.Equal("report final (2).pdf", download.FileName);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(_folder, "report final.pdf")));
    }

    [Fact]
    public async Task Handles_an_empty_file()
    {
        _server.Content = [];
        var download = NewDownload([Route(Ethernet)]);

        await download.RunAsync();

        AssertCompleted(download);
    }

    [Fact]
    public async Task Fails_cleanly_when_no_adapter_is_turned_on()
    {
        var route = Route(Ethernet);
        route.Settings.Enabled = false;
        var download = NewDownload([route]);

        await download.RunAsync();

        var snapshot = download.GetSnapshot();
        Assert.Equal(DownloadState.Failed, snapshot.State);
        Assert.Contains("adapter", snapshot.Message);
    }

    private static NetworkRoute Route(string address, int connections = 3, int share = 50) =>
        new($"route-{address}", $"Adapter {address}", AdapterKind.Ethernet, [IPAddress.Parse(address)])
        {
            Settings = { Connections = connections, Share = share },
        };

    private SegmentedDownload NewDownload(IReadOnlyList<NetworkRoute> routes, DownloadOptions? options = null, string? fileName = null) =>
        new(_server.Url, _folder, routes, options ?? new DownloadOptions { ChunkSizeBytes = 1 * MB }, fileName);

    private void AssertCompleted(SegmentedDownload download)
    {
        var snapshot = download.GetSnapshot();
        Assert.True(snapshot.State == DownloadState.Completed, $"State {snapshot.State}: {snapshot.Message}");
        Assert.Equal(_server.Content, File.ReadAllBytes(download.FilePath!));
        Assert.False(File.Exists(download.FilePath + SegmentedDownload.PartSuffix));
    }

    private static byte[] RandomBytes(long count)
    {
        var bytes = new byte[count];
        new Random(42).NextBytes(bytes);
        return bytes;
    }
}
