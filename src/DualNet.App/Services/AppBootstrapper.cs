using System.IO;
using System.Net;
using DualNet.Core;
using DualNet.Core.Engine;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Models;
using DualNet.Core.Net;
using DualNet.Core.Storage;

namespace DualNet.App.Services;

/// <summary>
/// Service hub for the browser shell: settings store, download engine, NIC cache,
/// browser stores (bookmarks/history/session) and the online free-proxy provider.
/// </summary>
public static class AppBootstrapper
{
    private static JsonSettingsStore? _store;
    private static BrowserStore? _browserStore;
    private static SessionStore? _sessionStore;
    private static ProxyService? _proxyService;

    public static string AppDataDir { get; private set; } = "";
    public static AppSettings Settings => _store?.Data ?? new AppSettings();
    public static DownloadManager? Downloads { get; private set; }
    public static BrowserStore BrowserStore => _browserStore ??= new BrowserStore(AppDataDir);
    public static SessionStore Sessions => _sessionStore ??= new SessionStore(AppDataDir);
    public static ProxyService Proxies => _proxyService ??= new ProxyService(
        Path.Combine(AppDataDir, "proxy-cache.json"));

    public static event Action? SettingsChanged;

    public static void Initialize(string appDataDir)
    {
        AppDataDir = appDataDir;
        Directory.CreateDirectory(appDataDir);
        _store = new JsonSettingsStore(Path.Combine(appDataDir, "settings.json"));

        // Sentry: settings override → shipped default (with legacy-DSN migration);
        // the user can disable telemetry entirely in Settings → Telemetry.
        var dsn = Settings.SentryEnabled
            ? (string.IsNullOrWhiteSpace(Settings.SentryDsn) ? SentrySetup.DefaultDsn : SentrySetup.NormalizeDsn(Settings.SentryDsn))
            : "";
        SentrySetup.Init(dsn);

        // Global engine proxy (direct by default; per-download proxies override).
        HttpConnectionFactory.ConfigureProxy(Settings.ProxyMode, Settings.ProxyHost, Settings.ProxyPort);

        Downloads = new DownloadManager(() => Settings);

        // Recover unfinished downloads left by a previous run (crash-recovery resumables).
        try
        {
            var downloadDir = EffectiveDownloadDir();
            var adapters = NetworkAdapterService.GetActiveAdapters(Settings.ManualAdapters);
            var ids = adapters.Where(a => a.IsOnline).Select(a => a.Id).ToList();
            Downloads.RecoverFromDirectory(
                downloadDir,
                () => adapters.Where(a => a.IsOnline).Select(a => a.IPv4Address).Select(IPAddress.Parse).ToList(),
                (scopeLabel, adapterIds) =>
                {
                    var online = adapters.Where(a => a.IsOnline).ToList();
                    var wanted = adapterIds.Where(ids.Contains).ToHashSet(StringComparer.Ordinal);
                    var binds = online.Where(a => wanted.Count == 0 || wanted.Contains(a.Id))
                                      .Select(a => IPAddress.Parse(a.IPv4Address))
                                      .ToList();
                    return binds.Count > 0 ? binds : null;
                });
        }
        catch (Exception ex)
        {
            SentrySetup.CaptureException(ex, "recovery");
        }
    }

    public static string EffectiveDownloadDir()
    {
        var dir = Settings.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void SaveSettings()
    {
        _store?.Save();
        SettingsChanged?.Invoke();
    }

    public static void SetSetting(Action<AppSettings> mutate)
    {
        mutate(Settings);
        SaveSettings();
    }

    public static void Shutdown()
    {
        // Persist the session is done by MainWindow before close; here we stop the engine.
        try { Downloads?.Dispose(); } catch { }
        try { SentrySetup.FlushSync(); } catch { }
    }

    /// <summary>Resolves an adapter id ("all", "eth-1", "manual:…") to bind IP addresses.</summary>
    public static IReadOnlyList<IPAddress> ResolveBinds(string scopeId)
    {
        var adapters = NetworkAdapterService.GetActiveAdapters(Settings.ManualAdapters)
            .Where(a => a.IsOnline)
            .ToList();
        if (string.IsNullOrEmpty(scopeId) || scopeId == "all")
            return adapters.Select(a => IPAddress.Parse(a.IPv4Address)).ToList();

        var chosen = adapters.FirstOrDefault(a => string.Equals(a.Id, scopeId, StringComparison.Ordinal));
        if (chosen is null)
            return adapters.Select(a => IPAddress.Parse(a.IPv4Address)).ToList();
        return new[] { IPAddress.Parse(chosen.IPv4Address) };
    }

    public static IReadOnlyList<NetworkAdapterInfo> OnlineAdapters() =>
        NetworkAdapterService.GetActiveAdapters(Settings.ManualAdapters).Where(a => a.IsOnline).ToList();
}
