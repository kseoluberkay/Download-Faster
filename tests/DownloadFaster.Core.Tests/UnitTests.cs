using System.Diagnostics;
using System.Net;
using DownloadFaster.Core;

namespace DownloadFaster.Core.Tests;

public sealed class FileNamesTests
{
    [Theory]
    [InlineData("movie.mkv", "movie.mkv")]
    [InlineData("a:b?c*.txt", "a_b_c_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("   ", "download")]
    public void Sanitize_makes_names_safe_for_Windows(string input, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Url_names_are_unescaped()
    {
        Assert.Equal("my file.zip", FileNames.FromUrl(new Uri("https://example.com/dl/my%20file.zip?token=1")));
        Assert.Null(FileNames.FromUrl(new Uri("https://example.com/")));
    }

    [Fact]
    public void MakeUnique_numbers_taken_names()
    {
        var taken = new HashSet<string> { "a.zip", "a (2).zip" };
        Assert.Equal("a (3).zip", FileNames.MakeUnique("a.zip", taken.Contains));
        Assert.Equal("b.zip", FileNames.MakeUnique("b.zip", taken.Contains));
    }
}

public sealed class UnitsTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_027_891, "1.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Bytes_are_readable(long bytes, string expected) =>
        Assert.Equal(expected, Units.Bytes(bytes).Replace(',', '.'));

    [Fact]
    public void Durations_are_short()
    {
        Assert.Equal("45s", Units.Duration(TimeSpan.FromSeconds(44.2)));
        Assert.Equal("3m 05s", Units.Duration(TimeSpan.FromSeconds(185)));
        Assert.Equal("2h 01m", Units.Duration(TimeSpan.FromMinutes(121)));
    }
}

public sealed class RouteSettingsTests
{
    [Fact]
    public void Values_are_clamped()
    {
        var settings = new RouteSettings { Connections = 500, Share = -3, SpeedLimitBytesPerSecond = -1 };
        Assert.Equal(RouteSettings.MaxConnections, settings.Connections);
        Assert.Equal(0, settings.Share);
        Assert.Equal(0, settings.SpeedLimitBytesPerSecond);
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public async Task Unlimited_never_waits()
    {
        var limiter = new RateLimiter(() => 0);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            await limiter.WaitAsync(1024 * 1024, CancellationToken.None);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Limited_spreads_bytes_over_time()
    {
        var limiter = new RateLimiter(() => 1024 * 1024);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await limiter.WaitAsync(100 * 1024, CancellationToken.None);
        }

        // 2000 KB at 1024 KB/s, minus the quarter-second burst allowance.
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(1.5), $"Took only {clock.Elapsed}");
    }
}

public sealed class BindingTests
{
    [Fact]
    public async Task Connections_leave_from_the_bound_address()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptTcpClientAsync();
        await using var stream = await BoundHttpClient.ConnectAsync(
            [IPAddress.Parse("127.0.0.2")], new DnsEndPoint("127.0.0.1", port), CancellationToken.None);
        using var server = await accept;

        Assert.Equal(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)server.Client.RemoteEndPoint!).Address);
    }

    [Fact]
    public void Discovery_only_returns_adapters_with_usable_addresses()
    {
        foreach (var route in AdapterDiscovery.Discover())
        {
            Assert.NotEmpty(route.LocalAddresses);
            Assert.All(route.LocalAddresses, a => Assert.True(AdapterDiscovery.IsUsableAddress(a)));
        }
    }

    [Theory]
    [InlineData("169.254.10.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("2a02:1810::1", true)]
    public void Usable_addresses(string address, bool usable) =>
        Assert.Equal(usable, AdapterDiscovery.IsUsableAddress(IPAddress.Parse(address)));
}
