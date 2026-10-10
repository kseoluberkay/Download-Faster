namespace DownloadFaster.Core.Proxy;

/// <summary>How freely connections from other apps may be spread over the adapters.</summary>
public enum SpreadPolicy
{
    /// <summary>
    /// Only downloads are spread: plain-HTTP traffic (game and update CDNs, including Steam) and
    /// known download hosts. Everything else stays on one adapter, so logins never see the
    /// public IP address change.
    /// </summary>
    DownloadsOnly,

    /// <summary>Each website stays on one adapter; different websites use different adapters.</summary>
    PerSite,

    /// <summary>Every new connection may use any adapter. Fastest, but some sites may log you out.</summary>
    PerConnection,
}

/// <summary>Live traffic through one adapter from the all-apps proxy.</summary>
public sealed record RouteTraffic(string RouteId, int ActiveConnections, double BytesPerSecond, long TotalBytes);

/// <summary>
/// Picks the adapter for each new connection: the least busy one (weighted by the Share
/// sliders in fixed-split mode), unless the connection belongs to a site that must stay on
/// the adapter it already uses.
/// </summary>
public sealed class ConnectionBalancer
{
    /// <summary>Hosts (and their subdomains) that only serve downloads and can be spread freely.</summary>
    public static readonly IReadOnlyList<string> DefaultDownloadHosts =
    [
        "steamcontent.com", "steamserver.net", "steampipe.akamaized.net",
        "download.epicgames.com", "download2.epicgames.com", "download3.epicgames.com", "download4.epicgames.com",
        "epicgames-download1.akamaized.net", "fastly-download.epicgames.com", "egdownload.fastly-edge.com",
        "level3.blizzard.com", "blzddist1-a.akamaihd.net", "origin-a.akamaihd.net",
        "gog-cdn-fastly.gog.com", "cdn.gog.com", "uplaypc-s-ubisoft.cdn.ubi.com",
        "download.windowsupdate.com", "dl.delivery.mp.microsoft.com", "dl.google.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    ];

    private const string SharedStickyKey = "*";

    private readonly Func<IReadOnlyList<NetworkRoute>> _routes;
    private readonly DownloadOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RouteState> _states = [];
    private readonly Dictionary<string, (string RouteId, long LastUse)> _sticky = new(StringComparer.OrdinalIgnoreCase);
    private volatile SpreadPolicy _policy = SpreadPolicy.DownloadsOnly;
    private IReadOnlyList<string> _downloadHosts = DefaultDownloadHosts;
    private int _rotation;

    /// <param name="routes">Returns the current adapters; read on every new connection.</param>
    /// <param name="options">Supplies the live balancing mode (automatic or fixed split).</param>
    public ConnectionBalancer(Func<IReadOnlyList<NetworkRoute>> routes, DownloadOptions options)
    {
        _routes = routes;
        _options = options;
    }

    public SpreadPolicy Policy
    {
        get => _policy;
        set => _policy = value;
    }

    public IReadOnlyList<string> DownloadHosts
    {
        get => Volatile.Read(ref _downloadHosts);
        set => Volatile.Write(ref _downloadHosts, value);
    }

    /// <summary>How long a site stays pinned to its adapter after its last connection.</summary>
    public TimeSpan StickyFor { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Adapters to try for a new connection to <paramref name="host"/>, best first. The rest are
    /// fallbacks in case the first cannot connect. Empty when no adapter is turned on.
    /// </summary>
    /// <param name="plainHttp">The app sent an unencrypted HTTP request, which is almost always a download.</param>
    public IReadOnlyList<NetworkRoute> Choose(string host, int port, bool plainHttp = false)
    {
        var enabled = _routes().Where(r => r.Settings.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            var healthy = enabled.Where(r => !State(r).InCooldown).ToList();
            if (healthy.Count == 0)
            {
                healthy = enabled;
            }

            string? key = StickyKey(host, port, plainHttp);
            long now = Environment.TickCount64;
            NetworkRoute? chosen = null;
            if (key is not null
                && _sticky.TryGetValue(key, out var pinned)
                && now - pinned.LastUse < StickyFor.TotalMilliseconds)
            {
                chosen = healthy.FirstOrDefault(r => r.Id == pinned.RouteId);
            }

            chosen ??= LeastBusy(healthy);
            if (key is not null)
            {
                _sticky[key] = (chosen.Id, now);
                PruneSticky(now);
            }

            return [chosen, .. healthy.Where(r => r != chosen)];
        }
    }

    /// <summary>The key a connection is pinned by, or null when it may use any adapter.</summary>
    internal string? StickyKey(string host, int port, bool plainHttp = false)
    {
        // Unencrypted HTTP carries no secure logins worth protecting; game and update CDNs (Steam
        // included) use it for their downloads, so it is always spread.
        var policy = _policy;
        if (policy == SpreadPolicy.PerConnection || plainHttp || port == 80 || IsDownloadHost(host))
        {
            return null;
        }

        return policy == SpreadPolicy.PerSite ? host : SharedStickyKey;
    }

    public void Opened(NetworkRoute route)
    {
        lock (_gate)
        {
            State(route).Active++;
        }
    }

    public void Closed(NetworkRoute route)
    {
        lock (_gate)
        {
            State(route).Active--;
        }
    }

    public void Succeeded(NetworkRoute route)
    {
        lock (_gate)
        {
            State(route).Failures = 0;
        }
    }

    /// <summary>Records a failed connect; an adapter that keeps failing is skipped for a while.</summary>
    public void Failed(NetworkRoute route)
    {
        lock (_gate)
        {
            var state = State(route);
            state.Failures++;
            if (state.Failures >= 3)
            {
                state.CooldownUntil = Environment.TickCount64 + (Math.Min(60, 5 << Math.Min(state.Failures - 3, 4)) * 1000L);
            }
        }
    }

    public void AddBytes(NetworkRoute route, int count)
    {
        RouteState state;
        lock (_gate)
        {
            state = State(route);
        }

        Interlocked.Add(ref state.Bytes, count);
    }

    public IReadOnlyList<RouteTraffic> GetTraffic()
    {
        lock (_gate)
        {
            return _states.Select(kv => new RouteTraffic(
                kv.Key, kv.Value.Active, kv.Value.Meter.BytesPerSecond, Interlocked.Read(ref kv.Value.Bytes))).ToList();
        }
    }

    /// <summary>Updates the speed readings. Call a few times per second.</summary>
    public void SampleSpeeds()
    {
        List<RouteState> states;
        lock (_gate)
        {
            states = [.. _states.Values];
        }

        foreach (var state in states)
        {
            state.Meter.Sample(Interlocked.Read(ref state.Bytes));
        }
    }

    private bool IsDownloadHost(string host)
    {
        foreach (string suffix in DownloadHosts)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The adapter with the fewest open connections for its weight. Weights are equal in
    /// automatic mode, so slow adapters, whose connections stay open longer, get fewer new ones.
    /// </summary>
    private NetworkRoute LeastBusy(List<NetworkRoute> candidates)
    {
        bool fixedSplit = _options.Mode == BalanceMode.FixedSplit;
        var weighted = fixedSplit ? candidates.Where(r => r.Settings.Share > 0).ToList() : candidates;
        if (weighted.Count == 0)
        {
            weighted = candidates;
        }

        int start = _rotation++ & int.MaxValue;
        NetworkRoute best = weighted[start % weighted.Count];
        double bestScore = double.MaxValue;
        for (int i = 0; i < weighted.Count; i++)
        {
            var route = weighted[(start + i) % weighted.Count];
            double weight = fixedSplit ? Math.Max(1, route.Settings.Share) : 1;
            double score = (State(route).Active + 1) / weight;
            if (score < bestScore)
            {
                best = route;
                bestScore = score;
            }
        }

        return best;
    }

    private void PruneSticky(long now)
    {
        if (_sticky.Count < 4096)
        {
            return;
        }

        foreach (var key in _sticky.Where(kv => now - kv.Value.LastUse >= StickyFor.TotalMilliseconds).Select(kv => kv.Key).ToList())
        {
            _sticky.Remove(key);
        }
    }

    private RouteState State(NetworkRoute route)
    {
        if (!_states.TryGetValue(route.Id, out var state))
        {
            state = new RouteState();
            _states[route.Id] = state;
        }

        return state;
    }

    private sealed class RouteState
    {
        public readonly SpeedMeter Meter = new();
        public int Active;
        public int Failures;
        public long CooldownUntil;
        public long Bytes;

        public bool InCooldown => Environment.TickCount64 < CooldownUntil;
    }
}
