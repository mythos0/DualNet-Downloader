using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DualNet.Core.Models;
using DualNet.Core.Net;

namespace DualNet.Core.Engine;

/// <summary>
/// A single file download. Splits the file into byte-range segments, interleaves them across
/// the bound network adapters (Ethernet + Wi-Fi simultaneously), streams each segment directly
/// into its final offset in a pre-allocated file, persists progress for pause/resume, retries
/// failed segments with exponential backoff, and falls back to a single stream when the server
/// does not support ranges.
/// </summary>
public sealed class DownloadJob : IDisposable
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly object _sync = new();
    private List<SegmentState> _segments = new();
    private List<HttpClient> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Timer? _timer;
    private long _lastSampleReceived;
    private long _lastSampleTimestamp;
    private volatile bool _pauseRequested;
    private bool _rangeSupported;
    private long _totalSize = -1;
    private string? _etag;
    private string? _lastModifiedUtc;
    private bool _stateLoadedFromDisk;
    private Exception? _lastError;
    private bool _filePathClaimed;
    private bool _disposed;

    public DownloadJob(string url, IReadOnlyList<IPAddress> bindAddresses, DownloadScope scope,
                       string targetDirectory, DownloadOptions? options = null,
                       string? fileNameOverride = null)
    {
        Url = url.Trim();
        BindAddresses = bindAddresses;
        Scope = scope;
        TargetDirectory = targetDirectory;
        Options = options ?? DownloadOptions.Default;
        Status = DownloadStatus.Queued;
        _fileName = fileNameOverride ?? GuessNameFromUrl(Url);
        FilePath = FileNameGuesser.MakeUniquePath(targetDirectory, _fileName);
    }

    // ---- public observable state -------------------------------------------------------------
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Url { get; }
    public DownloadScope Scope { get; }
    public IReadOnlyList<IPAddress> BindAddresses { get; }
    public DownloadOptions Options { get; }
    public string TargetDirectory { get; }
    public string FilePath { get; private set; }
    private string _fileName;
    public DownloadStatus Status { get; private set; }
    public string? StatusMessage { get; private set; }
    public long TotalBytes => _totalSize;
    public int SegmentCount => _segments.Count;
    public long ReceivedBytes { get; private set; }
    public double SpeedBps { get; private set; }
    public bool IsRangeSupported => _rangeSupported;
    public DateTime? StartedUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }

    /// <summary>Wall-clock seconds from first Start() to Completed (0 when unfinished).</summary>
    public double DurationSeconds =>
        StartedUtc is { } s
            ? Math.Max(0.1, ((FinishedUtc ?? DateTime.UtcNow) - s).TotalSeconds)
            : 0;

    /// <summary>Average throughput over the whole job lifetime (bytes/second).</summary>
    public double AverageSpeedBps =>
        DurationSeconds > 0 ? ReceivedBytes / DurationSeconds : 0;

    /// <summary>Completes when the current run (start/resume) finishes. Awaitable from tests.</summary>
    public Task Completion => _runTask ?? Task.CompletedTask;

    public event Action<DownloadJob, long, long, double>? ProgressChanged;   // received, total (-1 unknown), speed bps
    public event Action<DownloadJob>? StatusChanged;

    // ---- lifecycle ---------------------------------------------------------------------------

    public void Start()
    {
        ThrowIfDisposed();
        if (Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Pausing)
            return;

        _pauseRequested = false;
        _lastError = null;
        StatusMessage = null;
        Status = _stateLoadedFromDisk || _segments.Count > 0 ? DownloadStatus.Downloading : DownloadStatus.Preparing;
        StartedUtc ??= DateTime.UtcNow;
        _cts = new CancellationTokenSource();
        _lastSampleReceived = 0;
        _lastSampleTimestamp = Stopwatch.GetTimestamp();
        StartProgressTimer();
        RaiseStatus();
        _runTask = RunAsync(_cts.Token);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        if (Status == DownloadStatus.Queued)
        {
            Status = DownloadStatus.Paused;
            RaiseStatus();
            return;
        }
        if (Status is not (DownloadStatus.Downloading or DownloadStatus.Preparing))
            return;

        Status = DownloadStatus.Pausing;
        _pauseRequested = true;
        RaiseStatus();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        if (Status is DownloadStatus.Completed or DownloadStatus.Canceled)
            return;
        Status = DownloadStatus.Canceled;
        RaiseStatus();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ---- main pipeline -----------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            EnsureClients();

            if (!_stateLoadedFromDisk && _segments.Count == 0)
            {
                // Probe with retries: transient 5xx on the first request must not fail the job.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await ProbeAsync(ct).ConfigureAwait(false);   // size, ranges, filename, plan, preallocate
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                                               && attempt < Options.RetryCount)
                    {
                        await Task.Delay(BackoffDelay(attempt + 1), ct).ConfigureAwait(false);
                    }
                }
            }
            else if (_stateLoadedFromDisk)
            {
                await RevalidateAsync(ct).ConfigureAwait(false); // ETag guard on cross-restart resume
            }

            ct.ThrowIfCancellationRequested();

            if (_rangeSupported && _totalSize > 0)
            {
                await RunSegmentedAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await RunSingleStreamAsync(ct).ConfigureAwait(false);
            }

            FinishComplete();
        }
        catch (OperationCanceledException) when (_pauseRequested)
        {
            PersistState();
            Status = DownloadStatus.Paused;
            StatusMessage = null;
            SpeedBps = 0;
        }
        catch (OperationCanceledException)
        {
            Status = DownloadStatus.Canceled;   // user cancel; manager cleans partial files
            SpeedBps = 0;
        }
        catch (Exception ex)
        {
            PersistState();
            Status = DownloadStatus.Failed;
            StatusMessage = ex.Message;
            SpeedBps = 0;
        }
        finally
        {
            StopProgressTimer();
            RaiseProgress();
            RaiseStatus();
        }
    }

    private void EnsureClients()
    {
        if (_clients.Count == 0)
            _clients = HttpConnectionFactory.CreateAll(BindAddresses).ToList();
    }

    // ---- probe / plan ------------------------------------------------------------------------

    private async Task ProbeAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _clients[0]
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            _rangeSupported = true;
            _totalSize = response.Content.Headers.ContentRange is { HasLength: true } cr
                ? cr.Length!.Value
                : -1;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            _rangeSupported = false;
            _totalSize = response.Content.Headers.ContentLength ?? -1;
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        _etag = response.Headers.ETag?.Tag;
        if (response.Content.Headers.LastModified is { } lm)
            _lastModifiedUtc = lm.ToString("R");

        var name = FileNameGuesser.FromResponse(response, new Uri(Url));

        if (!_filePathClaimed)
        {
            // Atomically claim the final path so two concurrent downloads of the same
            // file name can never collide on one target file.
            var (claimedPath, claimedStream) = FileNameGuesser.ClaimAvailablePath(TargetDirectory, name);
            FilePath = claimedPath;
            _fileName = Path.GetFileName(claimedPath);
            _filePathClaimed = true;

            if (_rangeSupported && _totalSize > 0)
            {
                // Pre-allocate the final file so each segment can write at its own offset.
                using (var fs = claimedStream)
                    fs.SetLength(_totalSize);
            }
            else
            {
                claimedStream.Dispose();   // empty placeholder; single stream fills it
            }
        }
        else if (_rangeSupported && _totalSize > 0)
        {
            // Re-probe after a remote change: recreate/truncate at the already-claimed path.
            using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.SetLength(_totalSize);
        }

        if (_rangeSupported && _totalSize > 0)
        {
            _segments = SegmentPlanner.Plan(_totalSize, true, Options)
                .Select(r => new SegmentState { Start = r.Start, End = r.End, Written = 0 })
                .ToList();
        }
        else
        {
            _segments = new List<SegmentState> { new() { Start = 0, End = -1, Written = 0 } };
        }

        PersistState();
    }

    /// <summary>On cross-restart resume: if the remote file changed (ETag / Last-Modified), restart from scratch.</summary>
    private async Task RevalidateAsync(CancellationToken ct)
    {
        if (TotalProgress() == 0)
            return;
        if (string.IsNullOrEmpty(_etag) && string.IsNullOrEmpty(_lastModifiedUtc))
            return;   // nothing to validate against — best-effort resume

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        if (!string.IsNullOrEmpty(_etag))
            request.Headers.IfRange = new RangeConditionHeaderValue(_etag);
        else if (DateTimeOffset.TryParse(_lastModifiedUtc, out var lm))
            request.Headers.IfRange = new RangeConditionHeaderValue(lm);

        using var response = await _clients[0]
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // If-Range mismatch => server replies 200 with the full new body => changed.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ResetProgress();
            await ProbeAsync(ct).ConfigureAwait(false);
        }
    }

    private void ResetProgress()
    {
        lock (_sync)
        {
            _segments.Clear();
            _rangeSupported = false;
            _totalSize = -1;
            _etag = null;
            _lastModifiedUtc = null;
            _stateLoadedFromDisk = false;
            // Keep _filePathClaimed: the path stays ours; probe will truncate it.
        }
        ReceivedBytes = 0;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    // ---- segmented download ------------------------------------------------------------------

    private async Task RunSegmentedAsync(CancellationToken ct)
    {
        var workers = new List<Task>();
        int adapterCount = Math.Max(1, BindAddresses.Count);

        for (int i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            if (segment.IsComplete) continue;

            int adapterSlot = SegmentPlanner.AssignAdapter(i, adapterCount);
            var client = _clients[Math.Min(adapterSlot, _clients.Count - 1)];
            int index = i;
            workers.Add(Task.Run(() => SegmentWorkerAsync(client, index, segment, ct), ct));
        }

        if (workers.Count > 0)
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        var incomplete = _segments.FirstOrDefault(s => !s.IsComplete);
        if (incomplete is not null)
            throw _lastError ?? new IOException("One or more segments could not be downloaded.");

        if (new FileInfo(FilePath).Length != _totalSize)
            throw new IOException($"File size mismatch after download (expected {_totalSize} bytes).");

        // Merge is implicit: every segment streamed straight into its final offset,
        // so the file on disk is already byte-identical to the original.
        await using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            fs.Flush();
    }

    private async Task SegmentWorkerAsync(HttpClient client, int index, SegmentState segment, CancellationToken ct)
    {
        long segmentLength = segment.End - segment.Start + 1;
        int failedAttempts = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                long done = segment.Written;
                if (done >= segmentLength)
                    return;

                long progressedThisAttempt = 0;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                    request.Headers.Range = new RangeHeaderValue(segment.Start + done, segment.End);

                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(Options.StallTimeoutMs);

                    using var response = await client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                        .ConfigureAwait(false);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException(
                            $"Server ignored the range request (HTTP {(int)response.StatusCode}); cannot segment.");

                    await using var stream = await response.Content
                        .ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);

                    await using var fs = new FileStream(
                        FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write,
                        81920, useAsync: true);
                    fs.Seek(segment.Start + done, SeekOrigin.Begin);

                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Options.BufferSizeBytes);
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer, attemptCts.Token).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token).ConfigureAwait(false);
                            done += read;
                            progressedThisAttempt += read;
                            segment.Written = done;

                            if (_pauseRequested)
                                throw new OperationCanceledException();   // graceful stop

                            attemptCts.CancelAfter(Options.StallTimeoutMs);   // reset stall watchdog
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }

                    if (done < segmentLength)
                        throw new IOException($"Connection closed early ({done}/{segmentLength} bytes).");

                    return;   // segment complete
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || _pauseRequested)
                {
                    throw;    // outer pause/cancel — propagate
                }
                catch (OperationCanceledException)
                {
                    // stall watchdog fired — retriable
                }

                if (_pauseRequested)
                    throw new OperationCanceledException();

                if (progressedThisAttempt > 0)
                    failedAttempts = 0;   // made progress — reset retry budget

                failedAttempts++;
                if (failedAttempts > Options.RetryCount)
                    throw new IOException($"Segment #{index + 1} failed after {Options.RetryCount} retries: {_lastError?.Message ?? "repeated errors"}");

                await Task.Delay(BackoffDelay(failedAttempts), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex;
            throw;
        }
        finally
        {
            PersistState();
        }
    }

    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(1000L << Math.Min(attempt - 1, 4), 15_000));

    // ---- single-stream fallback --------------------------------------------------------------

    private async Task RunSingleStreamAsync(CancellationToken ct)
    {
        var segment = _segments[0];   // End == -1 (open-ended) or unsupported ranges
        int failedAttempts = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long done = segment.Written;
            long startOffset = done;   // Range header start for this attempt
            bool canResume = done > 0 && _rangeSupported;   // no ranges => restart from zero

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                if (canResume)
                    request.Headers.Range = new RangeHeaderValue(done, null);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(Options.StallTimeoutMs);

                using var response = await _clients[0]
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Guard against servers/proxies that report success but deliver nothing:
                // when Content-Length is known, the body must match it. Note the declared
                // length is relative to the requested range, not the whole file.
                long? declaredLength = response.Content.Headers.ContentLength;

                await using var stream = await response.Content
                    .ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);

                await using var fs = new FileStream(
                    FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write,
                    81920, useAsync: true);
                fs.Seek(done, SeekOrigin.Begin);

                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Options.BufferSizeBytes);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, attemptCts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token).ConfigureAwait(false);
                        done += read;
                        segment.Written = done;

                        if (_pauseRequested)
                            throw new OperationCanceledException();

                        attemptCts.CancelAfter(Options.StallTimeoutMs);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                if (declaredLength is { } expected && done - startOffset != expected)
                    throw new IOException($"Response truncated (got {done - startOffset} of {expected} bytes).");

                if (_totalSize > 0 && done < _totalSize)
                    throw new IOException($"Connection closed early ({done}/{_totalSize} bytes).");

                return;   // complete
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _pauseRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // stall watchdog — retriable
            }

            failedAttempts++;
            if (failedAttempts > Options.RetryCount)
                throw new IOException($"Download failed after {Options.RetryCount} retries.");

            await Task.Delay(BackoffDelay(failedAttempts), ct).ConfigureAwait(false);
        }
    }

    // ---- completion / state ------------------------------------------------------------------

    private void FinishComplete()
    {
        if (_totalSize > 0)
            ReceivedBytes = _totalSize;
        Status = DownloadStatus.Completed;
        StatusMessage = null;
        FinishedUtc = DateTime.UtcNow;
        DeleteStateFile();
    }

    public long TotalProgress()
    {
        lock (_sync)
        {
            if (_segments.Count == 0)
                return 0;
            return _segments.Sum(s => s.End < 0 ? s.Written : Math.Min(s.Written, Math.Max(0, s.End - s.Start + 1)));
        }
    }

    public void CleanupPartial()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        DeleteStateFile();
    }

    private string StateFilePath => FilePath + ".dualnet";

    private void PersistState()
    {
        try
        {
            var state = new PersistedDownloadState
            {
                Id = Id,
                Url = Url,
                FilePath = FilePath,
                TotalSize = _totalSize,
                RangeSupported = _rangeSupported,
                ETag = _etag,
                LastModifiedUtc = _lastModifiedUtc,
                ScopeLabel = Scope.Label,
                AdapterIds = Scope.AdapterIds.ToList(),
                CreatedUtc = StartedUtc ?? DateTime.UtcNow,
                Segments = _segments.ToList()
            };
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, StateJsonOptions));
        }
        catch { /* state persistence is best-effort */ }
    }

    private void DeleteStateFile()
    {
        try { if (File.Exists(StateFilePath)) File.Delete(StateFilePath); } catch { }
    }

    /// <summary>Restores a paused job from its persisted state file (app-restart recovery).</summary>
    public static DownloadJob? TryRestore(string stateFilePath, DownloadOptions options,
                                          Func<string, List<string>, IReadOnlyList<IPAddress>?> resolveBinds)
    {
        try
        {
            var state = JsonSerializer.Deserialize<PersistedDownloadState>(
                File.ReadAllText(stateFilePath), StateJsonOptions);
            if (state is null ||
                string.IsNullOrWhiteSpace(state.Url) ||
                !Uri.IsWellFormedUriString(state.Url, UriKind.Absolute) ||
                !File.Exists(state.FilePath))
                return null;

            var ips = resolveBinds(state.ScopeLabel, state.AdapterIds);
            if (ips is null || ips.Count == 0)
                return null;

            var job = new DownloadJob(state.Url, ips, new DownloadScope(state.ScopeLabel, state.AdapterIds),
                Path.GetDirectoryName(state.FilePath)!, options, Path.GetFileName(state.FilePath))
            {
                Status = DownloadStatus.Paused
            };
            // Restore the exact original path — bypass MakeUniquePath's collision suffix logic.
            job.FilePath = state.FilePath;
            job._fileName = Path.GetFileName(state.FilePath);
            job._filePathClaimed = true;
            job.LoadFrom(state);
            return job;
        }
        catch
        {
            return null;
        }
    }

    private void LoadFrom(PersistedDownloadState state)
    {
        _totalSize = state.TotalSize;
        _rangeSupported = state.RangeSupported;
        _etag = state.ETag;
        _lastModifiedUtc = state.LastModifiedUtc;
        _segments = state.Segments.Count > 0
            ? state.Segments
            : SegmentPlanner.Plan(state.TotalSize, state.RangeSupported, Options)
                .Select(r => new SegmentState { Start = r.Start, End = r.End }).ToList();
        _stateLoadedFromDisk = true;
        ReceivedBytes = TotalProgress();
    }

    // ---- progress sampling -------------------------------------------------------------------

    private void StartProgressTimer()
    {
        _timer = new Timer(_ => { try { RaiseProgress(); } catch { } }, null,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));
    }

    private void StopProgressTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void RaiseProgress()
    {
        long received = TotalProgress();
        var now = Stopwatch.GetTimestamp();
        double dt = Math.Max(0.05, (now - _lastSampleTimestamp) / (double)Stopwatch.Frequency);
        long delta = Math.Max(0, received - _lastSampleReceived);
        double speed = delta / dt;

        _lastSampleReceived = received;
        _lastSampleTimestamp = now;
        ReceivedBytes = received;
        SpeedBps = Status is DownloadStatus.Downloading or DownloadStatus.Preparing ? speed : 0;

        ProgressChanged?.Invoke(this, ReceivedBytes, TotalBytes, SpeedBps);
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this);

    private static string GuessNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var name = FileNameGuesser.Sanitize(last is null ? null : Uri.UnescapeDataString(last));
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
        catch
        {
            return "download";
        }
    }

    public override string ToString() => $"{Path.GetFileName(FilePath)} [{Status}]";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProgressTimer();
        try { _cts?.Cancel(); } catch { }
        foreach (var c in _clients)
            try { c.Dispose(); } catch { }
        _clients.Clear();
        try { _cts?.Dispose(); } catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
