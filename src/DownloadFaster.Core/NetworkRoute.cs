using System.Net;

namespace DownloadFaster.Core;

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Cellular,
    Other,
}

/// <summary>
/// One way out to the internet: a network adapter plus the local addresses that
/// connections are bound to so their traffic leaves through that adapter.
/// </summary>
public sealed class NetworkRoute
{
    public NetworkRoute(string id, string name, AdapterKind kind, IReadOnlyList<IPAddress> localAddresses, RouteSettings? settings = null)
    {
        if (localAddresses.Count == 0)
        {
            throw new ArgumentException("A route needs at least one local address.", nameof(localAddresses));
        }

        Id = id;
        Name = name;
        Kind = kind;
        LocalAddresses = localAddresses;
        Settings = settings ?? new RouteSettings();
    }

    public string Id { get; }

    /// <summary>Friendly adapter name, e.g. "Wi-Fi" or "Ethernet 2".</summary>
    public string Name { get; }

    /// <summary>Hardware description, e.g. "Intel(R) Wi-Fi 6 AX201 160MHz".</summary>
    public string Description { get; init; } = "";

    public AdapterKind Kind { get; }

    public IReadOnlyList<IPAddress> LocalAddresses { get; }

    /// <summary>Negotiated link speed reported by the driver, or 0 when unknown.</summary>
    public long LinkSpeedBitsPerSecond { get; init; }

    /// <summary>True for Hyper-V, VPN, VirtualBox and similar adapters.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>User-adjustable settings. Changes take effect while downloads are running.</summary>
    public RouteSettings Settings { get; }

    /// <summary>Speed limiter shared by every download that uses this route.</summary>
    public RateLimiter Limiter => Settings.Limiter;

    public string KindLabel => Kind switch
    {
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.Cellular => "Cellular",
        _ => "Other",
    };

    public override string ToString() => $"{Name} ({KindLabel}, {string.Join(", ", LocalAddresses)})";
}

/// <summary>
/// Per-adapter knobs. Every property is safe to change from the UI thread while
/// downloads read it from worker threads.
/// </summary>
public sealed class RouteSettings
{
    public const int MaxConnections = 32;

    private volatile bool _enabled = true;
    private int _connections = 4;
    private long _speedLimit;
    private int _share = 50;

    public RouteSettings()
    {
        Limiter = new RateLimiter(() => SpeedLimitBytesPerSecond);
    }

    /// <summary>Whether downloads may use this adapter at all.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Parallel connections opened through this adapter, per download.</summary>
    public int Connections
    {
        get => Volatile.Read(ref _connections);
        set => Volatile.Write(ref _connections, Math.Clamp(value, 1, MaxConnections));
    }

    /// <summary>Speed cap for this adapter in bytes per second. 0 means unlimited.</summary>
    public long SpeedLimitBytesPerSecond
    {
        get => Interlocked.Read(ref _speedLimit);
        set => Interlocked.Exchange(ref _speedLimit, Math.Max(0, value));
    }

    /// <summary>Applies <see cref="SpeedLimitBytesPerSecond"/> across all downloads using the adapter.</summary>
    public RateLimiter Limiter { get; }

    /// <summary>Relative weight (0-100) used by <see cref="BalanceMode.FixedSplit"/>.</summary>
    public int Share
    {
        get => Volatile.Read(ref _share);
        set => Volatile.Write(ref _share, Math.Clamp(value, 0, 100));
    }
}
