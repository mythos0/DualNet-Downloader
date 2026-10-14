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

    /// <summary>0 = no proxy, 1 = system proxy, 2 = manual proxy.</summary>
    public int ProxyMode { get; set; }

    public string ProxyHost { get; set; } = "";

    public int ProxyPort { get; set; } = 8080;

    /// <summary>Sort finished downloads into per-category sub-folders (Video, Compressed, …).</summary>
    public bool OrganizeByCategory { get; set; }

    /// <summary>Expose the localhost integration API (GET /add?url=…) for apps and scripts.</summary>
    public bool LocalApiEnabled { get; set; } = true;

    public int LocalApiPort { get; set; } = 45454;

    /// <summary>Play a sound when a download finishes (Windows).</summary>
    public bool CompletionSound { get; set; } = true;

    /// <summary>IDM-style scheduler: only download inside the daily [start, stop) window.</summary>
    public bool ScheduleEnabled { get; set; }

    public string ScheduleStart { get; set; } = "09:00";

    public string ScheduleStop { get; set; } = "23:00";

    /// <summary>Optional custom endpoint for the per-adapter speed test (empty = Cloudflare).</summary>
    public string SpeedTestUrl { get; set; } = "";

    /// <summary>
    /// Dynamic segment scaling: keep adding connections while combined throughput improves
    /// and hand work to idle adapters. Off = strict fixed 8/16 segment plan.
    /// </summary>
    public bool AdaptiveScaling { get; set; } = true;

    /// <summary>Search engine used when a non-URL is typed in the address bar (index into SearchEngines.Names).</summary>
    public int SearchEngine { get; set; }

    /// <summary>Sentry DSN (override to point the app at a fresh/rotated DSN without a rebuild).</summary>
    public string SentryDsn { get; set; } = "";

    /// <summary>User-defined network providers for adapters the auto-scan does not offer.</summary>
    public List<ManualAdapterEntry> ManualAdapters { get; set; } = new();
}
