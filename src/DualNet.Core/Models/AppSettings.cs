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
}
