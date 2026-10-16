using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DualNet.App.Browser;
using DualNet.App.Services;
using DualNet.Core.Engine;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Models;
using DualNet.Core.Net;
using DualNet.Core.Storage;

namespace DualNet.App.ViewModels;

/// <summary>Central browser + download manager state shared by the chrome shell and internal pages.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _dlTimer;
    private int _nextTabId = 1;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();
    public ObservableCollection<ProxyEntry> WorkingProxies { get; } = new();
    public ObservableCollection<NicChoice> NicChoices { get; } = new();
    public ObservableCollection<BookmarkEntry> BookmarksBarItems { get; } = new();

    public event Action<TabViewModel, BrowserDownloadInfo>? DownloadBubbleRequested;
    public Action? FocusOmniboxRequested;
    public Action? StartFindRequested;
    public event Action? TabsChanged;
    public event Action<string>? StatusShown;

    /// <summary>MainWindow supplies the UserControl factory for internal pages.</summary>
    public Func<TabViewModel, object> InternalPageFactory { get; set; } = _ => new object();

    public MainViewModel()
    {
        NewTabCommand = new RelayCommand(_ => CreateTab());
        NewPrivateTabCommand = new RelayCommand(_ => CreateTab(privateTab: true));
        CloseTabCommand = new RelayCommand(t => CloseTab((TabViewModel)t!));
        ActivateTabCommand = new RelayCommand(t => ActivateTab((TabViewModel)t!));
        ReopenClosedTabCommand = new RelayCommand(_ => ReopenClosedTab());
        BackCommand = new RelayCommand(_ => { var h = ActiveTab?.Host; h?.Browser.BackCommand.Execute(null); });
        ForwardCommand = new RelayCommand(_ => { var h = ActiveTab?.Host; h?.Browser.ForwardCommand.Execute(null); });
        ReloadCommand = new RelayCommand(_ => Reload(ActiveTab));
        StopCommand = new RelayCommand(_ => ActiveTab?.Host?.Browser.StopCommand.Execute(null));
        HomeCommand = new RelayCommand(_ => Navigate(ActiveTab, "dualnet://app/newtab"));
        ToggleBookmarkCommand = new RelayCommand(_ => ToggleBookmark());
        ToggleBookmarksBarCommand = new RelayCommand(_ =>
        {
            AppBootstrapper.SetSetting(s => s.ShowBookmarksBar = !s.ShowBookmarksBar);
            Changed(nameof(BookmarksBarVisible));
            ReloadBookmarksBar();
        });
        ScreenshotCommand = new RelayCommand(_ => _ = TakeScreenshotAsync());
        RefreshProxiesCommand = new RelayCommand(_ => _ = RefreshProxiesAsync(force: true));
        PauseDownloadCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) it.Job.Pause(); });
        ResumeDownloadCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) it.Job.Start(); });
        CancelDownloadCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) it.Job.Cancel(); });
        OpenFileCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) ShellOpen(it.FilePath); });
        OpenFolderCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) ShellOpen(Path.GetDirectoryName(it.FilePath) ?? ""); });
        CopyUrlCommand = new RelayCommand(d => { if (d is DownloadItemViewModel it) Clipboard.SetText(it.Url); });
        RemoveDownloadCommand = new RelayCommand(d =>
        {
            if (d is DownloadItemViewModel it)
            {
                if (it.IsActive) it.Job.Cancel();
                Downloads.Remove(it);
            }
        });
        ClearHistoryCommand = new RelayCommand(_ => AppBootstrapper.BrowserStore.ClearVisited());

        _dlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dlTimer.Tick += (_, _) => RefreshDownloads();
        _dlTimer.Start();

        LoadNicChoices();
        ReloadBookmarksBar();
    }

    // ---------------- tabs ------------------------------------------------------------------

    private TabViewModel? _activeTab;
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            _activeTab = value;
            Changed(nameof(ActiveTab));
            Changed(nameof(OmniboxText));
            Changed(nameof(IsBookmarked));
            Changed(nameof(HasActiveWebTab));
        }
    }

    public string OmniboxText
    {
        get => ActiveTab?.OmniboxText ?? "";
        set { /* two-way shim: actual navigation via NavigateCommand */ }
    }

    public bool BookmarksBarVisible => AppBootstrapper.Settings.ShowBookmarksBar;
    public bool HasActiveWebTab => ActiveTab?.IsWeb == true;

    public bool IsBookmarked
    {
        get
        {
            var url = ActiveTab?.Url ?? "";
            return url.StartsWith("http", StringComparison.Ordinal) &&
                   AppBootstrapper.BrowserStore.IsBookmarked(url);
        }
    }

    public TabViewModel CreateTab(string? url = null, bool background = false, bool privateTab = false)
    {
        var tab = new TabViewModel
        {
            Id = _nextTabId++,
            IsPrivate = privateTab,
            NetScope = AppBootstrapper.Settings.DefaultTabNetScope,
        };
        Tabs.Add(tab);
        if (!background || Tabs.Count == 1)
            ActivateTab(tab);
        else
            TabsChanged?.Invoke();

        if (!string.IsNullOrWhiteSpace(url))
            Navigate(tab, url);
        else
            OpenInternalPage(tab, privateTab ? "dualnet://app/newtab" : "dualnet://app/newtab");
        return tab;
    }

    public void ActivateTab(TabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return;
        foreach (var t in Tabs) t.IsActive = t == tab;
        ActiveTab = tab;
        TabsChanged?.Invoke();
        Changed(nameof(OmniboxText));
        Changed(nameof(IsBookmarked));
    }

    public void CloseTab(TabViewModel tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        var idx = Tabs.IndexOf(tab);
        try { tab.Host?.Dispose(); } catch { }
        tab.Host = null;
        Tabs.Remove(tab);
        tab.IsActive = false;

        // remember for Ctrl+Shift+T (skip private + internal-only tabs)
        var url = tab.Url;
        if (!tab.IsPrivate && url.StartsWith("http", StringComparison.Ordinal))
            AppBootstrapper.Sessions.PushClosedTab(new SessionTab
            {
                Url = url, Title = tab.Title, Proxy = string.IsNullOrEmpty(tab.Proxy) ? null : tab.Proxy,
                NetScope = tab.NetScope,
            });

        if (Tabs.Count == 0)
        {
            CreateTab();
            return;
        }
        if (ActiveTab == tab || ActiveTab is null)
            ActivateTab(Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)]);
        else
            TabsChanged?.Invoke();
        SaveSessionDebounced();
    }

    public void CycleTab(int dir)
    {
        if (Tabs.Count == 0 || ActiveTab is null) return;
        var i = Tabs.IndexOf(ActiveTab);
        ActivateTab(Tabs[(i + dir + Tabs.Count) % Tabs.Count]);
    }

    public void ActivateTabByIndex(int index)
    {
        if (Tabs.Count == 0) return;
        ActivateTab(index == 8 ? Tabs[^1] : Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void ReopenClosedTab()
    {
        var closed = AppBootstrapper.Sessions.PopClosedTab();
        if (closed is null) return;
        var tab = CreateTab(closed.Url);
        tab.Proxy = closed.Proxy ?? "";
        _ = tab.Host?.SetProxyAsync(string.IsNullOrEmpty(closed.Proxy) ? null : closed.Proxy.Split(':')[0],
            string.IsNullOrEmpty(closed.Proxy) ? 0 : int.TryParse(closed.Proxy.Split(':')[1], out var p) ? p : 0);
    }

    // ---------------- navigation --------------------------------------------------------------

    public void Navigate(TabViewModel? tab, string input)
    {
        if (tab is null) return;
        var resolved = BrowserService.ResolveInput(input, AppBootstrapper.Settings.SearchEngine);
        if (resolved.StartsWith("dualnet://", StringComparison.Ordinal))
        {
            OpenInternalPage(tab, resolved);
            return;
        }
        tab.Kind = TabContentKind.Web;
        tab.Url = resolved;
        tab.RefreshDisplay();
        EnsureHost(tab, resolved);
        Changed(nameof(OmniboxText));
        Changed(nameof(HasActiveWebTab));
    }

    public void OpenInternalPage(TabViewModel? tab, string url)
    {
        if (tab is null) return;
        var page = url.Replace("dualnet://app/", "").Replace("dualnet://", "").TrimEnd('/');
        if (page.Length == 0) page = "newtab";
        tab.Kind = TabContentKind.Internal;
        tab.InternalPage = page;
        tab.Url = $"dualnet://app/{page}";
        tab.Title = tab.DisplayTitle;
        tab.RefreshDisplay();
        tab.InternalView ??= InternalPageFactory(tab);
        TabsChanged?.Invoke();
        Changed(nameof(OmniboxText));
        Changed(nameof(HasActiveWebTab));
        SaveSessionDebounced();
    }

    public void OpenPopupAsTab(string url, bool background)
    {
        var tab = CreateTab(url, background);
        if (background) ActivateTab(tab);   // popups usually deserve focus; keep simple
    }

    public void OpenUrlInNewTab(string url) => CreateTab(url);

    private void EnsureHost(TabViewModel tab, string url)
    {
        if (tab.Host is not null)
        {
            tab.Host.Browser.LoadUrl(url);
            return;
        }
        var host = new CefTabHost(tab, this, url);
        tab.Host = host;
        if (tab.Proxy is { Length: > 0 } proxy)
        {
            var parts = proxy.Split(':');
            _ = host.SetProxyAsync(parts[0], int.TryParse(parts.Skip(1).FirstOrDefault(), out var p) ? p : 0);
        }
        TabsChanged?.Invoke();
    }

    public void Reload(TabViewModel? tab)
    {
        if (tab is null) return;
        if (tab.Kind == TabContentKind.Internal) { TabsChanged?.Invoke(); return; }
        tab.Host?.Browser.ReloadCommand.Execute(null);
    }

    // browser events forwarded by CefTabHost --------------------------------------------------

    public void OnTabAddressChanged(TabViewModel tab, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        tab.Url = address;
        tab.Kind = TabContentKind.Web;
        tab.FaviconUrl = HostIconFor(address);
        tab.RefreshDisplay();
        if (tab == ActiveTab) { Changed(nameof(OmniboxText)); Changed(nameof(IsBookmarked)); }
    }

    public void OnTabTitleChanged(TabViewModel tab, string title)
    {
        if (!string.IsNullOrWhiteSpace(title)) tab.Title = title;
        tab.RefreshDisplay();
    }

    public void OnTabLoadingChanged(TabViewModel tab, bool loading, bool canBack, bool canFwd)
    {
        tab.Loading = loading;
        tab.CanBack = canBack;
        tab.CanFwd = canFwd;
        if (tab == ActiveTab) TabsChanged?.Invoke();
    }

    public void OnTabLoaded(TabViewModel tab, string url)
    {
        if (url.StartsWith("http", StringComparison.Ordinal))
        {
            AppBootstrapper.BrowserStore.AddVisited(url, tab.Title);
            SaveSessionDebounced();
        }
    }

    public void OnTabStatus(TabViewModel tab, string message)
    {
        if (tab == ActiveTab) StatusShown?.Invoke(message);
    }

    public static string HostIconFor(string url)
    {
        try
        {
            var u = new Uri(url);
            return $"https://icons.duckduckgo.com/ip3/{u.Host}.ico";
        }
        catch { return ""; }
    }

    public void PostToUi(Action action) => _ui.BeginInvoke(action);

    // ---------------- bookmarks ----------------------------------------------------------------

    public void ReloadBookmarksBar()
    {
        BookmarksBarItems.Clear();
        foreach (var b in AppBootstrapper.BrowserStore.Bookmarks.Take(20))
            BookmarksBarItems.Add(b);
        Changed(nameof(BookmarksBarVisible));
    }

    public void ToggleBookmark()
    {
        var tab = ActiveTab;
        if (tab is null || !tab.Url.StartsWith("http", StringComparison.Ordinal)) return;
        AppBootstrapper.BrowserStore.ToggleBookmark(tab.Url, string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title);
        Changed(nameof(IsBookmarked));
        ReloadBookmarksBar();
        TabsChanged?.Invoke();
    }

    // ---------------- downloads ------------------------------------------------------------------

    /// <summary>Browser-initiated download → show the NIC/proxy bubble before starting the engine.</summary>
    public void BeginBrowserDownload(TabViewModel tab, BrowserDownloadInfo info)
    {
        _pendingInfo = info;
        _pendingTab = tab;
        DownloadBubbleRequested?.Invoke(tab, info);
        DualNet.Core.SentrySetup.CaptureMessage($"browser-download-captured tab={tab.Id} {info.Url}", "info");
    }

    private BrowserDownloadInfo? _pendingInfo;
    private TabViewModel? _pendingTab;

    public BrowserDownloadInfo? PendingDownloadInfo => _pendingInfo;
    public TabViewModel? PendingDownloadTab => _pendingTab;

    public void DismissPendingDownload()
    {
        _pendingInfo = null;
        _pendingTab = null;
    }

    /// <summary>Starts a job in the DualNet engine with the user's NIC + proxy selection.</summary>
    public DownloadItemViewModel? EnqueueDownload(
        string url, string fileName, string scopeId, string? proxy, string? referer = null,
        string? cookies = null, string? userAgent = null, string? targetDir = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Downloads.Any(d => d.Url == url && d.IsActive)) return null;   // duplicate guard

            var binds = AppBootstrapper.ResolveBinds(scopeId);
            if (binds.Count == 0) binds = new[] { System.Net.IPAddress.Loopback };
            var scope = new DownloadScope(
                scopeId == "all" ? "Combined" : scopeId,
                scopeId == "all" ? AppBootstrapper.OnlineAdapters().Select(a => a.Id).ToList() : new List<string> { scopeId });

            var options = AppBootstrapper.Downloads!.OptionsFromSettings();
            if (!string.IsNullOrWhiteSpace(proxy) && proxy!.Contains(':'))
            {
                var parts = proxy.Split(':');
                options.ProxyHost = parts[0];
                options.ProxyPort = int.TryParse(parts[1], out var pp) ? pp : 0;
            }
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(cookies)) headers["Cookie"] = cookies!;
            if (!string.IsNullOrWhiteSpace(referer)) headers["Referer"] = referer!;
            if (!string.IsNullOrWhiteSpace(userAgent)) headers["User-Agent"] = userAgent!;
            if (headers.Count > 0) options.ExtraHeaders = headers;

            var dir = targetDir ?? AppBootstrapper.EffectiveDownloadDir();
            var job = AppBootstrapper.Downloads!.AddDownload(url, binds, scope, dir, options);
            var item = new DownloadItemViewModel(job);
            Downloads.Insert(0, item);
            DualNet.Core.SentrySetup.TrackDownloadStarted(scope.Label, binds.Count);
            return item;
        }
        catch (Exception ex)
        {
            DualNet.Core.SentrySetup.CaptureException(ex, "enqueue-download");
            return null;
        }
    }

    private void RefreshDownloads()
    {
        long totalSpeed = 0;
        int active = 0;
        foreach (var d in Downloads)
        {
            d.Refresh();
            if (d.IsActive) { active++; totalSpeed += (long)d.Speed; }
        }
        ActiveDownloadCount = active;
        _aggregateSpeed = totalSpeed;
        Changed(nameof(ActiveDownloadCount));
        Changed(nameof(AggregateSpeedText));
    }

    private int _activeDownloadCount;
    public int ActiveDownloadCount { get => _activeDownloadCount; private set { _activeDownloadCount = value; Changed(nameof(ActiveDownloadCount)); } }

    private long _aggregateSpeed;
    public string AggregateSpeedText => _aggregateSpeed > 0 ? $"{Formatter.SpeedText(_aggregateSpeed)}" : "";

    public void LoadNicChoices()
    {
        NicChoices.Clear();
        NicChoices.Add(new NicChoice("all", "Combined (all networks)"));
        foreach (var a in AppBootstrapper.OnlineAdapters())
            NicChoices.Add(new NicChoice(a.Id, $"{a.Name} ({a.TypeName}) — {a.IPv4Address}"));
    }

    // ---------------- proxies --------------------------------------------------------------------

    private string _proxyStatus = "";
    public string ProxyStatusText { get => _proxyStatus; set { _proxyStatus = value; Changed(nameof(ProxyStatusText)); } }

    public async Task RefreshProxiesAsync(bool force)
    {
        try
        {
            ProxyStatusText = "Fetching & testing free proxies…";
            var list = await Task.Run(() => AppBootstrapper.Proxies.GetWorkingAsync(force));
            WorkingProxies.Clear();
            foreach (var p in list.Take(60))
                WorkingProxies.Add(p);
            ProxyStatusText = list.Count == 0
                ? "No working proxies found — try Refresh."
                : $"{list.Count} working proxies (of the last test run).";
        }
        catch (Exception ex)
        {
            ProxyStatusText = "Proxy refresh failed: " + ex.Message;
            DualNet.Core.SentrySetup.CaptureException(ex, "proxy-refresh");
        }
    }

    public async Task<int?> TestCustomProxyAsync(string host, int port) =>
        await AppBootstrapper.Proxies.TestAsync(host, port);

    public async Task ApplyTabProxyAsync(TabViewModel tab, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            tab.Proxy = "";
            if (tab.Host is not null) await tab.Host.SetProxyAsync(null, 0);
            return;
        }
        var parts = proxy.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 80;
        if (tab.Host is not null)
            await tab.Host.SetProxyAsync(host, port);
        else
            tab.Proxy = $"{host}:{port}";
    }

    // ---------------- session ---------------------------------------------------------------------

    private DispatcherTimer? _sessionTimer;

    public void SaveSessionDebounced()
    {
        _sessionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _sessionTimer.Stop();
        _sessionTimer.Tick += SaveSessionNow;
        _sessionTimer.Start();
    }

    private void SaveSessionNow(object? s, EventArgs e)
    {
        ((DispatcherTimer)s!).Stop();
        SaveSessionNow();
    }

    public void SaveSessionNow()
    {
        try
        {
            var state = new SessionState
            {
                ActiveIndex = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab)),
                Tabs = Tabs.Select(t => new SessionTab
                {
                    Url = t.Url,
                    Title = t.Title,
                    Pinned = t.IsPinned,
                    Muted = t.IsMuted,
                    Private = t.IsPrivate,
                    Proxy = string.IsNullOrEmpty(t.Proxy) ? null : t.Proxy,
                    NetScope = t.NetScope,
                    InternalPage = t.Kind == TabContentKind.Internal ? t.InternalPage : null,
                }).ToList(),
            };
            AppBootstrapper.Sessions.Save(state);
        }
        catch { }
    }

    public void RestoreSession()
    {
        if (!AppBootstrapper.Settings.RestoreSession) return;
        try
        {
            var state = AppBootstrapper.Sessions.Load();
            if (state is null || state.Tabs.Count == 0)
            {
                CreateTab();
                return;
            }
            var restored = new List<TabViewModel>();
            foreach (var st in state.Tabs.Take(20))
            {
                if (string.IsNullOrWhiteSpace(st.Url)) continue;
                var tab = CreateTab(st.Url, background: true, privateTab: st.Private);
                tab.Proxy = st.Proxy ?? "";
                tab.NetScope = string.IsNullOrEmpty(st.NetScope) ? "all" : st.NetScope;
                if (st.Pinned) tab.IsPinned = true;
                restored.Add(tab);
            }
            if (restored.Count == 0) { CreateTab(); return; }
            var active = state.ActiveIndex >= 0 && state.ActiveIndex < restored.Count ? restored[state.ActiveIndex] : restored[0];
            ActivateTab(active);
        }
        catch
        {
            CreateTab();
        }
    }

    // ---------------- misc -------------------------------------------------------------------------

    public async Task TakeScreenshotAsync()
    {
        var host = ActiveTab?.Host;
        if (host is null) return;
        var path = Path.Combine(AppBootstrapper.EffectiveDownloadDir(),
            $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var saved = await host.ScreenshotAsync(path);
        StatusShown?.Invoke(saved is null ? "Screenshot failed" : $"Screenshot saved: {saved}");
    }

    public void ClearBrowsingData()
    {
        AppBootstrapper.BrowserStore.ClearVisited();
        try
        {
            foreach (var tab in Tabs)
            {
                var manager = tab.Host?.Context.GetCookieManager(null);
                manager?.DeleteCookies(null, null, null);
                manager?.FlushStore(null);
            }
        }
        catch (Exception ex)
        {
            DualNet.Core.SentrySetup.CaptureException(ex, "clear-data");
        }
    }

    private static void ShellOpen(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
        }
        catch { }
    }

    // ---------------- commands -----------------------------------------------------------------------

    public ICommand NewTabCommand { get; }
    public ICommand NewPrivateTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ActivateTabCommand { get; }
    public ICommand ReopenClosedTabCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand ToggleBookmarkCommand { get; }
    public ICommand ToggleBookmarksBarCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand RefreshProxiesCommand { get; }
    public ICommand PauseDownloadCommand { get; }
    public ICommand ResumeDownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand RemoveDownloadCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record NicChoice(string Id, string Label);

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
