using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public enum TabKind { Combined, SingleAdapter, History }

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

    [ObservableProperty] private string urlInput = "";
    [ObservableProperty] private string? inputError;
    [ObservableProperty] private double tabSpeedBps;
    [ObservableProperty] private string tabSpeedText = "0 B/s";
    [ObservableProperty] private string itemSummaryText = "0 active";
    [ObservableProperty] private bool hasNoItems = true;

    public bool IsDownloadTab => Kind != TabKind.History;
    public bool IsHistoryTab => Kind == TabKind.History;
    public bool IsCombined => Kind == TabKind.Combined;
    public string ScopeHint => Kind switch
    {
        TabKind.Combined => "Uses every active adapter — Ethernet + Wi-Fi together",
        TabKind.SingleAdapter => $"Uses {Adapter?.Name} ({Adapter?.IPv4Address}) only",
        _ => ""
    };

    /// <summary>Raised every sampling tick with the tab's aggregate speed (bytes/s).</summary>
    public event Action<double>? SpeedSampled;

    private readonly DispatcherTimer _sampleTimer;

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
            InputError = "Paste a file URL first (http/https).";
            return;
        }

        var errors = new List<string>();
        foreach (var url in urls)
        {
            if (AddDownload(url, out var error) is null && error is not null)
                errors.Add(error);
        }

        UrlInput = "";
        if (errors.Count > 0)
            InputError = string.Join("  ·  ", errors.Distinct());
    }

    public DownloadItemViewModel? AddDownload(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
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

        var job = AppServices.Manager.AddDownload(url, binds, scope,
            AppServices.Settings.DownloadDirectory);
        var item = new DownloadItemViewModel(job);
        Items.Insert(0, item);
        HasNoItems = Items.Count == 0;
        return item;
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
