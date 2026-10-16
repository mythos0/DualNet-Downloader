using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

/// <summary>UI adapter for one <see cref="DownloadJob"/> — refreshed on a shared timer.</summary>
public sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    public DownloadJob Job { get; }

    public DownloadItemViewModel(DownloadJob job)
    {
        Job = job;
        FileName = Path.GetFileName(job.FilePath);
        if (FileName.Length == 0) FileName = "download";
    }

    public string Id => Job.Id;
    public string Url => Job.Url;
    public string FileName { get; }
    public string FilePath => Job.FilePath;
    public string ScopeLabel => Job.Scope.Label;
    public string NetworkText
    {
        get
        {
            var per = Job.PerAdapterSpeedBps;
            if (per.Count == 0) return ScopeLabel;
            var parts = per.Select(p => $"{Formatter.SpeedText(p)}");
            return $"{ScopeLabel} · {string.Join(" + ", parts)}";
        }
    }

    public DownloadStatus Status => Job.Status;
    public long Received => Job.ReceivedBytes;
    public long Total => Job.TotalBytes;
    public double Speed => Job.SpeedBps;
    public int SegmentCount => Job.SegmentCount;

    public string StatusText => Job.Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Preparing => "Connecting…",
        DownloadStatus.Downloading => $"{Formatter.SpeedText(Job.SpeedBps)} · {Job.SegmentCount} connections",
        DownloadStatus.Pausing => "Pausing…",
        DownloadStatus.Paused => string.IsNullOrWhiteSpace(Job.StatusMessage) ? "Paused" : $"Paused · {Job.StatusMessage}",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => $"Failed · {Job.StatusMessage}",
        DownloadStatus.Canceled => "Canceled",
        _ => Job.Status.ToString(),
    };

    public double Percent
    {
        get
        {
            if (Job.TotalBytes <= 0) return Job.Status == DownloadStatus.Completed ? 100 : 0;
            return Math.Min(100, Job.ReceivedBytes * 100.0 / Job.TotalBytes);
        }
    }

    public string SizeText => Job.TotalBytes > 0
        ? $"{Formatter.BytesText(Job.ReceivedBytes)} / {Formatter.BytesText(Job.TotalBytes)}"
        : Formatter.BytesText(Job.ReceivedBytes);

    public string EtaText
    {
        get
        {
            if (Job.Status != DownloadStatus.Downloading || Job.TotalBytes <= 0 || Job.SpeedBps <= 0)
                return "";
            var remaining = Job.TotalBytes - Job.ReceivedBytes;
            if (remaining <= 0) return "";
            var eta = TimeSpan.FromSeconds(remaining / Job.SpeedBps);
            return eta.TotalHours >= 1 ? $"{eta.TotalHours:0.#} h left" :
                   eta.TotalMinutes >= 1 ? $"{eta.TotalMinutes:0.#} min left" :
                   $"{eta.TotalSeconds:0} s left";
        }
    }

    public bool IsActive => Job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Pausing;
    public bool IsPaused => Job.Status == DownloadStatus.Paused;
    public bool CanPause => Job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing;
    public bool CanResume => Job.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Queued;
    public bool IsTerminal => Job.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    /// <summary>Called by the shared UI timer to pull fresh values out of the engine job.</summary>
    public void Refresh()
    {
        Changed(nameof(Status));
        Changed(nameof(StatusText));
        Changed(nameof(Received));
        Changed(nameof(Total));
        Changed(nameof(Speed));
        Changed(nameof(Percent));
        Changed(nameof(SizeText));
        Changed(nameof(EtaText));
        Changed(nameof(NetworkText));
        Changed(nameof(SegmentCount));
        Changed(nameof(CanPause));
        Changed(nameof(CanResume));
        Changed(nameof(IsActive));
        Changed(nameof(IsPaused));
        Changed(nameof(IsTerminal));
        Changed(nameof(FilePath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Formatter
{
    public static string BytesText(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} {units[u]}" : $"{bytes:0.##} {units[u]}";
    }

    public static string SpeedText(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : $"{BytesText(bytesPerSecond)}/s";
}
