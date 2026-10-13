using System.Collections.Concurrent;
using System.Net;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.Core.Engine;

/// <summary>
/// Owns all downloads, enforces the max-concurrent-downloads queue, and recovers
/// unfinished downloads after an app restart.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentQueue<DownloadJob> _queued = new();
    private readonly object _startLock = new();
    private int _active;
    private bool _disposed;

    public DownloadManager(Func<AppSettings> settings)
    {
        Settings = settings;
    }

    private Func<AppSettings> Settings { get; }

    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.ToList();

    public event Action<DownloadJob>? JobEnded;          // completed / failed / canceled / paused
    public event Action<DownloadJob>? JobAdded;

    public DownloadJob AddDownload(string url, IReadOnlyList<IPAddress> bindAddresses, DownloadScope scope,
                                   string targetDirectory, DownloadOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var job = new DownloadJob(url, bindAddresses, scope, targetDirectory,
            options ?? OptionsFromSettings());
        _jobs[job.Id] = job;
        job.StatusChanged += OnJobStatusChanged;
        JobAdded?.Invoke(job);
        TryStartOrQueue(job);
        return job;
    }

    public DownloadOptions OptionsFromSettings() => new()
    {
        SegmentsSmall = 8,
        SegmentsLarge = 16,
        RetryCount = Math.Clamp(Settings().RetryCount, 1, 20),
    };

    private void TryStartOrQueue(DownloadJob job)
    {
        lock (_startLock)
        {
            if (_active < Math.Max(1, Settings().MaxConcurrentDownloads))
            {
                _active++;
                job.Start();
                return;
            }
        }
        _queued.Enqueue(job);
        if (job.Status == DownloadStatus.Queued)
            return; // stays queued; shown in UI as "Queued"
    }

    private void OnJobStatusChanged(DownloadJob job)
    {
        if (job.Status is DownloadStatus.Completed or DownloadStatus.Failed
                       or DownloadStatus.Canceled or DownloadStatus.Paused)
        {
            JobEnded?.Invoke(job);
            bool slotFreed = false;
            lock (_startLock)
            {
                if (_active > 0) { _active--; slotFreed = true; }
            }
            if (slotFreed)
                AdvanceQueue();
        }
    }

    private void AdvanceQueue()
    {
        while (true)
        {
            DownloadJob? next = null;
            lock (_startLock)
            {
                if (_active >= Math.Max(1, Settings().MaxConcurrentDownloads))
                    return;
                while (_queued.TryDequeue(out var candidate))
                {
                    if (candidate.Status == DownloadStatus.Queued) { next = candidate; break; }
                }
                if (next is null) return;
                _active++;
            }
            next.Start();
        }
    }

    /// <summary>Re-queues paused/queued jobs on app start or setting change.</summary>
    public void ResumeQueued()
    {
        foreach (var job in _jobs.Values.Where(j => j.Status == DownloadStatus.Queued).ToList())
            TryStartOrQueue(job);
    }

    public void PauseAll()
    {
        foreach (var job in _jobs.Values.ToList())
        {
            if (job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Queued)
                job.Pause();
        }
    }

    /// <summary>Scans a directory for *.dualnet state files and restores paused jobs.</summary>
    public List<DownloadJob> RecoverFromDirectory(
        string directory,
        Func<IReadOnlyList<IPAddress>> availableAdapters,
        Func<string, List<string>, IReadOnlyList<IPAddress>?> resolveBinds)
    {
        var restored = new List<DownloadJob>();
        try
        {
            if (!Directory.Exists(directory)) return restored;

            foreach (var stateFile in Directory.EnumerateFiles(directory, "*.dualnet"))
            {
                var job = DownloadJob.TryRestore(stateFile, OptionsFromSettings(), resolveBinds);
                if (job is null)
                    continue;

                _jobs[job.Id] = job;
                job.StatusChanged += OnJobStatusChanged;
                restored.Add(job);
            }
        }
        catch { }
        return restored;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PauseAll();
        foreach (var job in _jobs.Values)
            job.Dispose();
    }
}
