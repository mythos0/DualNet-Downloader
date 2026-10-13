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
/// One browser-style tab. The address bar keeps back/forward navigation history
/// like a web browser — "navigating" to a URL starts the download. Each tab only
/// uses the network adapter(s) it was created for (Ethernet tab → cable, Wi-Fi tab → radio).
/// </summary>
public partial class DownloadTabViewModel : ObservableObject
{
    public string Title { get; }
    public string Glyph { get; }
    public IBrush AccentBrush { get; }
    public TabKind Kind { get; }
    public NetworkAdapterInfo? Adapter { get; }
    public string ScopeLabel { get; }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ObservableCollection<HistoryItemViewModel> HistoryEntries { get; } = new();
    public ObservableCollection<string> RecentUrls { get; } = new();

    [ObservableProperty] private string urlInput = "";
    [ObservableProperty] private string? inputError;
    [ObservableProperty] private double tabSpeedBps;
    [ObservableProperty] private string tabSpeedText = "0 B/s";
    [ObservableProperty] private string itemSummaryText = "0 active";
    [ObservableProperty] private bool hasNoItems = true;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;

    partial void OnHasNoItemsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowHistoryEmpty));
    }

    public bool IsDownloadTab => Kind != TabKind.History;
    public bool IsHistoryTab => Kind == TabKind.History;
    public bool IsCombined => Kind == TabKind.Combined;
    public bool ShowHome => IsDownloadTab && HasNoItems;
    public bool ShowHistoryEmpty => IsHistoryTab && HasNoItems;
    public bool HasRecentUrls => RecentUrls.Count > 0;
    public string ScopeHint => Kind switch
    {
        TabKind.Combined => "Uses every active adapter — Ethernet + Wi-Fi together",
        TabKind.SingleAdapter => $"Uses {Adapter?.Name} ({Adapter?.IPv4Address}) only",
        _ => ""
    };

    /// <summary>Raised every sampling tick with the tab's aggregate speed (bytes/s).</summary>
    public event Action<double>? SpeedSampled;

    private readonly DispatcherTimer _sampleTimer;
    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();
    private const int MaxHistoryDepth = 50;

    public DownloadTabViewModel(TabKind kind, NetworkAdapterInfo? adapter = null)
    {
        Kind = kind;
        Adapter = adapter;
        (Glyph, Title, AccentBrush, ScopeLabel) = kind switch
        {
            TabKind.Combined => ("⚡", "Combined", new SolidColorBrush(Color.Parse("#22D3EE")), "All"),
            TabKind.SingleAdapter when adapter is { } a => (
                a.TypeName == "Wi-Fi" ? "📶" : "🔌",
                a.Name.Length > 22 ? a.Name[..22] : a.Name,
                new SolidColorBrush(Color.Parse(a.TypeName == "Wi-Fi" ? "#B388FF" : "#4F8CFF")),
                a.Id),
            _ => ("🕘", "History", new SolidColorBrush(Color.Parse("#9CA3AF")), "History")
        };

        if (kind == TabKind.History)
        {
            ReloadHistory();
            AppServices.History.Changed += ReloadHistory;
            _sampleTimer = MakeTimer(false);
            return;
        }

        _sampleTimer = MakeTimer(true);
    }

    private DispatcherTimer MakeTimer(bool enabled)
    {
        var t = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleTick());
        t.IsEnabled = enabled;
        return t;
    }

    // ---- browser-style address bar -----------------------------------------------------------

    [RelayCommand]
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

        // Single-line input may still hold several links separated by spaces or ';'.
        var urls = tokens
            .SelectMany(t => t.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => (raw: t, url: UrlNormalizer.Normalize(t)))
            .ToList();

        if (urls.Any(u => u.url is null))
        {
            InputError = $"'{TrimUrl(urls.First(u => u.url is null).raw)}' is not a valid http(s) link";
            return;
        }

        var normalized = string.Join("  ", urls.Select(u => u.url!));
        PushBackIfAddress();
        _forwardStack.Clear();
        UrlInput = normalized;
        RefreshNav();
        AddFromInput();
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(UrlInput))
            _forwardStack.Add(UrlInput.Trim());
        var url = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        UrlInput = url;
        RefreshNav();
        AddFromInput();
    }

    [RelayCommand]
    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(UrlInput))
            _backStack.Add(UrlInput.Trim());
        var url = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        UrlInput = url;
        RefreshNav();
        AddFromInput();
    }

    [RelayCommand]
    private void Reload()
    {
        var current = (UrlInput ?? "").Trim();
        if (current.Length > 0)
            Navigate(current);
    }

    [RelayCommand]
    private void GoHome()
    {
        PushBackIfAddress();
        _forwardStack.Clear();
        UrlInput = "";
        RefreshNav();
    }

    private void PushBackIfAddress()
    {
        var current = (UrlInput ?? "").Trim();
        if (current.Length == 0) return;
        if (_backStack.Count > 0 && string.Equals(_backStack[^1], current, StringComparison.OrdinalIgnoreCase))
            return;
        _backStack.Add(current);
        while (_backStack.Count > MaxHistoryDepth)
            _backStack.RemoveAt(0);
    }

    private void RefreshNav()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    // ---- downloads ---------------------------------------------------------------------------

    [RelayCommand]
    private void AddFromInput()
    {
        InputError = null;
        var urls = (UrlInput ?? "")
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
        RememberRecent(normalized);
        return item;
    }

    /// <summary>Attaches a crash-recovered job restored by the DownloadManager.</summary>
    public void AddRestoredJob(DownloadJob job)
    {
        var item = new DownloadItemViewModel(job);
        WireItem(item);
        Items.Insert(0, item);
        HasNoItems = Items.Count == 0;
        RememberRecent(job.Url);
    }

    private void WireItem(DownloadItemViewModel item)
    {
        item.RestartRequested += OnItemRestart;
        item.RemoveRequested += OnItemRemove;
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

    private void RememberRecent(string url)
    {
        RecentUrls.Remove(url);
        RecentUrls.Insert(0, url);
        while (RecentUrls.Count > 6)
            RecentUrls.RemoveAt(RecentUrls.Count - 1);
        OnPropertyChanged(nameof(HasRecentUrls));
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
                UrlInput = text.Trim();
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
        foreach (var item in Items)
        {
            if (item.IsActive)
            {
                sum += item.SpeedBps;
                active++;
            }
        }
        TabSpeedBps = sum;
        TabSpeedText = Formatter.Speed(sum);
        ItemSummaryText = $"{active} active · {Items.Count} total";
        SpeedSampled?.Invoke(sum);
    }

    public void Shutdown() => _sampleTimer.IsEnabled = false;

    private static string TrimUrl(string url) =>
        url.Length <= 42 ? url : url[..39] + "…";
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
