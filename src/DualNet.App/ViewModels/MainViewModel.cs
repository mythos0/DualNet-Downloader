using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<DownloadTabViewModel> Tabs { get; } = new();
    public ObservableCollection<AdapterChipViewModel> Adapters { get; } = new();
    public DownloadTabViewModel HistoryTab { get; }

    [ObservableProperty] private int themeMode;
    [ObservableProperty] private string adaptersSummary = "";
    [ObservableProperty] private string totalSpeedText = "0 B/s";
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private bool clipboardMonitor;
    [ObservableProperty] private bool shutdownWhenComplete;
    [ObservableProperty] private string? clipboardBannerText;
    [ObservableProperty] private string shutdownHint = "";

    public bool HasClipboardBanner => ClipboardUrl is not null;
    public bool ShowShutdownHint => ShutdownHint.Length > 0;

    private string? clipboardUrl;
    private string _lastClipboardText = "";
    private bool _shutdownTriggered;
    private readonly DispatcherTimer _clipboardTimer;

    /// <summary>The link currently detected in the clipboard (banner is shown when non-null).</summary>
    public string? ClipboardUrl
    {
        get => clipboardUrl;
        set
        {
            if (SetProperty(ref clipboardUrl, value))
                OnPropertyChanged(nameof(HasClipboardBanner));
        }
    }

    public DownloadTabViewModel? SelectedTab =>
        SelectedTabIndex >= 0 && SelectedTabIndex < Tabs.Count ? Tabs[SelectedTabIndex] : null;

    partial void OnClipboardBannerTextChanged(string? value) =>
        OnPropertyChanged(nameof(HasClipboardBanner));

    partial void OnShutdownHintChanged(string value) =>
        OnPropertyChanged(nameof(ShowShutdownHint));

    private static readonly string[] AdapterColors =
        { "#4F8CFF", "#B388FF", "#2DD4BF", "#F472B6", "#FBBF24", "#A3E635", "#FB923C" };

    private readonly DispatcherTimer _totalTimer;

    public MainViewModel()
    {
        AppServices.RefreshAdapters();

        Tabs.Add(new DownloadTabViewModel(TabKind.Combined));
        foreach (var adapter in AppServices.Adapters)
            Tabs.Add(new DownloadTabViewModel(TabKind.SingleAdapter, adapter));
        HistoryTab = new DownloadTabViewModel(TabKind.History);
        Tabs.Add(HistoryTab);

        themeMode = AppServices.Settings.ThemeMode;
        _totalTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateTotals());
        _totalTimer.IsEnabled = true;

        // Load toggles through fields (not properties) so saving isn't triggered during construction.
        clipboardMonitor = AppServices.Settings.ClipboardMonitor;
        shutdownWhenComplete = AppServices.Settings.ShutdownWhenComplete;
        _clipboardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
            (_, _) => CheckClipboard());
        _clipboardTimer.IsEnabled = true;

        AppServices.Manager.JobEnded += OnJobEnded;
        LoadAdapterChips();

        foreach (var job in AppServices.Manager.RecoverFromDirectory(
                     AppServices.Settings.DownloadDirectory,
                     () => AppServices.AllAdapterIPs(),
                     (label, ids) =>
                     {
                         var r = AppServices.ResolveBindsForTab(label, ids);
                         return r.Count > 0 ? r : null;
                     }))
        {
            AddRestoredJob(job);
        }
    }

    private void AddRestoredJob(DownloadJob job)
    {
        var tab = Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.Combined && job.Scope.Label == "All") ??
            Tabs.FirstOrDefault(t => t.Kind == TabKind.SingleAdapter && t.ScopeLabel == job.Scope.Label) ??
            Tabs[0];
        tab.AddRestoredJob(job);
    }

    partial void OnThemeModeChanged(int value)
    {
        AppServices.ApplyTheme(value);
        AppServices.Settings.ThemeMode = value;
        AppServices.SaveSettings();
    }

    partial void OnClipboardMonitorChanged(bool value)
    {
        AppServices.Settings.ClipboardMonitor = value;
        AppServices.SaveSettings();
        if (!value)
        {
            ClipboardUrl = null;
            ClipboardBannerText = null;
        }
    }

    partial void OnShutdownWhenCompleteChanged(bool value)
    {
        AppServices.Settings.ShutdownWhenComplete = value;
        AppServices.SaveSettings();
        if (!value)
        {
            _shutdownTriggered = false;
            ShutdownHint = "";
        }
    }

    private void OnJobEnded(DownloadJob job)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (job.Status)
            {
                case DownloadStatus.Completed:
                    HistoryTab.RecordHistory(job, "Completed", null);
                    SentrySetup.TrackDownloadCompleted(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label,
                        job.TotalBytes > 0 ? job.TotalBytes : job.ReceivedBytes,
                        job.DurationSeconds,
                        job.AverageSpeedBps);
                    break;
                case DownloadStatus.Failed:
                    HistoryTab.RecordHistory(job, "Failed", job.StatusMessage);
                    SentrySetup.TrackDownloadFailed(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label,
                        job.StatusMessage);
                    break;
                case DownloadStatus.Canceled:
                    SentrySetup.TrackDownloadCanceled(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label);
                    break;
            }

            MaybeShutdown();
        });
    }

    // ---- clipboard link watcher ----------------------------------------------------------------

    private async void CheckClipboard()
    {
        if (!ClipboardMonitor)
            return;
        try
        {
            var top = AppServices.MainWindow;
            if (top?.Clipboard is null) return;
            var text = await top.Clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText)
                return;
            _lastClipboardText = text;

            var url = ExtractFirstUrl(text);
            if (url is null || url == ClipboardUrl)
                return;

            ClipboardUrl = url;
            ClipboardBannerText = url.Length > 58 ? url[..55] + "…" : url;
        }
        catch { }
    }

    private static string? ExtractFirstUrl(string text)
    {
        foreach (var token in text.Split(new[] { '\r', '\n', ' ', '\t', ';', '"', '\'' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only tokens that already carry a scheme — never guess links from plain text.
            if (!token.Contains("://", StringComparison.Ordinal))
                continue;
            if (UrlNormalizer.Normalize(token) is { } normalized &&
                normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return normalized;
        }
        return null;
    }

    [RelayCommand]
    private void GoClipboardUrl()
    {
        var url = ClipboardUrl;
        if (url is null) return;

        var tab = SelectedTab is { IsDownloadTab: true } t ? t : Tabs.FirstOrDefault(x => x.IsDownloadTab);
        tab?.Navigate(url);
        ClipboardUrl = null;
        ClipboardBannerText = null;
    }

    [RelayCommand]
    private void DismissClipboard()
    {
        ClipboardUrl = null;
        ClipboardBannerText = null;
    }

    // ---- auto shutdown ---------------------------------------------------------------------------

    private void MaybeShutdown()
    {
        if (!ShutdownWhenComplete || _shutdownTriggered)
            return;

        var jobs = AppServices.Manager.Jobs;
        if (jobs.Count == 0)
            return;
        bool busy = jobs.Any(j => j.Status is DownloadStatus.Downloading or DownloadStatus.Preparing
                                                or DownloadStatus.Queued or DownloadStatus.Pausing);
        if (busy)
            return;

        _shutdownTriggered = true;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("shutdown", "/s /t 30") { CreateNoWindow = true });
                ShutdownHint = "All downloads finished — Windows shuts down in 30 s (cancel: shutdown /a)";
            }
            catch
            {
                ShutdownHint = "All downloads finished (auto shutdown failed)";
            }
        }
        else
        {
            ShutdownHint = "All downloads finished (auto shutdown is Windows-only)";
        }
    }

    private void LoadAdapterChips()
    {
        Adapters.Clear();
        for (int i = 0; i < AppServices.Adapters.Count; i++)
            Adapters.Add(new AdapterChipViewModel(AppServices.Adapters[i],
                AdapterColors[i % AdapterColors.Length]));
        AdaptersSummary = AppServices.Adapters.Count switch
        {
            0 => "No active network adapter detected",
            1 => $"1 adapter: {AppServices.Adapters[0].TypeName} ({AppServices.Adapters[0].IPv4Address})",
            _ => $"{AppServices.Adapters.Count} adapters: " +
                 string.Join(" + ", AppServices.Adapters.Select(a => $"{a.TypeName} ({a.IPv4Address})"))
        };
    }

    private void UpdateTotals()
    {
        double sum = Tabs.Where(t => t.Kind != TabKind.History).Sum(t => t.TabSpeedBps);
        TotalSpeedText = Formatter.Speed(sum);
    }

    [RelayCommand]
    private void RefreshNetworks()
    {
        AppServices.RefreshAdapters();
        LoadAdapterChips();
    }

    [RelayCommand]
    private void OpenDownloadsFolder()
    {
        try
        {
            var dir = AppServices.Settings.DownloadDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{dir}\"")
                {
                    UseShellExecute = true
                });
        }
        catch { }
    }

    public void Shutdown()
    {
        _totalTimer.IsEnabled = false;
        _clipboardTimer.IsEnabled = false;
        foreach (var tab in Tabs)
            tab.Shutdown();
        AppServices.Manager.PauseAll();
        AppServices.SaveSettings();
    }
}
