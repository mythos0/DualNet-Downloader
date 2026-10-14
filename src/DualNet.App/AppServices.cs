using System.Net;
using Avalonia.Controls;
using Avalonia.Styling;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using DualNet.Core.Net;
using DualNet.Core.Storage;

namespace DualNet.App;

/// <summary>Application-wide service locator + theme handling.</summary>
public static class AppServices
{
    private static bool _initialized;

    public static string AppDataDir { get; private set; } = "";
    public static string SettingsPath { get; private set; } = "";
    public static AppSettings Settings { get; private set; } = new();
    public static HistoryService History { get; private set; } = null!;
    public static DownloadManager Manager { get; private set; } = null!;
    public static BrowserStore BrowserData { get; private set; } = null!;
    public static LocalApiService? LocalApi { get; private set; }
    public static IReadOnlyList<NetworkAdapterInfo> Adapters { get; private set; } = Array.Empty<NetworkAdapterInfo>();
    public static Window? MainWindow { get; set; }

    /// <summary>Reads the persisted Sentry DSN before full app init (Sentry boots first).</summary>
    public static string TryLoadDsn()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData, "DualNet");
            var settings = JsonStore.Load<AppSettings>(Path.Combine(dir, "settings.json"));
            if (settings is null || string.IsNullOrWhiteSpace(settings.SentryDsn))
                return SentrySetup.DefaultDsn;   // never configured — keep the shipped DSN
            return settings.SentryDsn;
        }
        catch
        {
            return SentrySetup.DefaultDsn;
        }
    }

    /// <summary>Reads the persisted local-API port before full app init (single-instance IPC).</summary>
    public static int TryLoadLocalApiPort()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData, "DualNet");
            var settings = JsonStore.Load<AppSettings>(Path.Combine(dir, "settings.json"));
            return settings?.LocalApiPort is > 1024 and < 65536 ? settings.LocalApiPort : 45454;
        }
        catch
        {
            return 45454;
        }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppDataDir = Path.Combine(string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData, "DualNet");
        Directory.CreateDirectory(AppDataDir);
        SettingsPath = Path.Combine(AppDataDir, "settings.json");

        Settings = JsonStore.Load<AppSettings>(SettingsPath);
        if (string.IsNullOrWhiteSpace(Settings.DownloadDirectory))
            Settings.DownloadDirectory = DefaultDownloadDirectory();

        History = new HistoryService(AppDataDir);
        Manager = new DownloadManager(() => Settings);
        BrowserData = new BrowserStore(AppDataDir);

        RefreshAdapters();
        ApplyTheme(Settings.ThemeMode);
        ApplySpeedLimit();
        ApplyProxy();

        if (Settings.LocalApiEnabled)
        {
            try
            {
                LocalApi = new LocalApiService(Math.Clamp(Settings.LocalApiPort, 1024, 65535));
                LocalApi.Start();
            }
            catch { LocalApi = null; }
        }
    }

    /// <summary>Restarts (or stops) the localhost integration API after settings change.</summary>
    public static void ReconfigureLocalApi()
    {
        LocalApi?.Dispose();
        LocalApi = null;
        if (Settings.LocalApiEnabled)
        {
            try
            {
                LocalApi = new LocalApiService(Math.Clamp(Settings.LocalApiPort, 1024, 65535));
                LocalApi.Start();
            }
            catch { LocalApi = null; }
        }
    }

    /// <summary>Pushes the user's proxy configuration into the connection factory.</summary>
    public static void ApplyProxy() =>
        HttpConnectionFactory.ConfigureProxy(Settings.ProxyMode, Settings.ProxyHost, Settings.ProxyPort);

    /// <summary>Pushes the user's speed cap (KB/s) into the engine's global limiter.</summary>
    public static void ApplySpeedLimit() =>
        GlobalSpeedLimit.SetLimit(Math.Max(0, Settings.SpeedLimitKbps) * 1024);

    public static void RefreshAdapters() =>
        Adapters = NetworkAdapterService.GetActiveAdapters(Settings.ManualAdapters);

    public static void SaveSettings() => JsonStore.Save(SettingsPath, Settings);

    public static void ApplyTheme(int mode) =>
        Avalonia.Application.Current!.RequestedThemeVariant = mode switch
        {
            1 => ThemeVariant.Dark,
            2 => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };

    public static string DefaultDownloadDirectory()
    {
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads)) return downloads;
        }
        catch { }
        return Environment.CurrentDirectory;
    }

    /// <summary>Resolves the adapter IPs a download in a given tab should use.</summary>
    public static IReadOnlyList<IPAddress> ResolveBindsForTab(string scopeLabel, IReadOnlyList<string> adapterIds)
    {
        RefreshAdapters();
        if (!string.Equals(scopeLabel, "All", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var id in adapterIds)
            {
                var match = Adapters.FirstOrDefault(a =>
                    a.IsOnline &&
                    (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a.Name, id, StringComparison.OrdinalIgnoreCase)));
                if (match is not null)
                    return new List<IPAddress> { IPAddress.Parse(match.IPv4Address) };
            }
            return Array.Empty<IPAddress>();
        }
        return OnlineAdapterIPs();
    }

    /// <summary>All currently online adapter IPs (used by the Combined tab). Offline manual providers are excluded.</summary>
    public static IReadOnlyList<IPAddress> AllAdapterIPs() => OnlineAdapterIPs();

    private static IReadOnlyList<IPAddress> OnlineAdapterIPs()
    {
        RefreshAdapters();
        return Adapters.Where(a => a.IsOnline)
            .Select(a => IPAddress.Parse(a.IPv4Address)).ToList();
    }
}
