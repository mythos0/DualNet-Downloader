using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Models;
using DualNet.Core.Storage;
using DualNet.App.Views;

namespace DualNet.App.ViewModels;

/// <summary>View-model for one rendered block of a web page.</summary>
public sealed partial class PageBlockViewModel : ObservableObject
{
    private readonly ImageBlock? _image;

    public PageBlockViewModel(PageBlock block)
    {
        Kind = block switch
        {
            HeadingBlock => "heading",
            BulletBlock => "bullet",
            QuoteBlock => "quote",
            PreBlock => "pre",
            ImageBlock => "image",
            DividerBlock => "divider",
            _ => "para"
        };
        switch (block)
        {
            case HeadingBlock h:
                Level = h.Level;
                Text = h.Text;
                break;
            case ParagraphBlock p:
                Text = p.Text;
                Links = p.Links;
                break;
            case BulletBlock b: Text = b.Text; break;
            case QuoteBlock q: Text = q.Text; break;
            case PreBlock pr: Text = pr.Text; break;
            case ImageBlock img:
                _image = img;
                Text = img.Alt;
                ImageUrl = img.Url;
                break;
        }
    }

    public string Kind { get; }
    public int Level { get; }
    public string Text { get; } = "";
    public IReadOnlyList<BrowserLink> Links { get; } = Array.Empty<BrowserLink>();
    public string ImageUrl { get; } = "";
    public bool HasLinks => Links.Count > 0;
    public double HeadingFontSize => Level switch { 1 => 25, 2 => 20, _ => 16.5 };

    [ObservableProperty] private object? _imageSource;
    [ObservableProperty] private bool _hasImage;

    /// <summary>Fetches and decodes the image through the tab's network (best effort).</summary>
    public void LoadImage(Func<System.Net.IPAddress> bind, string referer)
    {
        if (_image is null || HasImage) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = DualNet.Core.Net.HttpConnectionFactory.Create(bind());
                using var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, _image.Url);
                if (!string.IsNullOrWhiteSpace(referer))
                    request.Headers.Referrer = new Uri(referer);
                using var response = await client.SendAsync(request,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;
                var length = response.Content.Headers.ContentLength ?? 0;
                if (length > 4 * 1024 * 1024) return;
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var decoded = Bitmap.DecodeToWidth(stream, 640);
                var display = decoded;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        ImageSource = display;
                        HasImage = true;
                    }
                    catch { }
                });
            }
            catch { /* image preview is best effort */ }
        });
    }
}

/// <summary>
/// One tab's embedded browser: address bar navigation through the tab's own network,
/// reader-view page rendering, link/download panel, bookmarks and visit history.
/// Page fetches use adapter-bound sockets, so the Ethernet tab browses over the cable
/// and the Wi-Fi tab over the radio — exactly like its downloads.
/// </summary>
public partial class BrowserViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<System.Net.IPAddress>> _binds;
    private readonly Action<string> _startDownload;
    private readonly BrowserStore? _store;

    public ObservableCollection<PageBlockViewModel> Blocks { get; } = new();
    public ObservableCollection<BrowserLink> FilteredLinks { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();
    public ObservableCollection<BookmarkEntry> BookmarkList { get; } = new();
    public ObservableCollection<VisitedEntry> VisitedList { get; } = new();
    public ObservableCollection<string> RecentUrls { get; } = new();

    [ObservableProperty] private string addressText = "";
    [ObservableProperty] private string pageTitle = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusText = "Type a link or a search, press Enter.";
    [ObservableProperty] private string? pageError;
    [ObservableProperty] private bool hasPage;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool isBookmarked;
    [ObservableProperty] private string linkSearchText = "";
    [ObservableProperty] private int linkFilterIndex;   // 0 = all links, 1 = files only
    [ObservableProperty] private int totalLinkCount;
    [ObservableProperty] private int fileLinkCount;
    [ObservableProperty] private bool webActive;        // real Chromium engine attached + ready
    [ObservableProperty] private bool webFailed;        // WebView2 init failed — reader fallback active
    [ObservableProperty] private bool readerOverride;   // user asked for the reader view of the current page

    /// <summary>The real browser surface, attached by the view when supported (Windows + WebView2).</summary>
    public RealWebView? Web { get; private set; }

    /// <summary>True when the tab should create the real browser engine (Windows + setting + not failed).</summary>
    public bool UseRealBrowserEnabled =>
        AppServices.Settings.UseRealBrowser && RealWebView.IsSupported && !WebFailed;

    public bool ShowWeb => WebActive && !ReaderOverride;
    public bool HasPageError => PageError is not null;
    public bool ShowHome => !IsLoading && !HasPage && !HasPageError && (!ShowWeb || (AddressText ?? "").Trim().Length == 0);
    public bool ShowReaderPage => HasPage && (!ShowWeb || ReaderOverride);
    public bool ShowError => HasPageError && (!ShowWeb || ReaderOverride);
    public bool HasRecentUrls => RecentUrls.Count > 0;
    public bool ShowLinksPanel => HasPage && FilteredLinks.Count > 0;
    public bool HasBookmarks => BookmarkList.Count > 0;
    public bool HasVisited => VisitedList.Count > 0;

    partial void OnLinkSearchTextChanged(string value) => ApplyLinkFilter();
    partial void OnLinkFilterIndexChanged(int value) => ApplyLinkFilter();

    partial void OnPageErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPageError));
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowReaderPage));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowHome));
    partial void OnHasPageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLinksPanel));
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowReaderPage));
    }

    partial void OnWebActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWeb));
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowReaderPage));
        OnPropertyChanged(nameof(ShowError));
        RefreshNav();
    }

    partial void OnReaderOverrideChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWeb));
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowReaderPage));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnAddressTextChanged(string value) => OnPropertyChanged(nameof(ShowHome));

    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();
    private CancellationTokenSource? _loadCts;
    private int _loadSeq;               // invalidates stale results when a newer navigation starts
    private string? _pendingWebUrl;     // typed/navigated before the Chromium engine finished attaching
    private const int MaxStackDepth = 60;

    public BrowserViewModel(Func<IReadOnlyList<System.Net.IPAddress>> binds,
                            Action<string> startDownload, BrowserStore? store)
    {
        _binds = binds;
        _startDownload = startDownload;
        _store = store;
        RefreshChrome();
    }

    // ---- real browser engine (WebView2) ------------------------------------------------------

    /// <summary>Called by the view once it knows this tab should host the real engine.</summary>
    public void AttachWeb(RealWebView web)
    {
        if (Web is not null)
            return;
        Web = web;

        web.Ready += () => Dispatcher.UIThread.Post(() =>
        {
            WebActive = true;
            if (!string.IsNullOrWhiteSpace(_pendingWebUrl))
            {
                var url = _pendingWebUrl;
                _pendingWebUrl = null;
                web.Navigate(url);
            }
            else if (web.ReadyCount > 1)
            {
                // Re-attached after a tab switch rebuilt the view: the native engine is a
                // fresh session, so reload the page this tab was showing.
                var current = (AddressText ?? "").Trim();
                if (current.Length > 0 && !current.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    web.Navigate(current);
            }
        });

        web.NavigationStarting += url => Dispatcher.UIThread.Post(() =>
        {
            // The engine started loading — mirror it exactly like a typed navigation.
            var current = (AddressText ?? "").Trim();
            if (current.Length > 0 && current != url && (_backStack.Count == 0 || _backStack[^1] != current))
            {
                _backStack.Add(current);
                while (_backStack.Count > MaxStackDepth)
                    _backStack.RemoveAt(0);
            }
            AddressText = url;
            PageError = null;
            HasPage = false;
            ReaderOverride = false;
            IsLoading = true;
            StatusText = $"Loading {TrimForStatus(url)}…";
        });

        web.NavigationCompleted += url => Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;
            PageError = null;
            StatusText = PageTitle.Length > 0 ? PageTitle : TrimForStatus(url);
            if (url.Length > 0 && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                _store?.AddVisited(url, PageTitle.Length > 0 ? PageTitle : url);
                RefreshChrome();
            }
        });

        web.TitleChanged += title => Dispatcher.UIThread.Post(() => PageTitle = title ?? "");
        web.HistoryChanged += () => Dispatcher.UIThread.Post(RefreshNav);

        web.DownloadStarting += (url, name) => Dispatcher.UIThread.Post(() =>
        {
            // Real-browser downloads ride the same dual-NIC segment engine as everything else.
            _startDownload(url);
            StatusText = $"⤓ Download started: {(name.Length > 0 ? name : TrimForStatus(url))}";
            _store?.AddVisited(url, name.Length > 0 ? name : url);
            RefreshChrome();
        });

        web.InitializationFailed += reason => Dispatcher.UIThread.Post(() =>
        {
            WebActive = false;
            WebFailed = true;
            IsLoading = false;
            StatusText = "Chromium engine unavailable — using the built-in reader view. " +
                         "(Install the Evergreen WebView2 Runtime for full browsing.)";
        });
    }

    private bool WebNavigates => UseRealBrowserEnabled && Web is not null;

    [RelayCommand]
    private Task ToggleReaderView()
    {
        if (ReaderOverride)
        {
            ReaderOverride = false;
            return Task.CompletedTask;
        }
        var url = (AddressText ?? "").Trim();
        if (url.Length == 0)
            return Task.CompletedTask;
        ReaderOverride = true;
        return FetchReaderAsync(url);
    }

    // ---- navigation --------------------------------------------------------------------------

    [RelayCommand]
    public Task GoAsync(string? input) => GoCore(Resolve(input));

    [RelayCommand]
    private Task GoBack()
    {
        if (WebNavigates && Web!.IsReady && Web.CanGoBack)
        {
            Web.GoBack();
            return Task.CompletedTask;
        }
        if (_backStack.Count == 0) return Task.CompletedTask;
        var current = (AddressText ?? "").Trim();
        if (current.Length > 0)
            _forwardStack.Add(current);
        var url = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        return GoCore(url, pushCurrent: false);
    }

    [RelayCommand]
    private Task GoForward()
    {
        if (WebNavigates && Web!.IsReady && Web.CanGoForward)
        {
            Web.GoForward();
            return Task.CompletedTask;
        }
        if (_forwardStack.Count == 0) return Task.CompletedTask;
        var current = (AddressText ?? "").Trim();
        if (current.Length > 0)
            _backStack.Add(current);
        var url = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        return GoCore(url, pushCurrent: false);
    }

    [RelayCommand]
    private Task Reload()
    {
        var url = (AddressText ?? "").Trim();
        if (url.Length == 0) return Task.CompletedTask;
        if (WebNavigates && Web!.IsReady)
        {
            Web.Reload();
            return Task.CompletedTask;
        }
        return GoCore(url, pushCurrent: false);
    }

    [RelayCommand]
    private void Stop()
    {
        try { _loadCts?.Cancel(); } catch { }
        if (WebNavigates)
            Web!.Stop();
    }

    [RelayCommand]
    private void GoHome()
    {
        var current = (AddressText ?? "").Trim();
        if (WebNavigates)
        {
            if (current.Length > 0)
                _backStack.Add(current);
            AddressText = "";
            PageTitle = "";
            PageError = null;
            HasPage = false;
            ReaderOverride = false;
            IsLoading = false;
            _pendingWebUrl = null;
            try { _loadCts?.Cancel(); } catch { }
            Web!.Navigate("about:blank");
            StatusText = "Type a link or a search, press Enter.";
            RefreshNav();
            RefreshChrome();
            return;
        }

        if (current.Length > 0)
            _backStack.Add(current);
        _forwardStack.Clear();
        ReaderOverride = false;
        _loadSeq++;   // invalidate any in-flight load result
        try { _loadCts?.Cancel(); } catch { }
        AddressText = "";
        PageTitle = "";
        IsLoading = false;
        HasPage = false;
        PageError = null;
        Blocks.Clear();
        FilteredLinks.Clear();
        StatusText = "Type a link or a search, press Enter.";
        RefreshNav();
        RefreshChrome();
    }

    private string Resolve(string? input) => BrowserService.ResolveInput(input ?? "", AppServices.Settings.SearchEngine);

    /// <summary>The navigation entry point used by the rest of the app (clipboard, API, drag & drop).</summary>
    public async Task Go(string? input) => await GoCore(Resolve(input)).ConfigureAwait(false);

    private async Task GoCore(string url, bool pushCurrent = true)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            return;

        if (pushCurrent)
        {
            var current = (AddressText ?? "").Trim();
            if (current.Length > 0 && (_backStack.Count == 0 || _backStack[^1] != current))
            {
                _backStack.Add(current);
                while (_backStack.Count > MaxStackDepth)
                    _backStack.RemoveAt(0);
            }
            _forwardStack.Clear();
        }
        RefreshNav();

        // Real browser engine: Chromium does the loading; we only mirror state.
        if (WebNavigates)
        {
            ReaderOverride = false;
            AddressText = url;
            PageError = null;
            HasPage = false;
            IsLoading = true;
            StatusText = $"Loading {TrimForStatus(url)}…";
            if (Web!.IsReady)
                Web.Navigate(url);
            else
                _pendingWebUrl = url;   // engine still attaching — fires when Ready
            return;
        }

        await FetchReaderAsync(url).ConfigureAwait(false);
    }

    /// <summary>Reader-mode fetch: used when the real engine is off/unavailable, or on demand.</summary>
    private async Task FetchReaderAsync(string url)
    {
        try { _loadCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        int seq = ++_loadSeq;

        // Browsers show the target address immediately; the final URL (after any
        // redirect) replaces it when the response arrives.
        AddressText = url;
        PageError = null;
        IsLoading = true;
        StatusText = $"Loading {TrimForStatus(url)}…";

        var result = await BrowserService.FetchAsync(url, _binds(), cts.Token).ConfigureAwait(false);
        if (seq != _loadSeq)
            return;   // superseded by a newer navigation or GoHome
        IsLoading = false;

        await Dispatcher.UIThread.InvokeAsync(() => ApplyResult(result));
    }

    private void ApplyResult(BrowserResult result)
    {
        if (result.Error is { } error)
        {
            PageError = error;
            StatusText = error;
            HasPage = false;
            return;
        }

        AddressText = result.FinalUrl.Length > 0 ? result.FinalUrl : result.RequestedUrl;
        PageError = null;

        if (result.IsDownload)
        {
            // The "page" is actually a file: hand it straight to the download engine
            // on this tab's adapters and stay on the current page.
            _startDownload(result.FinalUrl);
            PageTitle = "";
            StatusText = $"⤓ Download started: {result.DownloadFileName}" +
                         (result.DownloadSize is { } size ? $" ({Formatter.Bytes(size)})" : "");
            _store?.AddVisited(result.FinalUrl, result.DownloadFileName ?? result.FinalUrl);
            RefreshChrome();
            return;
        }

        var page = result.Page!;
        PageTitle = page.Title;
        Blocks.Clear();
        foreach (var block in page.Blocks)
            Blocks.Add(new PageBlockViewModel(block));

        LoadImages(result.FinalUrl);

        _allLinks = page.Links.ToList();
        TotalLinkCount = _allLinks.Count;
        FileLinkCount = _allLinks.Count(l => l.IsFile);
        ApplyLinkFilter();

        HasPage = true;
        StatusText = $"{page.Title}  ·  {page.Links.Count} link(s), {FileLinkCount} file(s)";
        _store?.AddVisited(page.Url, page.Title);
        IsBookmarked = _store?.IsBookmarked(page.Url) ?? false;
        RefreshChrome();
    }

    private void LoadImages(string referer)
    {
        if (_binds() is not { Count: > 0 } binds)
            return;
        var bind = binds[0];
        int loaded = 0;
        foreach (var block in Blocks)
        {
            if (block.Kind != "image") continue;
            if (loaded++ >= 12) break;
            block.LoadImage(() => bind, referer);
        }
    }

    // ---- links panel -------------------------------------------------------------------------

    [RelayCommand]
    private Task OpenLink(BrowserLink? link) =>
        link is null ? Task.CompletedTask : GoCore(link.Url);

    [RelayCommand]
    private void DownloadLink(BrowserLink? link)
    {
        if (link is null) return;
        _startDownload(link.Url);
        StatusText = $"⤓ Download started: {(link.FileName.Length > 0 ? link.FileName : link.Url)}";
    }

    private void ApplyLinkFilter()
    {
        FilteredLinks.Clear();
        foreach (var link in _allLinks)
        {
            bool match = LinkFilterIndex == 0 || link.IsFile;
            if (match && LinkSearchText.Length > 0)
            {
                match = link.Text.Contains(LinkSearchText, StringComparison.OrdinalIgnoreCase) ||
                        link.Url.Contains(LinkSearchText, StringComparison.OrdinalIgnoreCase) ||
                        link.Host.Contains(LinkSearchText, StringComparison.OrdinalIgnoreCase);
            }
            if (match)
                FilteredLinks.Add(link);
        }
        OnPropertyChanged(nameof(ShowLinksPanel));
    }

    private List<BrowserLink> _allLinks = new();

    // ---- suggestions -------------------------------------------------------------------------

    public void UpdateSuggestions(string? text)
    {
        Suggestions.Clear();
        if (_store is null || string.IsNullOrWhiteSpace(text) || text.Length < 2)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = _store.Visited.Select(v => v.Url)
            .Concat(_store.Bookmarks.Select(b => b.Url));
        foreach (var url in candidates)
        {
            if (url.Contains(text, StringComparison.OrdinalIgnoreCase) && seen.Add(url))
            {
                Suggestions.Add(url);
                if (Suggestions.Count >= 8)
                    break;
            }
        }
    }

    // ---- bookmarks / history chrome ----------------------------------------------------------

    [RelayCommand]
    private void ToggleBookmark()
    {
        var url = (AddressText ?? "").Trim();
        if (url.Length == 0 || _store is null) return;
        bool added = _store.ToggleBookmark(url, PageTitle.Length > 0 ? PageTitle : url);
        IsBookmarked = added;
        StatusText = added ? "★ Bookmarked this page." : "Bookmark removed.";
        RefreshChrome();
    }

    [RelayCommand]
    private Task OpenBookmark(BookmarkEntry? entry) =>
        entry is null ? Task.CompletedTask : GoCore(entry.Url);

    [RelayCommand]
    private void RemoveBookmark(BookmarkEntry? entry)
    {
        if (entry is null || _store is null) return;
        _store.RemoveBookmark(entry.Url);
        RefreshChrome();
        IsBookmarked = _store.IsBookmarked((AddressText ?? "").Trim());
    }

    [RelayCommand]
    private Task OpenVisited(VisitedEntry? entry) =>
        entry is null ? Task.CompletedTask : GoCore(entry.Url);

    [RelayCommand]
    private void ClearBrowserHistory()
    {
        _store?.ClearVisited();
        RefreshChrome();
        StatusText = "Browser history cleared.";
    }

    [RelayCommand]
    private void OpenExternal()
    {
        var url = (AddressText ?? "").Trim();
        if (url.Length == 0) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the system browser: {ex.Message}";
        }
    }

    private void RefreshChrome()
    {
        BookmarkList.Clear();
        if (_store is not null)
            foreach (var b in _store.Bookmarks)
                BookmarkList.Add(b);

        VisitedList.Clear();
        if (_store is not null)
            foreach (var v in _store.Visited.Take(20))
                VisitedList.Add(v);

        RecentUrls.Clear();
        if (_store is not null)
            foreach (var v in _store.Visited.Take(6))
                RecentUrls.Add(v.Url);

        OnPropertyChanged(nameof(HasRecentUrls));
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasVisited));
        IsBookmarked = _store?.IsBookmarked((AddressText ?? "").Trim()) ?? false;
    }

    private void RefreshNav()
    {
        if (WebNavigates && Web!.IsReady)
        {
            CanGoBack = Web.CanGoBack;
            CanGoForward = Web.CanGoForward;
            return;
        }
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    private static string TrimForStatus(string url) => url.Length <= 52 ? url : url[..49] + "…";
}
