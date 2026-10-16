using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DualNet.App.Browser;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core.Models;
using DualNet.Core.Net;
using DualNet.Core.Storage;
using DualNet.Core.Engine.Browser;
using Microsoft.Win32;

namespace DualNet.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<int, FrameworkElement> _tabViews = new();
    private TabViewModel? _contextTab;

    // v4.1.2: the tab context menu lives in Window.Resources (a ContextMenu can never be a
    // layout child — see MainWindow.xaml comment). Resolved lazily, never throws.
    private ContextMenu? TabContextMenu => TryFindResource("TabContextMenu") as ContextMenu;
    private System.IntPtr _hwnd;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.InternalPageFactory = BuildInternalPage;
        _vm.DownloadBubbleRequested += OnDownloadBubbleRequested;
        _vm.FocusOmniboxRequested += () => Dispatcher.BeginInvoke(() => { Omnibox.Focus(); Omnibox.SelectAll(); });
        _vm.StartFindRequested += () => OpenFind();
        _vm.StatusShown += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
        _vm.TabsChanged += () => Dispatcher.BeginInvoke(SyncTabViews);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        StateChanged += (_, _) => SyncTabViews();
        _vm.Tabs.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(SyncTabViews);
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ActiveDownloadCount))
                Dispatcher.BeginInvoke(UpdateDownloadBadge);
        };
        _vm.Downloads.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateDownloadBadge);
    }

    // ---------------- startup ----------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_COPYDATA = 0x004A;
        if (msg == WM_COPYDATA)
        {
            NativeMethods.ReceiveCopyData(lParam);
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SuggestPopup.PlacementTarget = Omnibox;
        FindPopup.PlacementTarget = Omnibox;
        DownloadsPopup.PlacementTarget = BtnDownloads;
        ProxyPopup.PlacementTarget = BtnProxy;
        MenuPopup.PlacementTarget = BtnMenu;

        if (AppBootstrapper.Settings.WindowBounds is { } b && b.Height > 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!AppBootstrapper.Settings.WindowMaximized)
            {
                Left = b.X; Top = b.Y; Width = b.Width; Height = b.Height;
            }
        }
        if (AppBootstrapper.Settings.WindowMaximized) WindowState = WindowState.Maximized;

        NetSelector.ItemsSource = _vm.NicChoices;
        SyncNetSelector();
        _vm.LoadNicChoices();
        NetSelector.ItemsSource = _vm.NicChoices;

        // theme system change listener
        _prefHandler = (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(() => App.Current.RefreshSystemTheme());
        };
        SystemEvents.UserPreferenceChanged += _prefHandler;

        _vm.RestoreSession();
        _ = _vm.RefreshProxiesAsync(force: false);   // serve cache / fetch silently
        _ = StartupWarmupAsync();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        AppBootstrapper.Settings.WindowBounds = new WindowRect(Left, Top, Width, Height);
        AppBootstrapper.Settings.WindowMaximized = WindowState == WindowState.Maximized;
        AppBootstrapper.SaveSettings();
    }

    private UserPreferenceChangedEventHandler? _prefHandler;

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _vm.SaveSessionNow();
        try { SystemEvents.UserPreferenceChanged -= _prefHandler; } catch { }
        Application.Current.Shutdown();
    }

    private async Task StartupWarmupAsync()
    {
        // Warm the proxy cache on first launch so the bubble is instantly useful.
        try
        {
            await _vm.RefreshProxiesAsync(force: false);
            await Dispatcher.InvokeAsync(() =>
            {
                ProxyList.ItemsSource = _vm.WorkingProxies.Take(20).ToList();
            });
        }
        catch { }
    }

    public void OpenUrlInNewTab(string url) => _vm.OpenUrlInNewTab(url);

    // ---------------- window chrome ----------------------------------------------------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    private void BtnNewTab_Click(object sender, RoutedEventArgs e) => _vm.CreateTab();

    // ---------------- tab strip ---------------------------------------------------------------

    private void SyncTabViews()
    {
        // tab strip active state + view host sync
        foreach (var tab in _vm.Tabs)
        {
            if (tab.IsWeb && tab.Host is not null)
            {
                if (tab.Host.Browser.CleanupElement is null) tab.Host.Browser.CleanupElement = Root;
            }
            _tabViews.TryGetValue(tab.Id, out var existing);
            if (existing is null)
            {
                existing = CreateViewFor(tab);
                if (existing is not null)
                {
                    _tabViews[tab.Id] = existing;
                    PageHost.Children.Add(existing);
                }
            }
            Visibility vis;
            if (_vm.IsSplitMode)
            {
                // split mode: exactly the two pane tabs are visible
                bool inSplit = tab == _vm.SplitLeft || tab == _vm.SplitRight;
                vis = inSplit ? Visibility.Visible : Visibility.Collapsed;
                if (inSplit && existing is not null)
                {
                    int col = tab == _vm.SplitRight ? 2 : 0;
                    Grid.SetColumn(existing, col);
                    Grid.SetColumnSpan(existing, 1);
                }
            }
            else
            {
                vis = tab.IsActive ? Visibility.Visible : Visibility.Collapsed;
                if (existing is not null)
                {
                    Grid.SetColumn(existing, 0);
                    Grid.SetColumnSpan(existing, 3);
                }
            }
            if (existing is not null)
                existing.Visibility = vis;
        }
        // remove views of closed tabs
        foreach (var dead in _tabViews.Keys.Where(id => _vm.Tabs.All(t => t.Id != id)).ToList())
        {
            if (_tabViews.Remove(dead, out var el)) PageHost.Children.Remove(el);
        }
        ApplySplitLayout();
        SyncNetSelector();
        UpdateProxyBadge();
    }

    private GridSplitter? _splitter;
    private Border? _splitDivider;

    /// <summary>Maintains the two-column page layout + divider for split mode.</summary>
    private void ApplySplitLayout()
    {
        if (_vm.IsSplitMode)
        {
            PageHost.ColumnDefinitions.Clear();
            PageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (_splitter is null)
            {
                _splitter = new GridSplitter
                {
                    Width = 6,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = TryFindResource("BrushBorder") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    ResizeDirection = GridResizeDirection.Columns,
                };
                Grid.SetColumn(_splitter, 1);
                PageHost.Children.Add(_splitter);
            }
            _splitter.Visibility = Visibility.Visible;
        }
        else
        {
            PageHost.ColumnDefinitions.Clear();
            PageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (_splitter is not null) _splitter.Visibility = Visibility.Collapsed;
        }
    }

    private Views.AgentPage? _agentPage;

    private Views.AgentPage CreateAgentPage()
    {
        // One shared agent page VM keeps model status/idle manager across visits.
        _agentPage ??= new Views.AgentPage { DataContext = new AgentViewModel(_vm) };
        return _agentPage;
    }

    private FrameworkElement? CreateViewFor(TabViewModel tab)
    {
        if (tab.IsWeb)
        {
            if (tab.Host is null) return null;
            return tab.Host.Browser;
        }
        tab.InternalView ??= _vm.InternalPageFactory(tab);
        return tab.InternalView as FrameworkElement;
    }

    private void SyncNetSelector()
    {
        var tab = _vm.ActiveTab;
        if (tab is null) return;
        var match = _vm.NicChoices.FirstOrDefault(n => n.Id == tab.NetScope);
        NetSelector.SelectionChanged -= NetSelector_SelectionChanged;
        NetSelector.SelectedItem = match ?? _vm.NicChoices.FirstOrDefault();
        NetSelector.SelectionChanged += NetSelector_SelectionChanged;
    }

    private void UpdateProxyBadge()
    {
        var tab = _vm.ActiveTab;
        ProxyBadge.Text = tab is { ProxyActive: true } ? $"proxy: {tab.Proxy}" : "";
        BtnProxy.Foreground = tab is { ProxyActive: true }
            ? FindResource("BrushWarn") as Brush
            : FindResource("BrushIcon") as Brush;
    }

    private FrameworkElement BuildInternalPage(TabViewModel tab)
    {
        return tab.InternalPage switch
        {
            "downloads" => new Views.DownloadsPage { DataContext = _vm },
            "history" => new Views.HistoryPage { DataContext = _vm },
            "bookmarks" => new Views.BookmarksPage { DataContext = _vm },
            "settings" => new Views.SettingsPage { DataContext = _vm },
            "extensions" => new Views.ExtensionsPage { DataContext = _vm },
            "agent" => CreateAgentPage(),
            "about" => new Views.AboutPage { DataContext = _vm },
            "engine-error" => BuildEngineErrorPage(tab),
            _ => new Views.NewTabPage { DataContext = _vm },
        };
    }

    /// <summary>v4.1.1 — shown when the Chromium engine cannot be created for a tab, so the
    /// failure is visible (with its reason) instead of a blank tab and a silent exception.</summary>
    private FrameworkElement BuildEngineErrorPage(TabViewModel tab)
    {
        var panel = new StackPanel { Margin = new Thickness(40) };
        panel.Children.Add(new TextBlock
        {
            Text = "The browser engine could not be started for this tab",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = tab.EngineError.Length > 0 ? tab.EngineError : "Unknown error.",
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
            Foreground = (Brush)FindResource("BrushTextDim"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Re-extract the full DualNet zip into one folder and restart the app. " +
                   "Details are saved in %APPDATA%\\DualNet\\logs.",
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
            Foreground = (Brush)FindResource("BrushTextDim"),
            TextWrapping = TextWrapping.Wrap,
        });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void NetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.ActiveTab is not { } tab || NetSelector.SelectedItem is not NicChoice choice) return;
        tab.NetScope = choice.Id;
        _vm.SaveSessionDebounced();
    }

    private void TabStrip_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            var item = ItemsControl.ContainerFromElement(TabStrip, d) as ContentPresenter;
            if (item?.Content is TabViewModel tab)
            {
                _contextTab = tab;
                var menu = TabContextMenu;
                if (menu is not null)
                {
                    menu.PlacementTarget = TabStrip;
                    menu.IsOpen = true;
                }
                e.Handled = true;
            }
        }
    }

    private void TabPin_Click(object s, RoutedEventArgs e) { if (_contextTab is { } t) t.IsPinned = !t.IsPinned; }
    private void TabMute_Click(object s, RoutedEventArgs e) { if (_contextTab?.Host is { } h) h.ToggleMute(); }
    private void TabDuplicate_Click(object s, RoutedEventArgs e)
    {
        if (_contextTab is { } t) _vm.CreateTab(t.IsWeb ? t.Url : null);
    }
    private void TabClose_Click(object s, RoutedEventArgs e) { if (_contextTab is { } t) _vm.CloseTab(t); }
    private void TabCloseOthers_Click(object s, RoutedEventArgs e)
    {
        if (_contextTab is not { } keep) return;
        foreach (var t in _vm.Tabs.Where(t => t != keep).ToList()) _vm.CloseTab(t);
    }

    // ---------------- toolbar ------------------------------------------------------------------

    private void BtnBack_Click(object s, RoutedEventArgs e) => _vm.BackCommand.Execute(null);
    private void BtnForward_Click(object s, RoutedEventArgs e) => _vm.ForwardCommand.Execute(null);
    private void BtnReload_Click(object s, RoutedEventArgs e) => _vm.ReloadCommand.Execute(null);
    private void BtnHome_Click(object s, RoutedEventArgs e) => _vm.HomeCommand.Execute(null);
    private void BtnBookmarkStar_Click(object s, RoutedEventArgs e) => _vm.ToggleBookmarkCommand.Execute(null);

    private void Omnibox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = Omnibox.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            SuggestPopup.IsOpen = false;
            _vm.Navigate(_vm.ActiveTab, text);
            Keyboard.ClearFocus();
            MoveFocusToPage();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestPopup.IsOpen = false;
            ResetOmniboxToTab();
            MoveFocusToPage();
        }
    }

    private void Omnibox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Omnibox.SelectAll();
    private void Omnibox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SuggestPopup.IsOpen) ResetOmniboxToTab();
    }

    private void ResetOmniboxToTab()
    {
        _suggestSuppress = true;
        Omnibox.Text = _vm.ActiveTab?.OmniboxText ?? "";
        _suggestSuppress = false;
    }

    private void MoveFocusToPage()
    {
        var host = _vm.ActiveTab?.Host;
        if (host is not null)
            Dispatcher.BeginInvoke(() => { try { host.Browser.Focus(); } catch { } });
    }

    // omnibox suggestions
    private void Omnibox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suggestSuppress) _ = UpdateSuggestionsAsync(Omnibox.Text ?? "");
    }

    private bool _suggestSuppress;

    private async Task UpdateSuggestionsAsync(string text)
    {
        var query = text.Trim();
        if (query.Length == 0) { SuggestPopup.IsOpen = false; return; }

        var suggestions = await Task.Run(() =>
        {
            var list = new List<Suggestion>();
            try
            {
                foreach (var h in AppBootstrapper.BrowserStore.Visited
                    .Where(v => v.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                v.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(6))
                    list.Add(new Suggestion("🕘", h.Title.Length > 0 ? h.Title : h.Url, HostOf(h.Url), h.Url));
            }
            catch { }
            try
            {
                foreach (var b in AppBootstrapper.BrowserStore.Bookmarks
                    .Where(b => b.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                b.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(4))
                    list.Add(new Suggestion("★", b.Title, "Bookmark", b.Url));
            }
            catch { }
            list.Add(new Suggestion("🔎", $"{query}", "DuckDuckGo",
                SearchEngines.BuildUrl(AppBootstrapper.Settings.SearchEngine, query)));
            return list;
        });

        SuggestList.ItemsSource = suggestions;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = suggestions.Count > 0;
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    private void SuggestList_Click(object sender, MouseButtonEventArgs e) => CommitSuggestion();
    private void SuggestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitSuggestion();
    }

    private void CommitSuggestion()
    {
        Suggestion? sug = SuggestList.SelectedItem as Suggestion;
        sug ??= (SuggestList.ItemsSource as List<Suggestion>)?.FirstOrDefault();
        if (sug is null) return;
        SuggestPopup.IsOpen = false;
        _suggestSuppress = true;
        Omnibox.Text = sug.Primary;
        _suggestSuppress = false;
        _vm.Navigate(_vm.ActiveTab, sug.Url);
        MoveFocusToPage();
    }

    private void Omnibox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.Text) is string s)
            _vm.Navigate(_vm.ActiveTab, s);
    }

    // find
    private void BtnFind_Click(object s, RoutedEventArgs e) => OpenFind();

    private void OpenFind()
    {
        FindPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => FindBox.Focus());
    }

    private void FindBox_TextChanged(object s, TextChangedEventArgs e) => RunFind(false, false);
    private void FindBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunFind(forward: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), findNext: true);
        if (e.Key == Key.Escape) { FindPopup.IsOpen = false; }
    }
    private void FindNext_Click(object s, RoutedEventArgs e) => RunFind(false, true);
    private void FindPrev_Click(object s, RoutedEventArgs e) => RunFind(true, true);
    private void FindClose_Click(object s, RoutedEventArgs e) { FindPopup.IsOpen = false; _vm.ActiveTab?.Host?.FindStopped(); }

    private void RunFind(bool forward, bool findNext)
    {
        var host = _vm.ActiveTab?.Host;
        if (host is null) { FindCount.Text = "internal page"; return; }
        host.FindHandlerResult((count, ordinal, _) =>
            Dispatcher.BeginInvoke(() => FindCount.Text = count <= 0 ? "0 results" : $"{ordinal}/{count}"));
        host.Find(FindBox.Text ?? "", forward, findNext);
    }

    // net/proxy/downloads/menu buttons
    private void BtnDownloads_Click(object s, RoutedEventArgs e)
    {
        UpdatePendingPanel();
        DownloadsPopup.IsOpen = true;
    }

    private void OpenDownloadsManager_Click(object s, MouseButtonEventArgs e)
    {
        DownloadsPopup.IsOpen = false;
        _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/downloads");
    }

    private void BtnProxy_Click(object s, RoutedEventArgs e)
    {
        var tab = _vm.ActiveTab;
        ProxyCurrentText.Text = tab is null ? "" : $"Current: {(tab.ProxyActive ? tab.Proxy : "Direct")}";
        ProxyList.ItemsSource = _vm.WorkingProxies.Take(20).ToList();
        ProxyHint.Visibility = _vm.WorkingProxies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProxyStatusMini.Text = _vm.ProxyStatusText;
        ProxyPopup.IsOpen = true;
        if (_vm.WorkingProxies.Count == 0)
            _ = _vm.RefreshProxiesAsync(false);
    }

    private void ProxyDirect_Click(object s, RoutedEventArgs e)
    {
        _ = _vm.ApplyTabProxyAsync(_vm.ActiveTab!, null);
        ProxyPopup.IsOpen = false;
        UpdateProxyBadge();
    }

    private void ProxyPick_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not ProxyEntry entry) return;
        _ = _vm.ApplyTabProxyAsync(_vm.ActiveTab!, entry.EndPoint);
        ProxyPopup.IsOpen = false;
        UpdateProxyBadge();
    }

    private async void ProxyRefresh_Click(object s, RoutedEventArgs e)
    {
        ProxyStatusMini.Text = "testing…";
        await _vm.RefreshProxiesAsync(true);
        ProxyList.ItemsSource = _vm.WorkingProxies.Take(20).ToList();
        ProxyHint.Visibility = _vm.WorkingProxies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProxyStatusMini.Text = _vm.ProxyStatusText;
    }

    private async void CustomProxyTest_Click(object s, RoutedEventArgs e)
    {
        if (!TryParseCustomProxy(out var host, out var port)) return;
        CustomProxyResult.Text = "testing…";
        var ms = await _vm.TestCustomProxyAsync(host, port);
        CustomProxyResult.Text = ms is null ? $"✕ {host}:{port} unreachable" : $"✓ {host}:{port} works ({ms} ms)";
    }

    private void CustomProxyUse_Click(object s, RoutedEventArgs e)
    {
        if (!TryParseCustomProxy(out var host, out var port)) return;
        _ = _vm.ApplyTabProxyAsync(_vm.ActiveTab!, $"{host}:{port}");
        ProxyPopup.IsOpen = false;
        UpdateProxyBadge();
    }

    private bool TryParseCustomProxy(out string host, out int port)
    {
        host = CustomProxyHost.Text.Trim();
        if (!int.TryParse(CustomProxyPort.Text.Trim(), out port)) port = 0;
        if (host.Length == 0 || port is < 1 or > 65535)
        {
            CustomProxyResult.Text = "Enter host and port (1–65535).";
            return false;
        }
        return true;
    }

    private void BtnMenu_Click(object s, RoutedEventArgs e) => MenuPopup.IsOpen = true;

    private void MenuNewTab_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.CreateTab(); }
    private void MenuNewPrivate_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.CreateTab(privateTab: true); }
    private void MenuReopen_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ReopenClosedTab(); }
    private void MenuHistory_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/history"); }
    private void MenuBookmarks_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/bookmarks"); }
    private void MenuDownloadsPage_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/downloads"); }
    private void MenuFind_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenFind(); }
    private void MenuZoomIn_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ActiveTab?.Host?.ZoomIn(); }
    private void MenuZoomOut_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ActiveTab?.Host?.ZoomOut(); }
    private void MenuZoomReset_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ActiveTab?.Host?.ZoomReset(); }
    private void MenuScreenshot_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _ = _vm.TakeScreenshotAsync(); }
    private void MenuDevTools_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ActiveTab?.Host?.ShowDevTools(); }
    private void MenuExtensions_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/extensions"); }
    private void MenuSettings_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/settings"); }
    private void MenuAbout_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/about"); }
    private void MenuAgent_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/agent"); }
    private void BtnAgent_Click(object s, RoutedEventArgs e) => _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/agent");
    private void MenuSplit_Click(object s, RoutedEventArgs e) { MenuPopup.IsOpen = false; _vm.ToggleSplitView(); }

    private void ToggleBookmarksBarCommand_Execute() => _vm.ToggleBookmarksBarCommand.Execute(null);

    private void BookmarkChip_MouseUp(object s, MouseButtonEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is BookmarkEntry b)
            _vm.Navigate(_vm.ActiveTab, b.Url);
    }

    // ---------------- download bubble ------------------------------------------------------------

    private void OnDownloadBubbleRequested(TabViewModel tab, BrowserDownloadInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdatePendingPanel();
            DownloadsPopup.IsOpen = true;
        });
    }

    private void UpdatePendingPanel()
    {
        var info = _vm.PendingDownloadInfo;
        if (info is null)
        {
            PendingDownloadPanel.Visibility = Visibility.Collapsed;
            UpdateDownloadBadge();
            return;
        }
        PendingDownloadPanel.Visibility = Visibility.Visible;
        PendingFileName.Text = info.SuggestedFileName;
        PendingUrl.Text = info.Url;
        PendingNetSelector.ItemsSource = _vm.NicChoices;
        var defaultNic = _vm.PendingDownloadTab?.NetScope ?? "all";
        PendingNetSelector.SelectedItem = _vm.NicChoices.FirstOrDefault(n => n.Id == defaultNic) ?? _vm.NicChoices.FirstOrDefault();

        var proxyChoices = new List<string> { "Direct" };
        proxyChoices.AddRange(_vm.WorkingProxies.Take(10).Select(p => p.EndPoint));
        var tabProxy = _vm.PendingDownloadTab?.Proxy;
        if (!string.IsNullOrWhiteSpace(tabProxy) && !proxyChoices.Contains(tabProxy))
            proxyChoices.Insert(1, tabProxy);
        PendingProxySelector.ItemsSource = proxyChoices;
        PendingProxySelector.SelectedIndex = 0;
        UpdateDownloadBadge();
    }

    private void PendingProxy_SelectionChanged(object s, SelectionChangedEventArgs e) { }

    private void PendingSaveAs_Click(object s, RoutedEventArgs e)
    {
        var info = _vm.PendingDownloadInfo;
        if (info is null) return;
        var dlg = new SaveFileDialog
        {
            FileName = info.SuggestedFileName,
            InitialDirectory = AppBootstrapper.EffectiveDownloadDir(),
        };
        if (dlg.ShowDialog(this) == true)
            StartPendingDownload(Path.GetDirectoryName(dlg.FileName));
        DownloadsPopup.IsOpen = false;
    }

    private void PendingStart_Click(object s, RoutedEventArgs e)
    {
        StartPendingDownload(null);
        DownloadsPopup.IsOpen = false;
    }

    private void PendingCancel_Click(object s, RoutedEventArgs e)
    {
        _vm.DismissPendingDownload();
        PendingDownloadPanel.Visibility = Visibility.Collapsed;
        DownloadsPopup.IsOpen = false;
    }

    private void StartPendingDownload(string? targetDir)
    {
        var info = _vm.PendingDownloadInfo;
        var tab = _vm.PendingDownloadTab;
        if (info is null) return;

        var scopeId = (PendingNetSelector.SelectedItem as NicChoice)?.Id ?? "all";
        var proxy = PendingProxySelector.SelectedItem as string;
        proxy = string.IsNullOrWhiteSpace(proxy) || proxy == "Direct" ? null : proxy;

        _vm.EnqueueDownload(info.Url, info.SuggestedFileName, scopeId, proxy,
            referer: info.Referer, cookies: info.Cookies, userAgent: info.UserAgent, targetDir: targetDir);
        _vm.DismissPendingDownload();
        PendingDownloadPanel.Visibility = Visibility.Collapsed;
        UpdateDownloadBadge();
    }

    private void UpdateDownloadBadge()
    {
        var active = _vm.Downloads.Count(d => d.IsActive);
        DownloadBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadBadgeText.Text = active.ToString();
    }

    /// <summary>Add-URL flow from internal pages — starts immediately with the tab's defaults.</summary>
    public void AddDownloadFromUi(string url)
    {
        var tab = _vm.ActiveTab;
        if (tab is null) return;
        var scopeId = tab.NetScope;
        var proxy = string.IsNullOrWhiteSpace(tab.Proxy) ? null : tab.Proxy;
        _vm.EnqueueDownload(url, GuessName(url), scopeId, proxy);
    }

    private static string GuessName(string url)
    {
        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.Split('/').LastOrDefault(p => p.Length > 0);
            return string.IsNullOrWhiteSpace(last) ? u.Host : Uri.UnescapeDataString(last);
        }
        catch { return "download"; }
    }

    // ---------------- global keyboard (when chrome UI has focus) ---------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.T when ctrl && shift: _vm.CreateTab(privateTab: true); e.Handled = true; break;
            case Key.T when ctrl: _vm.CreateTab(); e.Handled = true; break;
            case Key.W when ctrl: if (_vm.ActiveTab is { } t) _vm.CloseTab(t); e.Handled = true; break;
            case Key.L when ctrl: Omnibox.Focus(); Omnibox.SelectAll(); e.Handled = true; break;
            case Key.F when ctrl: OpenFind(); e.Handled = true; break;
            case Key.J when ctrl: _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/downloads"); e.Handled = true; break;
            case Key.K when ctrl && shift: _vm.OpenInternalPage(_vm.ActiveTab, "dualnet://app/agent"); e.Handled = true; break;
            case Key.S when ctrl && shift: _vm.ToggleSplitView(); e.Handled = true; break;
            case Key.R when ctrl: _vm.ReloadCommand.Execute(null); e.Handled = true; break;
            case Key.D when ctrl: _vm.ToggleBookmarkCommand.Execute(null); e.Handled = true; break;
            case Key.F5: _vm.ReloadCommand.Execute(null); e.Handled = true; break;
            case Key.F12: _vm.ActiveTab?.Host?.ShowDevTools(); e.Handled = true; break;
            case Key.F3 when ctrl: _ = _vm.TakeScreenshotAsync(); e.Handled = true; break;
            case Key.Add when ctrl: _vm.ActiveTab?.Host?.ZoomIn(); e.Handled = true; break;
            case Key.Subtract when ctrl: _vm.ActiveTab?.Host?.ZoomOut(); e.Handled = true; break;
            case Key.D0 when ctrl: _vm.ActiveTab?.Host?.ZoomReset(); e.Handled = true; break;
            case Key.Escape when FindPopup.IsOpen: FindPopup.IsOpen = false; e.Handled = true; break;
        }
        base.OnPreviewKeyDown(e);
    }

    public void OnWindowActivated() { }
}

public sealed record Suggestion(string Glyph, string Primary, string Secondary, string Url);
