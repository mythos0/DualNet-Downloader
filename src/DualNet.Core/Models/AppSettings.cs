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

    /// <summary>Hard cap on simultaneous connections per download (4–64, default 32).</summary>
    public int MaxConnectionsPerDownload { get; set; } = 32;

    /// <summary>
    /// Browse with the embedded real browser engine (Chromium via WebView2) when available;
    /// off or unavailable = lightweight reader-mode browser.
    /// </summary>
    public bool UseRealBrowser { get; set; } = true;

    /// <summary>Search engine used when a non-URL is typed in the address bar (index into SearchEngines.Names).</summary>
    public int SearchEngine { get; set; }

    /// <summary>Sentry DSN (override to point the app at a fresh/rotated DSN without a rebuild).</summary>
    public string SentryDsn { get; set; } = "";

    /// <summary>User-defined network providers for adapters the auto-scan does not offer.</summary>
    public List<ManualAdapterEntry> ManualAdapters { get; set; } = new();

    // ---- v4.0.0 browser settings -------------------------------------------------------

    /// <summary>Block third-party cookies in the browser (Chromium phaseout flag; restart applies).</summary>
    public bool BlockThirdPartyCookies { get; set; } = true;

    /// <summary>Show the bookmarks bar under the toolbar.</summary>
    public bool ShowBookmarksBar { get; set; } = true;

    /// <summary>Ask where to save each download before it starts.</summary>
    public bool AskWhereToSave { get; set; }

    /// <summary>Default download NIC scope: "all" (combined) or a specific adapter id.</summary>
    public string DefaultDownloadScope { get; set; } = "all";

    /// <summary>Bundled extensions that should be loaded on startup ("uBlockOrigin", "uBlockOriginLite").</summary>
    public List<string> EnabledExtensions { get; set; } = new() { "uBlockOrigin" };

    /// <summary>Reopen the previous session's tabs on launch (crash recovery / session restore).</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Show full URLs (with scheme) in the omnibox instead of trimming https://www.</summary>
    public bool ShowFullUrls { get; set; } = true;

    /// <summary>Send crash reports / events to Sentry (Settings → Telemetry can disable).</summary>
    public bool SentryEnabled { get; set; } = true;

    /// <summary>Default download NIC scope per new tab mirrors this app-level default ("all" or adapter id).</summary>
    public string DefaultTabNetScope => string.IsNullOrWhiteSpace(DefaultDownloadScope) ? "all" : DefaultDownloadScope;

    // ---- window state -------------------------------------------------------------------

    public WindowRect? WindowBounds { get; set; }

    public bool WindowMaximized { get; set; }
}

/// <summary>Persisted window rectangle.</summary>
public sealed record WindowRect(double X, double Y, double Width, double Height);
