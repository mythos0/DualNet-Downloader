using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public partial class AdapterChipViewModel : ObservableObject
{
    public AdapterChipViewModel(NetworkAdapterInfo info, string colorHex,
        Action<AdapterChipViewModel>? removeRequested = null)
    {
        Name = info.Name.Length > 18 ? info.Name[..18] : info.Name;
        IPv4Address = info.IPv4Address;
        SpeedText = info.IsOnline && info.SpeedBitsPerSecond > 0 ? info.SpeedText : "";
        TypeName = info.TypeName;
        IsManual = info.IsManual;
        IsOnline = info.IsOnline;
        Tooltip = $"{info.Description}\n{info.TypeName} · {info.IPv4Address}" +
                  (info.SpeedText.Length > 0 ? $" · {info.SpeedText}" : "") +
                  (info.IsManual ? "\nManual provider — click ✕ to remove" : "") +
                  (info.IsOnline ? "" : "\nOffline — this IP is not present on this PC right now");
        DotBrush = new SolidColorBrush(Color.Parse(IsOnline ? colorHex : "#6B7280"));
        RemoveProviderCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
            () => removeRequested?.Invoke(this), () => IsManual);
    }

    public string Name { get; }
    public string IPv4Address { get; }
    public string SpeedText { get; }
    public string TypeName { get; }
    public string Tooltip { get; }
    public bool IsManual { get; }
    public bool IsOnline { get; }
    public bool HasSpeed => SpeedText.Length > 0;
    public double ChipOpacity => IsOnline ? 1.0 : 0.55;
    public IBrush DotBrush { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand RemoveProviderCommand { get; }
}

public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel()
    {
        DownloadDirectory = AppServices.Settings.DownloadDirectory;
        MaxConcurrent = AppServices.Settings.MaxConcurrentDownloads;
        RetryCount = AppServices.Settings.RetryCount;
        SpeedLimitKbps = AppServices.Settings.SpeedLimitKbps;
        ClipboardMonitor = AppServices.Settings.ClipboardMonitor;
        ShutdownWhenComplete = AppServices.Settings.ShutdownWhenComplete;
        ProxyModeIndex = AppServices.Settings.ProxyMode;
        ProxyHost = AppServices.Settings.ProxyHost;
        ProxyPort = AppServices.Settings.ProxyPort;
        OrganizeByCategory = AppServices.Settings.OrganizeByCategory;
        LocalApiEnabled = AppServices.Settings.LocalApiEnabled;
        LocalApiPort = AppServices.Settings.LocalApiPort;
        CompletionSound = AppServices.Settings.CompletionSound;
        ScheduleEnabled = AppServices.Settings.ScheduleEnabled;
        ScheduleStartText = AppServices.Settings.ScheduleStart;
        ScheduleStopText = AppServices.Settings.ScheduleStop;
        SpeedTestUrl = AppServices.Settings.SpeedTestUrl;
        SearchEngineIndex = AppServices.Settings.SearchEngine;
        AdaptiveScaling = AppServices.Settings.AdaptiveScaling;
        SentryDsn = string.IsNullOrWhiteSpace(AppServices.Settings.SentryDsn)
            ? SentrySetup.DefaultDsn
            : AppServices.Settings.SentryDsn;
    }

    [ObservableProperty] private string downloadDirectory = "";
    [ObservableProperty] private double? maxConcurrent = 3;
    [ObservableProperty] private double? retryCount = 5;
    [ObservableProperty] private double? speedLimitKbps = 0;
    [ObservableProperty] private bool clipboardMonitor;
    [ObservableProperty] private bool shutdownWhenComplete;
    [ObservableProperty] private int proxyModeIndex;
    [ObservableProperty] private string proxyHost = "";
    [ObservableProperty] private double? proxyPort = 8080;
    [ObservableProperty] private bool organizeByCategory;
    [ObservableProperty] private bool localApiEnabled;
    [ObservableProperty] private double? localApiPort = 45454;
    [ObservableProperty] private bool completionSound;
    [ObservableProperty] private bool scheduleEnabled;
    [ObservableProperty] private string scheduleStartText = "09:00";
    [ObservableProperty] private string scheduleStopText = "23:00";
    [ObservableProperty] private string speedTestUrl = "";
    [ObservableProperty] private int searchEngineIndex;
    [ObservableProperty] private bool adaptiveScaling = true;
    [ObservableProperty] private string sentryDsn = "";
    [ObservableProperty] private string sentryTestResult = "";

    public string[] SearchEngineNames => DualNet.Core.Engine.Browser.SearchEngines.Names;

    public async Task SendSentryTestEvent()
    {
        // Re-init against whatever DSN is currently in the box, then send a visible event.
        SentrySetup.Restart(SentryDsn.Trim());
        AppServices.Settings.SentryDsn = SentryDsn.Trim();
        AppServices.SaveSettings();
        var ok = await SentrySetup.SendTestEventAsync();
        SentryTestResult = !SentrySetup.IsConfigured
            ? "DSN is empty — telemetry disabled."
            : ok
                ? "Test event sent — check your Sentry dashboard for 'self-test'."
                : "Send failed — SDK queue rejected the event.";
    }

    public void Save()
    {
        var s = AppServices.Settings;
        s.DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? s.DownloadDirectory : DownloadDirectory.Trim();
        s.MaxConcurrentDownloads = (int)Math.Clamp(MaxConcurrent ?? s.MaxConcurrentDownloads, 1, 10);
        s.RetryCount = (int)Math.Clamp(RetryCount ?? s.RetryCount, 1, 20);
        s.SpeedLimitKbps = (long)Math.Clamp(SpeedLimitKbps ?? s.SpeedLimitKbps, 0, 1_048_576);
        s.ClipboardMonitor = ClipboardMonitor;
        s.ShutdownWhenComplete = ShutdownWhenComplete;
        s.ProxyMode = Math.Clamp(ProxyModeIndex, 0, 2);
        s.ProxyHost = ProxyHost.Trim();
        s.ProxyPort = (int)Math.Clamp(ProxyPort ?? s.ProxyPort, 1, 65535);
        s.OrganizeByCategory = OrganizeByCategory;
        s.LocalApiEnabled = LocalApiEnabled;
        s.LocalApiPort = (int)Math.Clamp(LocalApiPort ?? s.LocalApiPort, 1024, 65535);
        s.CompletionSound = CompletionSound;
        s.ScheduleEnabled = ScheduleEnabled;
        s.ScheduleStart = ScheduleStartText.Trim();
        s.ScheduleStop = ScheduleStopText.Trim();
        s.SpeedTestUrl = SpeedTestUrl.Trim();
        s.SearchEngine = Math.Clamp(SearchEngineIndex, 0, DualNet.Core.Engine.Browser.SearchEngines.Names.Length - 1);
        s.AdaptiveScaling = AdaptiveScaling;
        s.SentryDsn = SentryDsn.Trim();
        AppServices.ApplySpeedLimit();
        AppServices.ApplyProxy();
        AppServices.ReconfigureLocalApi();
        AppServices.SaveSettings();
    }
}
