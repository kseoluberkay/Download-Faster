using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadFaster.Core;

/// <summary>
/// Downloads one file over several adapters at once. The file is cut into chunks that
/// connections on every enabled adapter pull from a shared queue; near the end, idle
/// connections split the biggest unfinished chunk so a slow adapter never holds up the
/// finish. Adapter settings and <see cref="DownloadOptions.Mode"/> apply while running.
/// </summary>
public sealed class SegmentedDownload
{
    public const string PartSuffix = ".dfpart";
    public const string StateSuffix = ".dfpart.json";

    private readonly Lock _lock = new();
    private readonly SpeedMeter _meter = new();
    private readonly Dictionary<string, long> _bytesFromEarlierRuns = [];

    private List<RouteRuntime> _runtimes = [];
    private DownloadState _state = DownloadState.Queued;
    private string? _message;
    private long _downloaded;
    private long? _total;
    private bool _isSplit;
    private int _running;

    // Per-run state, guarded by _lock.
    private readonly LinkedList<Segment> _pending = new();
    private readonly List<Segment> _active = [];
    private SafeFileHandle? _handle;
    private Exception? _fatal;
    private int _consecutiveErrors;
    private long _chunkSize;
    private long _splitEpoch;
    private string? _splitSignature;
    private string? _etag;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SegmentedDownload(Uri url, string outputDirectory, IReadOnlyList<NetworkRoute> routes, DownloadOptions options, string? fileName = null)
    {
        Url = url;
        OutputDirectory = outputDirectory;
        Routes = routes;
        Options = options;
        FileName = fileName;

        // Show how far a previously paused download got before it is resumed.
        if (fileName is not null
            && ResumeState.TryLoad(StatePath) is { } saved
            && saved.Url == url.AbsoluteUri
            && saved.ValidRanges(saved.TotalBytes) is { } remaining)
        {
            _total = saved.TotalBytes;
            _downloaded = saved.TotalBytes - remaining.Sum(r => r.End - r.Start);
            _isSplit = true;
        }
    }

    public Uri Url { get; }

    public string OutputDirectory { get; }

    /// <summary>Known once the server has been contacted (or when resuming).</summary>
    public string? FileName { get; private set; }

    public string? FilePath => FileName is null ? null : Path.Combine(OutputDirectory, FileName);

    /// <summary>Adapters to use. Takes effect the next time the download starts or resumes.</summary>
    public IReadOnlyList<NetworkRoute> Routes { get; set; }

    public DownloadOptions Options { get; }

    public DownloadState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    private string PartPath => FilePath! + PartSuffix;

    private string StatePath => FilePath! + StateSuffix;

    /// <summary>
    /// Runs until the file is complete, the download fails, or <paramref name="cancellationToken"/>
    /// fires (which pauses it and keeps the partial file for resuming). Never throws.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The download is already running.");
        }

        using var samplerCts = new CancellationTokenSource();
        Task sampler = Task.CompletedTask;
        try
        {
            SetState(DownloadState.Connecting, null);
            _meter.Reset();
            lock (_lock)
            {
                _runtimes = Routes.Select(r => new RouteRuntime(r, BoundHttpClient.Create(r, Options.ConnectTimeout))).ToList();
                _fatal = null;
                _consecutiveErrors = 0;
            }

            sampler = SampleSpeedsAsync(samplerCts.Token);

            var info = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            ChooseFileName(info);

            if (info.TotalBytes is long total && info.AcceptsRanges)
            {
                await RunSplitAsync(info, total, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunSingleStreamAsync(info, cancellationToken).ConfigureAwait(false);
            }

            SetState(DownloadState.Completed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(DownloadState.Paused, null);
        }
        catch (Exception ex)
        {
            SetState(DownloadState.Failed, Describe(ex));
        }
        finally
        {
            samplerCts.Cancel();
            await sampler.ConfigureAwait(false);
            lock (_lock)
            {
                foreach (var rt in _runtimes)
                {
                    _bytesFromEarlierRuns[rt.Route.Id] = _bytesFromEarlierRuns.GetValueOrDefault(rt.Route.Id) + rt.Bytes;
                    rt.Client.Dispose();
                }

                _runtimes = [];
            }

            _meter.Reset();
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Deletes the partial file and its resume data. Only call while not running.</summary>
    public void DeletePartialFiles()
    {
        if (IsRunning || FileName is null)
        {
            return;
        }

        TryDelete(PartPath);
        TryDelete(StatePath);
    }

    public DownloadSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            bool live = _state is DownloadState.Downloading or DownloadState.Connecting;
            var routes = new List<RouteSnapshot>();
            foreach (var route in Routes)
            {
                var rt = _runtimes.FirstOrDefault(r => r.Route.Id == route.Id);
                long bytes = _bytesFromEarlierRuns.GetValueOrDefault(route.Id) + (rt?.Bytes ?? 0);
                string? problem = rt is { ConsecutiveFailures: > 0 } ? rt.LastError : null;
                routes.Add(new RouteSnapshot(
                    route.Id,
                    route.Name,
                    route.Kind,
                    bytes,
                    live && rt is not null ? rt.Meter.BytesPerSecond : 0,
                    rt?.ActiveConnections ?? 0,
                    problem));
            }

            return new DownloadSnapshot(
                _state,
                FileName,
                _total,
                _downloaded,
                live ? _meter.BytesPerSecond : 0,
                _isSplit,
                _message,
                routes);
        }
    }

    // ---------------------------------------------------------------- probing

    private async Task<RemoteFileInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        const int Rounds = 3;
        Exception? lastError = null;
        for (int round = 0; round < Rounds; round++)
        {
            foreach (var rt in UsableRuntimesInOrder())
            {
                try
                {
                    return await ProbeWithAsync(rt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsNetworkError(ex))
                {
                    lock (_lock)
                    {
                        rt.ConsecutiveFailures++;
                        rt.LastError = Describe(ex);
                    }

                    lastError = ex;
                }
            }

            // A missing file or denied access will not fix itself; dropped connections might.
            if (lastError is null or HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone
                or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest })
            {
                break;
            }

            if (round < Rounds - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(round + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
        {
            ExceptionDispatchInfo.Throw(lastError);
        }

        throw new InvalidOperationException("No network adapter is turned on. Tick at least one adapter.");
    }

    private async Task<RemoteFileInfo> ProbeWithAsync(RouteRuntime rt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.ConnectTimeout + Options.ReadTimeout);

        // Asking for the first byte tells us the size and whether the server can send parts.
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await rt.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var finalUrl = response.RequestMessage?.RequestUri ?? Url;
        var contentRange = response.Content.Headers.ContentRange;
        string? name = FileNames.FromContentDisposition(response.Content.Headers.ContentDisposition)
            ?? FileNames.FromUrl(finalUrl)
            ?? FileNames.FromUrl(Url);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && contentRange is { Length: 0 })
        {
            return new RemoteFileInfo(finalUrl, 0, true, name, response.Headers.ETag?.Tag, rt);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        bool acceptsRanges = response.StatusCode == HttpStatusCode.PartialContent
            && contentRange is { HasLength: true, From: 0 };
        long? total = acceptsRanges ? contentRange!.Length : response.Content.Headers.ContentLength;
        rt.ResolvedUrl = finalUrl;
        return new RemoteFileInfo(finalUrl, total, acceptsRanges, name, response.Headers.ETag?.Tag, rt);
    }

    private void ChooseFileName(RemoteFileInfo info)
    {
        Directory.CreateDirectory(OutputDirectory);
        if (FileName is not null)
        {
            return;
        }

        string name = info.SuggestedName ?? "download";

        // Pick up an unfinished download of the same link instead of starting over.
        string candidate = Path.Combine(OutputDirectory, name);
        if (ResumeState.TryLoad(candidate + StateSuffix) is { } previous && previous.Url == Url.AbsoluteUri)
        {
            FileName = name;
            return;
        }

        FileName = FileNames.MakeUnique(name, n =>
        {
            string path = Path.Combine(OutputDirectory, n);
            return File.Exists(path) || File.Exists(path + PartSuffix) || Directory.Exists(path);
        });
    }

    // ---------------------------------------------------------------- split download

    private async Task RunSplitAsync(RemoteFileInfo info, long total, CancellationToken cancellationToken)
    {
        List<(long Start, long End)>? ranges = LoadResumeRanges(info, total);
        bool resuming = ranges is not null;
        ranges ??= total > 0 ? [(0, total)] : [];

        var handle = File.OpenHandle(
            PartPath, resuming ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        try
        {
            if (!resuming)
            {
                RandomAccess.SetLength(handle, total);
            }

            lock (_lock)
            {
                _handle = handle;
                _etag = info.ETag;
                _chunkSize = Math.Max(64 * 1024, Options.ChunkSizeBytes);
                _total = total;
                _isSplit = true;
                _pending.Clear();
                _active.Clear();
                foreach (var (start, end) in ranges)
                {
                    for (long p = start; p < end; p += _chunkSize)
                    {
                        _pending.AddLast(new Segment(p, Math.Min(end, p + _chunkSize)));
                    }
                }

                _downloaded = total - ranges.Sum(r => r.End - r.Start);
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            SaveState();
            SetState(DownloadState.Downloading, null);

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await SuperviseAsync(runCts.Token).ConfigureAwait(false);
            }
            finally
            {
                runCts.Cancel();
                await Task.WhenAll(_runtimes.SelectMany(rt => rt.Workers).Select(w => w.Task)).ConfigureAwait(false);
                foreach (var rt in _runtimes)
                {
                    rt.Workers.ForEach(w => w.Cts.Dispose());
                    rt.Workers.Clear();
                }

                if (!IsComplete())
                {
                    SaveState();
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _handle = null;
            }

            handle.Dispose();
        }

        TryDelete(StatePath);
        MovePartToFinal();
    }

    private List<(long Start, long End)>? LoadResumeRanges(RemoteFileInfo info, long total)
    {
        var state = ResumeState.TryLoad(StatePath);
        if (state is null
            || state.Url != Url.AbsoluteUri
            || state.TotalBytes != total
            || (state.ETag is not null && info.ETag is not null && state.ETag != info.ETag)
            || !File.Exists(PartPath)
            || new FileInfo(PartPath).Length != total)
        {
            return null;
        }

        return state.ValidRanges(total);
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        long lastSave = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wake;
            lock (_lock)
            {
                if (_fatal is not null)
                {
                    ExceptionDispatchInfo.Throw(_fatal);
                }

                if (IsCompleteLocked())
                {
                    return;
                }

                wake = _wake.Task;
            }

            ReconcileWorkers(cancellationToken);
            UpdateRunMessage();

            if (Environment.TickCount64 - lastSave >= Options.StateSaveInterval.TotalMilliseconds)
            {
                SaveState();
                lastSave = Environment.TickCount64;
            }

            await Task.WhenAny(wake, Task.Delay(200, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Starts or stops connections so each adapter runs as many as its settings ask for.</summary>
    private void ReconcileWorkers(CancellationToken runToken)
    {
        foreach (var rt in _runtimes)
        {
            foreach (var finished in rt.Workers.Where(w => w.Task.IsCompleted).ToList())
            {
                finished.Cts.Dispose();
                rt.Workers.Remove(finished);
            }

            var settings = rt.Route.Settings;
            int desired = settings.Enabled ? settings.Connections : 0;
            var running = rt.Workers.Where(w => !w.StopRequested).ToList();

            for (int i = running.Count; i < desired; i++)
            {
                var worker = new Worker(CancellationTokenSource.CreateLinkedTokenSource(runToken));
                worker.Task = Task.Run(() => WorkerLoopAsync(rt, worker.Cts.Token));
                rt.Workers.Add(worker);
            }

            foreach (var worker in running.Skip(desired))
            {
                worker.StopRequested = true;
                worker.Cts.Cancel();
            }

            rt.WorkerCount = desired;
        }
    }

    private void UpdateRunMessage()
    {
        string? message = null;
        var enabled = _runtimes.Where(rt => rt.Route.Settings.Enabled).ToList();
        if (enabled.Count == 0)
        {
            message = "Waiting: turn on at least one adapter.";
        }
        else if (enabled.All(rt => rt.InCooldown))
        {
            message = "Connection problems, retrying...";
        }

        lock (_lock)
        {
            _message = message;
        }
    }

    private async Task WorkerLoopAsync(RouteRuntime rt, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Options.BufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (rt.InCooldown)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var segment = Acquire(rt);
                if (segment is null)
                {
                    if (IsComplete())
                    {
                        return;
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Interlocked.Increment(ref rt.ActiveConnections);
                try
                {
                    await FetchAsync(rt, segment, buffer, cancellationToken).ConfigureAwait(false);
                    Return(segment);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Return(segment);
                    return;
                }
                catch (Exception ex)
                {
                    Return(segment);
                    if (ex is DiskWriteException or RemoteFileChangedException)
                    {
                        Fail(ex);
                        return;
                    }

                    int failures = RegisterFailure(rt, ex);
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Min(failures, 5)), cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref rt.ActiveConnections);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Paused, finished, or this connection was switched off.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task FetchAsync(RouteRuntime rt, Segment segment, byte[] buffer, CancellationToken cancellationToken)
    {
        long from, to;
        SafeFileHandle handle;
        lock (_lock)
        {
            segment.Reserved = segment.Written;
            from = segment.Written;
            to = segment.End - 1;
            handle = _handle!;
        }

        if (from > to)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.ConnectTimeout + Options.ReadTimeout);

        Uri target = rt.ResolvedUrl ?? Url;
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await rt.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            // A redirect target may have expired; resolve the original link again next time.
            rt.ResolvedUrl = null;
            throw new HttpRequestException(
                response.StatusCode == HttpStatusCode.OK
                    ? "The server stopped accepting split downloads."
                    : $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                null,
                response.StatusCode);
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From != from)
        {
            throw new HttpRequestException("The server sent the wrong part of the file.");
        }

        if (contentRange.Length is long length && length != _total)
        {
            throw new RemoteFileChangedException();
        }

        rt.ResolvedUrl = response.RequestMessage?.RequestUri ?? target;

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                int toRead;
                lock (_lock)
                {
                    toRead = (int)Math.Min(buffer.Length, segment.End - segment.Reserved);
                }

                if (toRead <= 0)
                {
                    return;
                }

                timeout.CancelAfter(Options.ReadTimeout);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The server closed the connection early.");
                }

                long offset;
                lock (_lock)
                {
                    // Another connection may have taken over the tail of this segment meanwhile.
                    read = (int)Math.Min(read, Math.Max(0, segment.End - segment.Reserved));
                    offset = segment.Reserved;
                    segment.Reserved += read;
                }

                if (read == 0)
                {
                    return;
                }

                try
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DiskWriteException(ex);
                }

                lock (_lock)
                {
                    segment.Written += read;
                    _downloaded += read;
                    _consecutiveErrors = 0;
                    rt.ConsecutiveFailures = 0;
                    rt.LastError = null;
                }

                Interlocked.Add(ref rt.Bytes, read);
                await rt.Route.Limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Segment? Acquire(RouteRuntime rt)
    {
        lock (_lock)
        {
            if (_fatal is not null)
            {
                return null;
            }

            if (_pending.First is { } first)
            {
                if (!MayClaimLocked(rt))
                {
                    return null;
                }

                _pending.RemoveFirst();
                var segment = first.Value;
                segment.Owner = rt;
                segment.ClaimEpoch = _splitEpoch;
                _active.Add(segment);
                rt.Claimed += segment.End - segment.Written;
                return segment;
            }

            return StealLocked(rt);
        }
    }

    /// <summary>
    /// Splits the unfinished segment with the most bytes left so an idle connection can help.
    /// The cut point follows the two connections' speeds so both finish at about the same time.
    /// </summary>
    private Segment? StealLocked(RouteRuntime thief)
    {
        bool ownSegmentsOnly = Options.Mode == BalanceMode.FixedSplit;
        Segment? victim = null;
        long victimRemaining = 0;
        foreach (var segment in _active)
        {
            if (ownSegmentsOnly && segment.Owner != thief)
            {
                continue;
            }

            long remaining = segment.End - segment.Reserved;
            if (remaining > victimRemaining)
            {
                victim = segment;
                victimRemaining = remaining;
            }
        }

        // Not worth a new request for the last few kilobytes.
        if (victim?.Owner is null || victimRemaining < Options.BufferSize * 2L)
        {
            return null;
        }

        double thiefRate = thief.PerConnectionRate;
        double victimRate = victim.Owner.PerConnectionRate;
        double thiefShare = thiefRate > 0 && victimRate > 0 ? thiefRate / (thiefRate + victimRate) : 0.5;

        long minSplit = Math.Max(Options.MinSplitBytes, Options.BufferSize * 2L);
        long take = (long)(victimRemaining * thiefShare);
        if (victimRemaining - take < minSplit && thiefShare >= 0.75)
        {
            // The victim is at least 3x slower and would only keep a scrap: take over all of it.
            take = victimRemaining;
        }
        else
        {
            take = Math.Min(take, victimRemaining - minSplit);
            if (take < minSplit)
            {
                return null;
            }
        }

        long splitAt = victim.End - take;
        var stolen = new Segment(splitAt, victim.End) { Owner = thief, ClaimEpoch = _splitEpoch };
        victim.End = splitAt;
        if (victim.ClaimEpoch == _splitEpoch)
        {
            victim.Owner.Claimed -= take;
        }

        thief.Claimed += take;
        _active.Add(stolen);
        return stolen;
    }

    /// <summary>
    /// In fixed-split mode, lets an adapter take more work only while it is within its share.
    /// Shares count from the last change to the split setup, so an adapter that ran alone for a
    /// while is not starved afterwards while the others "catch up".
    /// </summary>
    private bool MayClaimLocked(RouteRuntime rt)
    {
        if (Options.Mode != BalanceMode.FixedSplit)
        {
            _splitSignature = null;
            return true;
        }

        var eligibleRoutes = _runtimes.Where(other =>
        {
            var settings = other.Route.Settings;
            return settings.Enabled && settings.Share > 0 && other.WorkerCount > 0 && !other.InCooldown;
        }).ToList();

        string signature = string.Join("|", eligibleRoutes.Select(r => $"{r.Route.Id}:{r.Route.Settings.Share}"));
        if (signature != _splitSignature)
        {
            _splitSignature = signature;
            _splitEpoch++;
            foreach (var other in _runtimes)
            {
                other.Claimed = 0;
            }
        }

        int eligible = eligibleRoutes.Count;
        bool selfEligible = eligibleRoutes.Contains(rt);
        double totalShare = eligibleRoutes.Sum(r => r.Route.Settings.Share);
        long totalClaimed = eligibleRoutes.Sum(r => r.Claimed);

        if (!selfEligible)
        {
            // Adapters with a 0% share only help out when nothing else can.
            return eligible == 0;
        }

        if (eligible == 1)
        {
            return true;
        }

        double target = rt.Route.Settings.Share / totalShare * totalClaimed;
        return rt.Claimed - target <= _chunkSize;
    }

    /// <summary>Gives back a segment; whatever was not downloaded goes to the front of the queue.</summary>
    private void Return(Segment segment)
    {
        lock (_lock)
        {
            if (!_active.Remove(segment))
            {
                return;
            }

            long remaining = segment.End - segment.Written;
            if (remaining > 0)
            {
                _pending.AddFirst(new Segment(segment.Written, segment.End));
                if (segment.Owner is not null && segment.ClaimEpoch == _splitEpoch)
                {
                    segment.Owner.Claimed -= remaining;
                }
            }

            if (IsCompleteLocked())
            {
                _wake.TrySetResult();
            }
        }
    }

    private int RegisterFailure(RouteRuntime rt, Exception ex)
    {
        lock (_lock)
        {
            rt.ConsecutiveFailures++;
            rt.LastError = Describe(ex);
            _consecutiveErrors++;

            if (rt.ConsecutiveFailures >= 3)
            {
                // 2s, 4s, 8s, 16s, then 30s between attempts on a struggling adapter.
                int seconds = Math.Min(30, 2 << Math.Min(rt.ConsecutiveFailures - 3, 4));
                rt.CooldownUntil = Environment.TickCount64 + (seconds * 1000L);
            }

            if (_consecutiveErrors >= Options.MaxConsecutiveErrors)
            {
                _fatal ??= new IOException($"Giving up after repeated errors. Last error: {rt.LastError}", ex);
                _wake.TrySetResult();
            }

            return rt.ConsecutiveFailures;
        }
    }

    private void Fail(Exception ex)
    {
        lock (_lock)
        {
            _fatal ??= ex;
            _wake.TrySetResult();
        }
    }

    private bool IsComplete()
    {
        lock (_lock)
        {
            return IsCompleteLocked();
        }
    }

    private bool IsCompleteLocked() => _pending.Count == 0 && _active.Count == 0;

    private void SaveState()
    {
        ResumeState state;
        lock (_lock)
        {
            if (_total is not long total)
            {
                return;
            }

            var ranges = _pending.Select(s => (s.Written, s.End))
                .Concat(_active.Select(s => (s.Written, s.End)))
                .Where(r => r.End > r.Written)
                .OrderBy(r => r.Written)
                .ToList();

            var merged = new List<long[]>();
            foreach (var (start, end) in ranges)
            {
                if (merged.Count > 0 && merged[^1][1] == start)
                {
                    merged[^1][1] = end;
                }
                else
                {
                    merged.Add([start, end]);
                }
            }

            state = new ResumeState { Url = Url.AbsoluteUri, TotalBytes = total, ETag = _etag, Remaining = merged };
        }

        try
        {
            state.Save(StatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resume data is a convenience; the download itself can carry on.
        }
    }

    // ---------------------------------------------------------------- single connection

    /// <summary>For servers that cannot send parts of a file: one connection, from the start.</summary>
    private async Task RunSingleStreamAsync(RemoteFileInfo info, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _isSplit = false;
            _total = info.TotalBytes;
            _downloaded = 0;
        }

        SetState(DownloadState.Downloading, "This server does not support split downloads, so one connection is used.");

        var candidates = UsableRuntimesInOrder().ToList();
        if (candidates.Remove(info.ProbedWith))
        {
            candidates.Insert(0, info.ProbedWith);
        }

        Exception? lastError = null;
        for (int attempt = 0; attempt < 3 && candidates.Count > 0; attempt++)
        {
            var rt = candidates[attempt % candidates.Count];
            try
            {
                await StreamWholeFileAsync(rt, cancellationToken).ConfigureAwait(false);
                TryDelete(StatePath);
                MovePartToFinal();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not DiskWriteException)
            {
                lastError = ex;
                RegisterFailure(rt, ex);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        ExceptionDispatchInfo.Throw(lastError ?? new InvalidOperationException("No network adapter is turned on."));
    }

    private async Task StreamWholeFileAsync(RouteRuntime rt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _downloaded = 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.ConnectTimeout + Options.ReadTimeout);
        using var response = await rt.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        long? expected = response.Content.Headers.ContentLength;
        lock (_lock)
        {
            _total = expected ?? _total;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Options.BufferSize);
        Interlocked.Increment(ref rt.ActiveConnections);
        try
        {
            var file = new FileStream(PartPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    while (true)
                    {
                        timeout.CancelAfter(Options.ReadTimeout);
                        int read = await stream.ReadAsync(buffer.AsMemory(0, Options.BufferSize), timeout.Token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new DiskWriteException(ex);
                        }

                        lock (_lock)
                        {
                            _downloaded += read;
                            rt.ConsecutiveFailures = 0;
                            rt.LastError = null;
                        }

                        Interlocked.Add(ref rt.Bytes, read);
                        await rt.Route.Limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref rt.ActiveConnections);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (expected is long length && Interlocked.Read(ref _downloaded) != length)
        {
            throw new IOException("The server closed the connection before the file was complete.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private IEnumerable<RouteRuntime> UsableRuntimesInOrder()
    {
        List<RouteRuntime> runtimes;
        lock (_lock)
        {
            runtimes = _runtimes;
        }

        return runtimes.Where(rt => rt.Route.Settings.Enabled);
    }

    private void MovePartToFinal()
    {
        string part = PartPath;
        FileName = FileNames.MakeUnique(FileName!, n => File.Exists(Path.Combine(OutputDirectory, n)));
        File.Move(part, FilePath!);
    }

    private async Task SampleSpeedsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            long downloaded;
            List<RouteRuntime> runtimes;
            lock (_lock)
            {
                downloaded = _downloaded;
                runtimes = _runtimes;
            }

            _meter.Sample(downloaded);
            foreach (var rt in runtimes)
            {
                rt.Meter.Sample(Interlocked.Read(ref rt.Bytes));
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetState(DownloadState state, string? message)
    {
        lock (_lock)
        {
            _state = state;
            _message = message;
        }
    }

    private static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or OperationCanceledException or TimeoutException;

    internal static string Describe(Exception ex) => ex switch
    {
        DiskWriteException => $"Could not write the file: {ex.InnerException?.Message}",
        RemoteFileChangedException => ex.Message,
        OperationCanceledException or TimeoutException => "Timed out waiting for the server.",
        HttpRequestException { InnerException: SocketException socket } => socket.Message,
        HttpRequestException { InnerException: IOException { InnerException: SocketException socket } } => socket.Message,
        _ => ex.Message,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record RemoteFileInfo(
        Uri FinalUrl, long? TotalBytes, bool AcceptsRanges, string? SuggestedName, string? ETag, RouteRuntime ProbedWith);

    private sealed class Segment(long start, long end)
    {
        /// <summary>Everything before this offset is on disk.</summary>
        public long Written = start;

        /// <summary>Everything before this offset is on disk or being written.</summary>
        public long Reserved = start;

        /// <summary>Exclusive end. Shrinks when another connection takes over the tail.</summary>
        public long End = end;

        public RouteRuntime? Owner;

        /// <summary>Which fixed-split accounting period the owner claimed this segment in.</summary>
        public long ClaimEpoch;
    }

    private sealed class Worker(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Task Task { get; set; } = Task.CompletedTask;

        public bool StopRequested { get; set; }
    }

    private sealed class RouteRuntime(NetworkRoute route, HttpClient client)
    {
        public NetworkRoute Route { get; } = route;

        public HttpClient Client { get; } = client;

        public SpeedMeter Meter { get; } = new();

        /// <summary>Only touched by the supervisor loop.</summary>
        public List<Worker> Workers { get; } = [];

        public volatile int WorkerCount;
        public int ActiveConnections;
        public long Bytes;

        // Guarded by the download's lock. Bytes handed out since the split setup last changed.
        public long Claimed;
        public int ConsecutiveFailures;
        public string? LastError;

        public long CooldownUntil;
        public volatile Uri? ResolvedUrl;

        public bool InCooldown => Environment.TickCount64 < Volatile.Read(ref CooldownUntil);

        public double PerConnectionRate => Meter.BytesPerSecond / Math.Max(1, Volatile.Read(ref ActiveConnections));
    }
}

/// <summary>The disk could not be written (full, removed, no permission). Retrying will not help.</summary>
internal sealed class DiskWriteException(Exception inner) : IOException(inner.Message, inner);

internal sealed class RemoteFileChangedException()
    : IOException("The file changed on the server while downloading. Remove it and start again.");
