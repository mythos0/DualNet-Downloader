using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public enum TabKind { Combined, SingleAdapter, History }

/// <summary>
/// One browser-style tab: an embedded browser (fetched over THIS tab's adapters) on top and
/// the download queue below. Navigating to a page renders it; navigating to a file starts
/// the download automatically. Each tab only uses the network adapter(s) it was created for.
/// </summary>
public partial class DownloadTabViewModel : ObservableObject
{
    [ObservableProperty] private string title;
    [ObservableProperty] private string glyph;
    [ObservableProperty] private bool showShelf = true;
    public string BaseTitle { get; }

    /// <summary>Chrome behavior: the tab label follows the page title (new browser tabs only).</summary>
    public bool FollowsPageTitle { get; set; }

    public IBrush AccentBrush { get; }
    public TabKind Kind { get; }
    public NetworkAdapterInfo? Adapter { get; }
    public string ScopeLabel { get; }
    public BrowserViewModel Browser { get; }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ObservableCollection<HistoryItemViewModel> HistoryEntries { get; } = new();
    public ObservableCollection<AdapterSpeedChipViewModel> AdapterSpeedChips { get; } = new();

    [ObservableProperty] private string? inputError;
    [ObservableProperty] private double tabSpeedBps;
    [ObservableProperty] private string tabSpeedText = "0 B/s";
    [ObservableProperty] private string itemSummaryText = "0 active";
    [ObservableProperty] private bool hasNoItems = true;
    [ObservableProperty] private bool isSpeedTesting;
    [ObservableProperty] private string speedTestText = "";

    partial void OnHasNoItemsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyListHint));
        OnPropertyChanged(nameof(ShowHistoryEmpty));
    }

    public bool IsDownloadTab => Kind != TabKind.History;
    public bool IsHistoryTab => Kind == TabKind.History;
    public bool IsCombined => Kind == TabKind.Combined;
    public bool ShowEmptyListHint => IsDownloadTab && HasNoItems;
    public bool ShowHistoryEmpty => IsHistoryTab && HasNoItems;
    public string ScopeHint => Kind switch
    {
        TabKind.Combined => "Uses every active adapter — Ethernet + Wi-Fi together",
        TabKind.SingleAdapter => $"Uses {Adapter?.Name} ({Adapter?.IPv4Address}) only",
        _ => ""
    };

    /// <summary>Raised every sampling tick with the tab's aggregate speed (bytes/s).</summary>
    public event Action<double>? SpeedSampled;

    /// <summary>Engine diagnostics (range fallback, resume reset…) re-raised from items.</summary>
    public event Action<string, string>? Diagnostic;

    private readonly DispatcherTimer _sampleTimer;

    public DownloadTabViewModel(TabKind kind, NetworkAdapterInfo? adapter = null)
    {
        Kind = kind;
        Adapter = adapter;
        var (g, t, brush, scope) = kind switch
        {
            TabKind.Combined => ("⚡", "Combined", new SolidColorBrush(Color.Parse("#22D3EE")), "All"),
            TabKind.SingleAdapter when adapter is { } a => (
                a.TypeName == "Wi-Fi" ? "📶" : "🔌",
                a.Name.Length > 22 ? a.Name[..22] : a.Name,
                new SolidColorBrush(Color.Parse(a.TypeName == "Wi-Fi" ? "#B388FF" : "#4F8CFF")),
                a.Id),
            _ => ("🕘", "History", new SolidColorBrush(Color.Parse("#9CA3AF")), "History")
        };
        glyph = g;
        title = t;
        BaseTitle = t;
        AccentBrush = brush;
        ScopeLabel = scope;

        Browser = new BrowserViewModel(
            ResolveTabBinds,
            url => AddDownload(url, out _),
            AppServices.BrowserData);

        // Chrome-like tab label: free-form browser tabs (created with ＋) rename
        // themselves to the loaded page's title.
        Browser.PropertyChanged += (_, e) =>
        {
            if (!FollowsPageTitle || e.PropertyName != nameof(BrowserViewModel.PageTitle))
                return;
            var page = Browser.PageTitle;
            Title = string.IsNullOrWhiteSpace(page) ? BaseTitle : page;
        };

        if (kind == TabKind.History)
        {
            ReloadHistory();
            AppServices.History.Changed += ReloadHistory;
            _sampleTimer = MakeTimer(false);
            return;
        }

        LoadAdapterChips();
        _sampleTimer = MakeTimer(true);
    }

    /// <summary>Adapter IPs this tab's browser + downloads are bound to.</summary>
    private IReadOnlyList<System.Net.IPAddress> ResolveTabBinds() =>
        Kind == TabKind.Combined
            ? AppServices.AllAdapterIPs()
            : AppServices.ResolveBindsForTab(ScopeLabel, new[] { ScopeLabel });

    private void LoadAdapterChips()
    {
        AdapterSpeedChips.Clear();
        foreach (var adapter in TabAdapters())
            AdapterSpeedChips.Add(new AdapterSpeedChipViewModel(adapter));
    }

    /// <summary>Adapters this tab is allowed to use, resolved fresh (offline providers excluded).</summary>
    public IReadOnlyList<NetworkAdapterInfo> TabAdapters() =>
        Kind == TabKind.Combined
            ? AppServices.Adapters.Where(a => a.IsOnline).ToList()
            : Adapter is null ? Array.Empty<NetworkAdapterInfo>() : new[] { Adapter };

    private DispatcherTimer MakeTimer(bool enabled)
    {
        var t = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleTick());
        t.IsEnabled = enabled;
        return t;
    }

    // ---- browser-style address bar -----------------------------------------------------------

    /// <summary>
    /// The tab's navigation entry point (address bar, clipboard, local API, command line).
    /// A single token goes through the browser: pages render, files auto-download.
    /// Multiple tokens are treated as a bulk link list and downloaded directly.
    /// </summary>
    public void Navigate(string? input)
    {
        InputError = null;
        var tokens = (input ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            InputError = "Type or paste a link first, e.g. https://server.com/file.zip";
            return;
        }

        var urls = tokens
            .SelectMany(t => t.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(t => t.Length > 0)
            .ToList();

        if (urls.Count == 0)
        {
            InputError = "Type or paste a link first, e.g. https://server.com/file.zip";
            return;
        }

        if (urls.Count == 1)
        {
            // Browser flow: page → render; file → download starts automatically.
            _ = Browser.GoAsync(urls[0]);
            return;
        }

        // Bulk list: every token must be a valid URL, then queue them all.
        var errors = new List<string>();
        foreach (var raw in urls)
        {
            var url = UrlNormalizer.Normalize(raw);
            if (url is null)
            {
                errors.Add($"'{TrimUrl(raw)}' is not a valid http(s) URL");
                continue;
            }
            if (AddDownload(url, out var error) is null && error is not null)
                errors.Add(error);
        }
        if (errors.Count > 0)
            InputError = string.Join("  ·  ", errors.Distinct());
    }

    /// <summary>⤓ Download button — queue everything in the address bar without browsing.</summary>
    [RelayCommand]
    private void DownloadFromAddress() => AddFromInput();

    // ---- downloads ---------------------------------------------------------------------------

    [RelayCommand]
    private void AddFromInput()
    {
        InputError = null;
        var urls = (Browser.AddressText ?? "")
            .Split(new[] { '\r', '\n', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            InputError = "Type or paste a link first, e.g. https://server.com/file.zip";
            return;
        }

        var errors = new List<string>();
        foreach (var raw in urls)
        {
            var url = UrlNormalizer.Normalize(raw);
            if (url is null)
            {
                errors.Add($"'{TrimUrl(raw)}' is not a valid http(s) URL");
                continue;
            }
            if (AddDownload(url, out var error) is null && error is not null)
                errors.Add(error);
        }

        if (errors.Count > 0)
            InputError = string.Join("  ·  ", errors.Distinct());
    }

    public DownloadItemViewModel? AddDownload(string url, out string? error)
    {
        error = null;
        var normalized = UrlNormalizer.Normalize(url);
        if (normalized is null ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            error = $"'{TrimUrl(url)}' is not a valid http(s) URL";
            return null;
        }

        var binds = Kind == TabKind.Combined
            ? AppServices.AllAdapterIPs()
            : AppServices.ResolveBindsForTab(ScopeLabel, new[] { ScopeLabel });
        if (binds.Count == 0)
        {
            error = $"Adapter '{Adapter?.Name ?? ScopeLabel}' is not online";
            return null;
        }

        var scope = new DownloadScope(ScopeLabel, Kind == TabKind.Combined
            ? AppServices.Adapters.Select(a => a.Id).ToList()
            : new[] { ScopeLabel });

        var job = AppServices.Manager.AddDownload(normalized, binds, scope,
            AppServices.Settings.DownloadDirectory);
        SentrySetup.TrackDownloadStarted(
            Kind == TabKind.Combined ? "combined" : ScopeLabel, binds.Count);

        var item = new DownloadItemViewModel(job);
        WireItem(item);
        Items.Insert(0, item);
        HasNoItems = Items.Count == 0;
        return item;
    }

    /// <summary>Attaches a crash-recovered job restored by the DownloadManager.</summary>
    public void AddRestoredJob(DownloadJob job)
    {
        var item = new DownloadItemViewModel(job);
        WireItem(item);
        Items.Insert(0, item);
        HasNoItems = Items.Count == 0;
    }

    /// <summary>
    /// Moves an existing item VM (its job keeps running) into this tab — used when a
    /// network goes offline and its tab's downloads are migrated to the Combined tab.
    /// </summary>
    public void AdoptItem(DownloadItemViewModel item)
    {
        WireItem(item);
        Items.Insert(0, item);
        HasNoItems = Items.Count == 0;
    }

    private void WireItem(DownloadItemViewModel item)
    {
        item.RestartRequested += OnItemRestart;
        item.RemoveRequested += OnItemRemove;
        item.Diagnostic += (kind, message) => Diagnostic?.Invoke(kind, message);
    }

    private void OnItemRestart(DownloadItemViewModel item)
    {
        var url = item.Url;
        if (item.Job.Status is DownloadStatus.Failed or DownloadStatus.Canceled)
        {
            // Reuse the same file name for the fresh attempt instead of piling up "(1)" copies.
            item.Job.CleanupPartial();
            Items.Remove(item);
            HasNoItems = Items.Count == 0;
        }
        AddDownload(url, out _);
    }

    private void OnItemRemove(DownloadItemViewModel item)
    {
        Items.Remove(item);
        HasNoItems = Items.Count == 0;
    }

    // ---- bulk tab actions ---------------------------------------------------------------------

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var item in Items.Where(i => i.CanPause).ToList())
            item.PauseCommand.Execute(null);
    }

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var item in Items.Where(i => i.Job.Status is DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed).ToList())
            item.ResumeCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.CanRemove).ToList())
            Items.Remove(item);
        HasNoItems = Items.Count == 0;
    }

    [RelayCommand]
    private void RetryFailed()
    {
        foreach (var item in Items.Where(i => i.Job.Status == DownloadStatus.Failed).ToList())
            item.ResumeCommand.Execute(null);
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        try
        {
            var top = AppServices.MainWindow;
            if (top?.Clipboard is null) return;
            var text = await top.Clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                Browser.AddressText = text.Trim();
        }
        catch { }
    }

    // ---- history -----------------------------------------------------------------------------

    [RelayCommand]
    private void ClearHistory() => AppServices.History.Clear();

    public void ReloadHistory()
    {
        HistoryEntries.Clear();
        foreach (var e in AppServices.History.Entries)
            HistoryEntries.Add(new HistoryItemViewModel(e));
    }

    /// <summary>Called by MainViewModel when a job reaches a terminal state.</summary>
    public void RecordHistory(DownloadJob job, string status, string? message)
    {
        AppServices.History.Add(new HistoryEntry
        {
            Url = job.Url,
            FileName = Path.GetFileName(job.FilePath),
            FilePath = job.FilePath,
            SizeBytes = job.TotalBytes > 0 ? job.TotalBytes : job.ReceivedBytes,
            Status = status,
            NetworkScope = job.Scope.Label == "All" ? "Combined" : job.Scope.Label,
            Message = message
        });
    }

    // ---- speed sampling ----------------------------------------------------------------------

    private void SampleTick()
    {
        double sum = 0;
        int active = 0;
        var perIp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item.IsActive)
            {
                sum += item.SpeedBps;
                active++;
            }

            // Aggregate per-adapter speeds across this tab's jobs (slot -> adapter IP).
            var job = item.Job;
            var speeds = job.PerAdapterSpeedBps;
            for (int slot = 0; slot < speeds.Count && slot < job.BindAddresses.Count; slot++)
            {
                var ip = job.BindAddresses[slot].ToString();
                perIp[ip] = perIp.GetValueOrDefault(ip) + speeds[slot];
            }
        }
        TabSpeedBps = sum;
        TabSpeedText = Formatter.Speed(sum);
        ItemSummaryText = $"{active} active · {Items.Count} total";
        UpdateAdapterChips(perIp);
        SpeedSampled?.Invoke(sum);
    }

    private void UpdateAdapterChips(Dictionary<string, double> perIp)
    {
        foreach (var chip in AdapterSpeedChips)
            chip.SpeedText = Formatter.Speed(perIp.GetValueOrDefault(chip.IpAddress));
    }

    // ---- per-adapter speed test ---------------------------------------------------------------

    private bool CanRunSpeedTest() => !IsSpeedTesting;

    [RelayCommand(CanExecute = nameof(CanRunSpeedTest))]
    private async Task RunSpeedTest()
    {
        var adapters = TabAdapters();
        if (adapters.Count == 0)
        {
            SpeedTestText = "No adapter online to test.";
            return;
        }

        IsSpeedTesting = true;
        RunSpeedTestCommand.NotifyCanExecuteChanged();
        try
        {
            var results = new List<string>();
            foreach (var adapter in adapters)
            {
                SpeedTestText = $"Testing {adapter.Name}…";
                var result = await Task.Run(() => SpeedTestService.RunAsync(
                    adapter.Name,
                    System.Net.IPAddress.Parse(adapter.IPv4Address),
                    AppServices.Settings.SpeedTestUrl,
                    SpeedTestService.MaxBytes,
                    CancellationToken.None,
                    progress: bytes => Dispatcher.UIThread.Post(() =>
                    {
                        if (IsSpeedTesting)
                            SpeedTestText = $"Testing {adapter.Name}… {Formatter.Bytes(bytes)}";
                    }))).ConfigureAwait(true);

                if (result.Success)
                {
                    results.Add($"{result.AdapterName}: {result.MegabitsPerSecond:0.#} Mbps");
                    SentrySetup.TrackSpeedTest(adapter.Name, result.MegabitsPerSecond, true);
                }
                else
                {
                    results.Add($"{result.AdapterName}: {result.Error}");
                    SentrySetup.TrackSpeedTest(adapter.Name, 0, false);
                    SentrySetup.CaptureMessage($"Speed test failed on {adapter.Name}: {result.Error}", "speedtest");
                }
            }
            SpeedTestText = string.Join("   ·   ", results);
        }
        finally
        {
            IsSpeedTesting = false;
            RunSpeedTestCommand.NotifyCanExecuteChanged();
        }
    }

    public void Shutdown() => _sampleTimer.IsEnabled = false;

    private static string TrimUrl(string url) =>
        url.Length <= 42 ? url : url[..39] + "…";
}

/// <summary>One per-adapter live speed pill shown next to the tab speed graph.</summary>
public partial class AdapterSpeedChipViewModel : ObservableObject
{
    public AdapterSpeedChipViewModel(NetworkAdapterInfo info, string colorHex = "#22D3EE")
    {
        Name = info.Name.Length > 16 ? info.Name[..16] : info.Name;
        TypeName = info.TypeName;
        IpAddress = info.IPv4Address;
        ColorHex = colorHex;
        DotBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        SpeedText = "0 B/s";
    }

    public string Name { get; }
    public string TypeName { get; }
    public string IpAddress { get; }
    public string ColorHex { get; }
    public Avalonia.Media.IBrush DotBrush { get; }

    [ObservableProperty] private string speedText;
}

/// <summary>Row in the History tab.</summary>
public partial class HistoryItemViewModel : ObservableObject
{
    public HistoryItemViewModel(HistoryEntry entry)
    {
        Entry = entry;
        FileName = entry.FileName;
        StatusText = entry.Status;
        Scope = entry.NetworkScope;
        SizeText = entry.SizeBytes > 0 ? Formatter.Bytes(entry.SizeBytes) : "—";
        WhenText = Formatter.When(entry.FinishedUtc);
        Message = entry.Message ?? "";
        StatusBrush = new SolidColorBrush(Color.Parse(entry.Status switch
        {
            "Completed" => "#34D399",
            "Failed" => "#F87171",
            _ => "#9CA3AF"
        }));
    }

    public HistoryEntry Entry { get; }
    public string FileName { get; }
    public string StatusText { get; }
    public string Scope { get; }
    public string SizeText { get; }
    public string WhenText { get; }
    public string Message { get; }
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public IBrush StatusBrush { get; }
}
