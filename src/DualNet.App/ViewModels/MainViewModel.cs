using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using DualNet.Core.Net;

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
    [ObservableProperty] private string scheduleStatus = "";
    [ObservableProperty] private bool showShelf = true;
    [ObservableProperty] private int activeDownloadCount;

    public bool HasActiveDownloads => ActiveDownloadCount > 0;

    partial void OnActiveDownloadCountChanged(int value) => OnPropertyChanged(nameof(HasActiveDownloads));

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(SelectedTab));

    public bool HasClipboardBanner => ClipboardUrl is not null;
    public bool ShowShutdownHint => ShutdownHint.Length > 0;
    public bool ShowScheduleStatus => ScheduleStatus.Length > 0;

    private string? clipboardUrl;
    private string _lastClipboardText = "";
    private bool _shutdownTriggered;
    private ScheduleWindow _lastScheduleWindow = ScheduleWindow.Outside;
    private bool _schedulePrimed;
    private readonly DispatcherTimer _clipboardTimer;
    private readonly DispatcherTimer _netWatchTimer;
    private string _lastNetSignature = "";

    /// <summary>UI toast notifications (download finished, link received, …).</summary>
    public event Action<string, string, bool>? ToastRequested;   // title, message, isError

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

    partial void OnScheduleStatusChanged(string value) =>
        OnPropertyChanged(nameof(ShowScheduleStatus));

    private static readonly string[] AdapterColors =
        { "#4F8CFF", "#B388FF", "#2DD4BF", "#F472B6", "#FBBF24", "#A3E635", "#FB923C" };

    private readonly DispatcherTimer _totalTimer;

    public MainViewModel()
    {
        AppServices.RefreshAdapters();

        Tabs.Add(new DownloadTabViewModel(TabKind.Combined));
        HistoryTab = new DownloadTabViewModel(TabKind.History);
        Tabs.Add(HistoryTab);

        // Build one tab per detected adapter (auto-detected + online manual providers)
        // and the header chips. Re-run on every rescan so newly arrived networks
        // always get their tab.
        SyncTabsAndChips();
        _lastNetSignature = NetSignature(AppServices.Adapters);

        themeMode = AppServices.Settings.ThemeMode;
        _totalTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateTotals());
        _totalTimer.IsEnabled = true;

        // Auto-refresh networks: plug in a cable / join a Wi-Fi network and the
        // matching tab appears within seconds — no manual rescan needed.
        _netWatchTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => AutoRefreshNetworks());
        _netWatchTimer.IsEnabled = true;

        // Load toggles through fields (not properties) so saving isn't triggered during construction.
        clipboardMonitor = AppServices.Settings.ClipboardMonitor;
        shutdownWhenComplete = AppServices.Settings.ShutdownWhenComplete;
        _clipboardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
            (_, _) => CheckClipboard());
        _clipboardTimer.IsEnabled = true;

        AppServices.Manager.JobEnded += OnJobEnded;
        AttachLocalApi();

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

        foreach (var url in StartupRequests.PendingUrls)
            Tabs[0].Navigate(url);
        StartupRequests.PendingUrls.Clear();
    }

    /// <summary>Wires the localhost integration API (re-attach after settings changes).</summary>
    public void AttachLocalApi()
    {
        if (AppServices.LocalApi is null)
            return;
        AppServices.LocalApi.UrlReceived -= OnLocalApiUrl;
        AppServices.LocalApi.UrlReceived += OnLocalApiUrl;
    }

    private void OnLocalApiUrl(string url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Tabs[0].Navigate(url);
            ToastRequested?.Invoke("Link received", url, false);
        });
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

    private void OnTabDiagnostic(string kind, string message)
    {
        SentrySetup.TrackDiagnostic(kind, message);
        if (kind is "range_fallback" or "resume_reset")
            ToastRequested?.Invoke("Server quirk handled",
                "The server ignored byte ranges — DualNet switched modes and kept the download going.", false);
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
                    ToastRequested?.Invoke("Download complete",
                        Path.GetFileName(job.FilePath), false);
                    if (AppServices.Settings.CompletionSound)
                        PlayCompletionSound();
                    break;
                case DownloadStatus.Failed:
                    HistoryTab.RecordHistory(job, "Failed", job.StatusMessage);
                    SentrySetup.TrackDownloadFailed(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label,
                        job.StatusMessage);
                    ToastRequested?.Invoke("Download failed",
                        $"{Path.GetFileName(job.FilePath)} — {job.StatusMessage ?? "unknown error"}", true);
                    break;
                case DownloadStatus.Canceled:
                    SentrySetup.TrackDownloadCanceled(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label);
                    break;
            }

            MaybeShutdown();
        });
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool MessageBeep(uint type);

    private static void PlayCompletionSound()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                MessageBeep(0x00000000u);   // default beep
        }
        catch { }
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

    /// <summary>External entry point (drag & drop, .txt files): a single link is browsed,
    /// a multi-link list is queued for download directly.</summary>
    public void AddUrlsFromText(string text)
    {
        var urls = (text ?? "")
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => UrlNormalizer.Normalize(t))
            .Where(n => n is not null && n.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (urls.Count == 0) return;

        var tab = SelectedTab is { IsDownloadTab: true } t ? t : Tabs.FirstOrDefault(x => x.IsDownloadTab);
        if (tab is null) return;

        if (urls.Count == 1)
        {
            tab.Navigate(urls[0]);   // browse — file links start downloading automatically
        }
        else
        {
            foreach (var url in urls)
                tab.AddDownload(url, out _);
        }
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

    // ---- network adapters: chips, tabs, auto-refresh -------------------------------------------

    private static string NetSignature(IReadOnlyList<NetworkAdapterInfo> adapters) =>
        string.Join("|", adapters.Select(a => $"{a.Id}~{a.IPv4Address}~{a.Name}~{(a.IsOnline ? 1 : 0)}"));

    /// <summary>
    /// Rebuilds header chips + adapter tabs from the current adapter set.
    /// New networks get a tab; networks that vanished have their downloads moved
    /// to the Combined tab and their tab removed. Selection is preserved.
    /// </summary>
    internal void SyncTabsAndChips()
    {
        AppServices.RefreshAdapters();
        var online = AppServices.Adapters.Where(a => a.IsOnline).ToList();
        var selected = SelectedTab;

        // 1. Tabs whose adapter is gone (or offline) → migrate items, remove tab.
        foreach (var tab in Tabs.Where(t => t.Kind == TabKind.SingleAdapter).ToList())
        {
            bool alive = online.Any(a =>
                string.Equals(a.Id, tab.ScopeLabel, StringComparison.OrdinalIgnoreCase));
            if (alive)
                continue;

            var combined = Tabs.FirstOrDefault(t => t.Kind == TabKind.Combined);
            if (combined is not null)
                foreach (var item in tab.Items.ToList())
                    combined.AdoptItem(item);
            Tabs.Remove(tab);
            tab.Shutdown();
            ToastRequested?.Invoke("Network went offline",
                $"{tab.Title} is no longer available — its downloads moved to the Combined tab.", false);
            SentrySetup.TrackDiagnostic("adapter_offline", tab.ScopeLabel);
        }

        // 2. New networks → insert a tab just before History.
        foreach (var adapter in online)
        {
            bool exists = Tabs.Any(t => t.Kind == TabKind.SingleAdapter &&
                string.Equals(t.ScopeLabel, adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (exists)
                continue;

            var tab = new DownloadTabViewModel(TabKind.SingleAdapter, adapter);
            tab.Diagnostic += OnTabDiagnostic;
            Tabs.Insert(Math.Max(0, Tabs.IndexOf(HistoryTab)), tab);
            ToastRequested?.Invoke("New network detected",
                $"Tab added: {tab.Title} ({adapter.IPv4Address})", false);
            SentrySetup.TrackDiagnostic("adapter_online", $"{adapter.Name} {adapter.IPv4Address}");
        }

        LoadAdapterChips();

        // 3. Keep the user's current tab selected whenever it still exists.
        if (selected is not null && Tabs.Contains(selected))
            SelectedTabIndex = Tabs.IndexOf(selected);
        else if (SelectedTabIndex < 0 || SelectedTabIndex >= Tabs.Count)
            SelectedTabIndex = 0;
    }

    /// <summary>Background watcher tick — detects newly arrived / departed networks.</summary>
    private void AutoRefreshNetworks()
    {
        try
        {
            var fresh = NetworkAdapterService.GetActiveAdapters(AppServices.Settings.ManualAdapters);
            if (NetSignature(fresh) == _lastNetSignature)
                return;   // nothing changed — don't touch the UI
            RefreshNetworksCore();
        }
        catch { }
    }

    private void RefreshNetworksCore()
    {
        SyncTabsAndChips();
        _lastNetSignature = NetSignature(AppServices.Adapters);
    }

    private void LoadAdapterChips()
    {
        Adapters.Clear();
        int i = 0;
        foreach (var adapter in AppServices.Adapters)   // includes offline manual providers (greyed)
        {
            var info = adapter;
            Adapters.Add(new AdapterChipViewModel(info,
                AdapterColors[i++ % AdapterColors.Length],
                info.IsManual
                    ? _ => RemoveManualAdapter(info.IPv4Address)
                    : null));
        }

        int onlineCount = AppServices.Adapters.Count(a => a.IsOnline);
        int manualCount = AppServices.Adapters.Count(a => a.IsManual);
        int manualOnline = AppServices.Adapters.Count(a => a.IsManual && a.IsOnline);
        string baseSummary = onlineCount switch
        {
            0 => "No active network adapter detected",
            1 => $"1 adapter: {AppServices.Adapters.First(a => a.IsOnline).TypeName} " +
                 $"({AppServices.Adapters.First(a => a.IsOnline).IPv4Address})",
            _ => $"{onlineCount} adapters: " +
                 string.Join(" + ", AppServices.Adapters.Where(a => a.IsOnline)
                     .Select(a => $"{a.TypeName} ({a.IPv4Address})"))
        };
        AdaptersSummary = manualCount == 0
            ? baseSummary
            : $"{baseSummary} · {manualCount} manual provider{(manualCount == 1 ? "" : "s")}" +
              (manualOnline < manualCount ? $" ({manualCount - manualOnline} offline)" : "");
    }

    [RelayCommand]
    private void RefreshNetworks() => RefreshNetworksCore();

    [RelayCommand]
    private async Task AddManualProviderAsync()
    {
        var dialog = new Views.ManualProviderWindow();
        var owner = AppServices.MainWindow;
        if (owner is { IsVisible: true })
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
        // The dialog persists its changes to Settings — pick them up now.
        RefreshNetworksCore();
    }

    private void RemoveManualAdapter(string ip)
    {
        int removed = AppServices.Settings.ManualAdapters.RemoveAll(
            m => string.Equals(m.IPv4Address, ip, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;
        AppServices.SaveSettings();
        RefreshNetworksCore();
        ToastRequested?.Invoke("Manual provider removed", ip, false);
        SentrySetup.TrackDiagnostic("manual_provider_removed", ip);
    }

    private void UpdateTotals()
    {
        double sum = Tabs.Where(t => t.Kind != TabKind.History).Sum(t => t.TabSpeedBps);
        TotalSpeedText = Formatter.Speed(sum);
        ActiveDownloadCount = AppServices.Manager.Jobs.Count(j =>
            j.Status is DownloadStatus.Downloading or DownloadStatus.Preparing
                     or DownloadStatus.Queued or DownloadStatus.Pausing);
        CheckSchedule();
    }

    // ---- Chrome-style tab management -------------------------------------------------------------

    /// <summary>＋ button / Ctrl+T: opens a fresh browser tab over all active networks.</summary>
    [RelayCommand]
    private void NewTab()
    {
        var tab = new DownloadTabViewModel(TabKind.Combined)
        {
            FollowsPageTitle = true,
            ShowShelf = ShowShelf
        };
        tab.Glyph = "🌐";
        tab.Diagnostic += OnTabDiagnostic;
        Tabs.Insert(Math.Max(0, Tabs.IndexOf(HistoryTab)), tab);
        SelectedTabIndex = Tabs.IndexOf(tab);
    }

    /// <summary>✕ on a tab: closes it; the last tab is never removed.</summary>
    [RelayCommand]
    private void CloseTab(DownloadTabViewModel? tab)
    {
        if (tab is null || tab == HistoryTab || !Tabs.Contains(tab))
            return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Shutdown();
        if (Tabs.Count == 0)
        {
            NewTab();
            return;
        }
        SelectedTabIndex = Math.Clamp(index, 0, Tabs.Count - 1);
    }

    /// <summary>⤓ toolbar button: shows/hides the downloads shelf (all tabs together).</summary>
    [RelayCommand]
    private void ToggleShelf()
    {
        ShowShelf = !ShowShelf;
        foreach (var tab in Tabs)
            tab.ShowShelf = ShowShelf;
    }

    // ---- ⋮ menu commands (act across every tab) ----------------------------------------------------

    [RelayCommand]
    private void PauseAllEverywhere()
    {
        foreach (var tab in Tabs.Where(t => t.IsDownloadTab))
            tab.PauseAllCommand.Execute(null);
    }

    [RelayCommand]
    private void ResumeAllEverywhere()
    {
        foreach (var tab in Tabs.Where(t => t.IsDownloadTab))
            tab.ResumeAllCommand.Execute(null);
    }

    [RelayCommand]
    private void RetryFailedEverywhere()
    {
        foreach (var tab in Tabs.Where(t => t.IsDownloadTab))
            tab.RetryFailedCommand.Execute(null);
    }

    [RelayCommand]
    private void SpeedTestSelected()
    {
        if (SelectedTab is { IsDownloadTab: true } tab)
            tab.RunSpeedTestCommand.Execute(null);
    }

    [RelayCommand]
    private void SetTheme(string mode) =>
        ThemeMode = mode switch { "dark" => 1, "light" => 2, _ => 0 };

    // ---- IDM-style scheduler ---------------------------------------------------------------------

    private void CheckSchedule()
    {
        var s = AppServices.Settings;
        if (!s.ScheduleEnabled)
        {
            if (_schedulePrimed)
            {
                _schedulePrimed = false;
                _lastScheduleWindow = ScheduleWindow.Outside;
                ScheduleStatus = "";
            }
            return;
        }

        if (!SchedulerService.TryParseHhMm(s.ScheduleStart, out var start) ||
            !SchedulerService.TryParseHhMm(s.ScheduleStop, out var stop))
        {
            ScheduleStatus = "Scheduler: invalid start/stop time (use HH:mm)";
            return;
        }

        var window = SchedulerService.Evaluate(TimeOnly.FromDateTime(DateTime.Now), start, stop);
        var action = SchedulerService.ActionForTransition(_lastScheduleWindow, window);

        if (!_schedulePrimed)
        {
            _schedulePrimed = true;
            action = SchedulerService.ActionForTransition(ScheduleWindow.Outside, window);
        }

        if (action is null)
            return;

        _lastScheduleWindow = window;
        ScheduleStatus = window == ScheduleWindow.Inside
            ? $"Scheduler: downloading until {s.ScheduleStop}"
            : $"Scheduler: paused until {s.ScheduleStart}";

        if (action == "start")
            StartAllJobs();
        else
            AppServices.Manager.PauseAll();
    }

    private void StartAllJobs()
    {
        foreach (var job in AppServices.Manager.Jobs
                     .Where(j => j.Status is DownloadStatus.Paused or DownloadStatus.Queued).ToList())
            job.Start();
    }

    [RelayCommand]
    private void StartAll() => StartAllJobs();

    // ---- history export ----------------------------------------------------------------------------

    [RelayCommand]
    private void ExportHistory()
    {
        try
        {
            var dir = AppServices.Settings.DownloadDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"DualNet-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            var rows = AppServices.History.ExportCsv(path);
            ToastRequested?.Invoke("History exported", $"{rows} rows → {path}", false);
        }
        catch (Exception ex)
        {
            ToastRequested?.Invoke("Export failed", ex.Message, true);
        }
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
        _netWatchTimer.IsEnabled = false;
        foreach (var tab in Tabs)
            tab.Shutdown();
        AppServices.Manager.PauseAll();
        AppServices.SaveSettings();
    }
}
