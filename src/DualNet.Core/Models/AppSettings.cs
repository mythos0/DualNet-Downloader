namespace DualNet.Core.Models;

/// <summary>Persisted application settings.</summary>
public sealed class AppSettings
{
    /// <summary>0 = follow system, 1 = dark, 2 = light.</summary>
    public int ThemeMode { get; set; } = 0;

    public string DownloadDirectory { get; set; } = "";

    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Retry budget per segment.</summary>
    public int RetryCount { get; set; } = 5;

    /// <summary>Reserved for per-download segment override (0 = auto by size).</summary>
    public int MaxSegmentsOverride { get; set; } = 0;

    /// <summary>Global speed cap in KB/s applied to the combined traffic of all adapters (0 = unlimited).</summary>
    public long SpeedLimitKbps { get; set; } = 0;

    /// <summary>Watch the clipboard for copied download links and surface a one-click banner.</summary>
    public bool ClipboardMonitor { get; set; }

    /// <summary>Shut the computer down when every download has reached a terminal state.</summary>
    public bool ShutdownWhenComplete { get; set; }
}
