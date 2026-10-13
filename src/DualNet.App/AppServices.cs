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
    public static IReadOnlyList<NetworkAdapterInfo> Adapters { get; private set; } = Array.Empty<NetworkAdapterInfo>();
    public static Window? MainWindow { get; set; }

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

        RefreshAdapters();
        ApplyTheme(Settings.ThemeMode);
    }

    public static void RefreshAdapters() => Adapters = NetworkAdapterService.GetActiveAdapters();

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
                    string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Name, id, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return new List<IPAddress> { IPAddress.Parse(match.IPv4Address) };
            }
            return Array.Empty<IPAddress>();
        }
        return Adapters.Select(a => IPAddress.Parse(a.IPv4Address)).ToList();
    }

    /// <summary>All currently active adapter IPs (used by the Combined tab).</summary>
    public static IReadOnlyList<IPAddress> AllAdapterIPs()
    {
        RefreshAdapters();
        return Adapters.Select(a => IPAddress.Parse(a.IPv4Address)).ToList();
    }
}
