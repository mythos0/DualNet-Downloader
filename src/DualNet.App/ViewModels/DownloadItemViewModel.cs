using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

/// <summary>UI wrapper around a DownloadJob with pause/resume/cancel/copy/restart/hash actions.</summary>
public partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadJob _job;

    public DownloadItemViewModel(DownloadJob job)
    {
        _job = job;
        _job.ProgressChanged += OnJobProgress;
        _job.StatusChanged += OnJobStatus;
        _job.DiagnosticEvent += OnJobDiagnostic;
        Refresh();
    }

    private void OnJobDiagnostic(DownloadJob job, string kind, string message) =>
        Dispatcher.UIThread.Post(() => Diagnostic?.Invoke(kind, message));

    /// <summary>The item wants a fresh re-download of its URL (keeps tab/scope context).</summary>
    public event Action<DownloadItemViewModel>? RestartRequested;

    /// <summary>The item wants to disappear from the list (terminal state only).</summary>
    public event Action<DownloadItemViewModel>? RemoveRequested;

    public DownloadJob Job => _job;
    public string Url => _job.Url;

    [ObservableProperty] private string fileName = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private double progressPercent;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string speedText = "";
    [ObservableProperty] private string statusGlyph = "↓";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canResume;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private bool isCompleted;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private double speedBps;
    [ObservableProperty] private bool canRestart;
    [ObservableProperty] private bool canRemove;
    [ObservableProperty] private bool canVerifyHash;
    [ObservableProperty] private string hashText = "";
    [ObservableProperty] private bool isHashing;
    [ObservableProperty] private string categoryGlyph = "⬇";
    [ObservableProperty] private string categoryLabel = "";
    [ObservableProperty] private string etaText = "";
    [ObservableProperty] private bool canMoveInQueue;

    /// <summary>Engine diagnostics (range fallback, resume reset…) for telemetry/toasts.</summary>
    public event Action<string, string>? Diagnostic;

    public bool HasHashText => !string.IsNullOrEmpty(HashText) && !IsHashing;

    partial void OnHashTextChanged(string value) => OnPropertyChanged(nameof(HasHashText));
    partial void OnIsHashingChanged(bool value) => OnPropertyChanged(nameof(HasHashText));

    private void OnJobProgress(DownloadJob job, long received, long total, double speed) =>
        Dispatcher.UIThread.Post(Refresh);

    private void OnJobStatus(DownloadJob job) =>
        Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        FileName = Path.GetFileName(_job.FilePath);
        ProgressPercent = _job.TotalBytes > 0 ? Math.Min(100, 100.0 * _job.ReceivedBytes / _job.TotalBytes) : 0;
        IsIndeterminate = _job.Status is DownloadStatus.Preparing or DownloadStatus.Downloading && _job.TotalBytes <= 0;
        ProgressText = _job.TotalBytes > 0
            ? $"{Formatter.Bytes(_job.ReceivedBytes)} of {Formatter.Bytes(_job.TotalBytes)}  ·  {ProgressPercent:0.0}%"
            : $"{Formatter.Bytes(_job.ReceivedBytes)} downloaded";
        SpeedBps = _job.SpeedBps;
        SpeedText = _job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing
            ? Formatter.Speed(_job.SpeedBps)
            : "";
        HasError = _job.Status == DownloadStatus.Failed && !string.IsNullOrEmpty(_job.StatusMessage);
        ErrorMessage = _job.StatusMessage;
        IsCompleted = _job.Status == DownloadStatus.Completed;
        IsActive = _job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing;

        var category = DownloadCategory.Of(FileName);
        CategoryGlyph = DownloadCategory.Glyph(category);
        CategoryLabel = DownloadCategory.Label(category);
        EtaText = ComputeEta();
        CanMoveInQueue = _job.Status == DownloadStatus.Queued;

        CanPause = _job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Queued;
        CanResume = _job.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued;
        CanCancel = _job.Status is not (DownloadStatus.Completed or DownloadStatus.Canceled);
        CanRestart = _job.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;
        CanRemove = CanRestart;
        CanVerifyHash = _job.Status == DownloadStatus.Completed;

        StatusText = _job.Status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Preparing => "Connecting…",
            DownloadStatus.Downloading => _job.SegmentBadge(),
            DownloadStatus.Pausing => "Pausing…",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Canceled => "Canceled",
            _ => _job.Status.ToString()
        };
        StatusGlyph = _job.Status switch
        {
            DownloadStatus.Queued => "…",
            DownloadStatus.Preparing => "↻",
            DownloadStatus.Downloading => "↓",
            DownloadStatus.Pausing => "⏸",
            DownloadStatus.Paused => "⏸",
            DownloadStatus.Completed => "✓",
            DownloadStatus.Failed => "!",
            DownloadStatus.Canceled => "✕",
            _ => "↓"
        };
    }

    private string ComputeEta()
    {
        if (_job.Status != DownloadStatus.Downloading || _job.TotalBytes <= 0 || SpeedBps <= 0)
            return "";
        var seconds = (_job.TotalBytes - _job.ReceivedBytes) / SpeedBps;
        if (seconds < 0) return "";
        if (seconds < 60) return $"≈ {(int)seconds}s left";
        if (seconds < 3600) return $"≈ {(int)(seconds / 60)} min left";
        return $"≈ {(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min left";
    }

    [RelayCommand]
    private void Pause() => _job.Pause();

    [RelayCommand]
    private void Resume() => _job.Start();

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void MoveUp() => AppServices.Manager.MoveInQueue(_job, -1);

    [RelayCommand]
    private void MoveDown() => AppServices.Manager.MoveInQueue(_job, +1);

    [RelayCommand]
    private async Task CopyUrl()
    {
        try
        {
            var clip = AppServices.MainWindow?.Clipboard;
            if (clip is not null)
                await clip.SetTextAsync(_job.Url);
        }
        catch { }
    }

    /// <summary>Computes the file's SHA-256 and copies it to the clipboard (integrity check).</summary>
    [RelayCommand]
    private async Task VerifyHash()
    {
        if (IsHashing || !File.Exists(_job.FilePath)) return;
        IsHashing = true;
        HashText = "";
        try
        {
            await Task.Run(async () =>
            {
                await using var fs = File.OpenRead(_job.FilePath);
                var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs);
                HashText = Convert.ToHexString(hash).ToLowerInvariant();
            });

            var clip = AppServices.MainWindow?.Clipboard;
            if (clip is not null)
                await clip.SetTextAsync(HashText);
        }
        catch
        {
            HashText = "";
        }
        finally
        {
            IsHashing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _job.Cancel();
        _job.CleanupPartial();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_job.FilePath) ?? "";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{_job.FilePath}\"");
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            if (!File.Exists(_job.FilePath)) return;
            Process.Start(new ProcessStartInfo(_job.FilePath) { UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>Small status helper appended next to "Downloading".</summary>
internal static class DownloadJobBadgeExtensions
{
    public static string SegmentBadge(this DownloadJob job)
    {
        var segs = job.SegmentCount;
        var adapters = Math.Max(1, job.BindAddresses.Count);
        return segs > 1 ? $"Downloading · {segs} segments on {adapters} network{(adapters > 1 ? "s" : "")}" : "Downloading";
    }
}
