using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public partial class AdapterChipViewModel : ObservableObject
{
    public AdapterChipViewModel(NetworkAdapterInfo info, string colorHex)
    {
        Name = info.Name.Length > 18 ? info.Name[..18] : info.Name;
        IPv4Address = info.IPv4Address;
        SpeedText = info.SpeedBitsPerSecond > 0 ? info.SpeedText : "";
        TypeName = info.TypeName;
        Tooltip = $"{info.Description}\n{info.TypeName} · {info.IPv4Address} · {info.SpeedText}";
        DotBrush = new SolidColorBrush(Color.Parse(colorHex));
    }

    public string Name { get; }
    public string IPv4Address { get; }
    public string SpeedText { get; }
    public string TypeName { get; }
    public string Tooltip { get; }
    public IBrush DotBrush { get; }
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
    }

    [ObservableProperty] private string downloadDirectory = "";
    [ObservableProperty] private double? maxConcurrent = 3;
    [ObservableProperty] private double? retryCount = 5;
    [ObservableProperty] private double? speedLimitKbps = 0;
    [ObservableProperty] private bool clipboardMonitor;
    [ObservableProperty] private bool shutdownWhenComplete;

    public void Save()
    {
        var s = AppServices.Settings;
        s.DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? s.DownloadDirectory : DownloadDirectory.Trim();
        s.MaxConcurrentDownloads = (int)Math.Clamp(MaxConcurrent ?? s.MaxConcurrentDownloads, 1, 10);
        s.RetryCount = (int)Math.Clamp(RetryCount ?? s.RetryCount, 1, 20);
        s.SpeedLimitKbps = (long)Math.Clamp(SpeedLimitKbps ?? s.SpeedLimitKbps, 0, 1_048_576);
        s.ClipboardMonitor = ClipboardMonitor;
        s.ShutdownWhenComplete = ShutdownWhenComplete;
        AppServices.ApplySpeedLimit();
        AppServices.SaveSettings();
    }
}
