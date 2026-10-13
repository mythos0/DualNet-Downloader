using System.Collections.Concurrent;
using System.Net;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.Core.Engine;

/// <summary>
/// Owns all downloads, enforces the max-concurrent-downloads queue, supports reordering the
/// waiting queue (IDM-style priority), and recovers unfinished downloads after an app restart.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly List<DownloadJob> _queued = new();   // guarded by _startLock
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

        // IDM-style categories: optional per-type sub-folder (Video/, Compressed/, …).
        var category = DownloadCategory.Of(FileNameGuesser.FromUrl(url));
        var folder = DownloadCategory.Subfolder(category, Settings().OrganizeByCategory);
        var finalDirectory = folder is null
            ? targetDirectory
            : Path.Combine(targetDirectory, folder);
        Directory.CreateDirectory(finalDirectory);

        var job = new DownloadJob(url, bindAddresses, scope, finalDirectory,
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
            _queued.Add(job);
        }
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
                for (int i = 0; i < _queued.Count; i++)
                {
                    if (_queued[i].Status == DownloadStatus.Queued)
                    {
                        next = _queued[i];
                        _queued.RemoveAt(i);
                        break;
                    }
                }
                if (next is null) return;
                _active++;
            }
            next.Start();
        }
    }

    /// <summary>Snapshot of the waiting queue in start order.</summary>
    public IReadOnlyList<DownloadJob> QueuedJobsSnapshot()
    {
        lock (_startLock)
            return _queued.ToList();
    }

    /// <summary>Moves a queued job up (-1) or down (+1) in the waiting queue.</summary>
    public bool MoveInQueue(DownloadJob job, int delta)
    {
        lock (_startLock)
        {
            int index = _queued.FindIndex(q => q.Id == job.Id && q.Status == DownloadStatus.Queued);
            if (index < 0) return false;
            int target = Math.Clamp(index + delta, 0, _queued.Count - 1);
            if (target == index) return false;
            var item = _queued[index];
            _queued.RemoveAt(index);
            _queued.Insert(target, item);
            return true;
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
