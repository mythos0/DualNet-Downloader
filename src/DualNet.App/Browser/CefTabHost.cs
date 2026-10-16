using System.IO;
using CefSharp;
using CefSharp.Enums;
using CefSharp.Handler;
using CefSharp.Wpf.HwndHost;
using System.ComponentModel;
using CefSharp.Structs;
using DualNet.App.ViewModels;

namespace DualNet.App.Browser;

/// <summary>
/// Wraps one ChromiumWebBrowser per tab: per-tab request context (shared profile or
/// private), per-tab proxy, download interception into the DualNet engine (cookies/UA/
/// referer preserved), popups as new tabs, keyboard shortcuts, find-in-page and zoom.
/// </summary>
public sealed class CefTabHost : IDisposable
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 DualNet/4.1.1";

    private readonly TabViewModel _tab;
    private readonly MainViewModel _main;
    private readonly IRequestContext _context;
    private readonly FindHandlerImpl _findHandler = new();

    public ChromiumWebBrowser Browser { get; }
    public IRequestContext Context => _context;

    private int _findIdentifier = 1;

    public CefTabHost(TabViewModel tab, MainViewModel main, string? initialUrl)
    {
        _tab = tab;
        _main = main;

        _context = CefRuntime.CreateTabContext(tab.IsPrivate);
        Browser = new ChromiumWebBrowser
        {
            RequestContext = _context,
            RequestHandler = new RequestHandlerImpl(this),
        };
        Browser.DownloadHandler = new DownloadHandlerImpl(this);
        Browser.LifeSpanHandler = new PopupLifeSpanHandler(this);
        Browser.KeyboardHandler = new KeyboardHandlerImpl(this);
        Browser.FindHandler = _findHandler;

        // HwndHost browser has no AddressChanged/TitleChanged events — observe the DPs.
        DependencyPropertyDescriptor.FromProperty(ChromiumWebBrowser.AddressProperty, typeof(ChromiumWebBrowser))
            .AddValueChanged(Browser, OnAddressDpChanged);
        DependencyPropertyDescriptor.FromProperty(ChromiumWebBrowser.TitleProperty, typeof(ChromiumWebBrowser))
            .AddValueChanged(Browser, OnTitleDpChanged);
        Browser.LoadingStateChanged += (_, e) => _main.OnTabLoadingChanged(_tab, e.IsLoading, e.CanGoBack, e.CanGoForward);
        Browser.StatusMessage += (_, e) => _main.OnTabStatus(_tab, e.Value ?? "");
        Browser.FrameLoadEnd += OnFrameLoadEnd;
        Browser.LoadError += OnLoadError;

        if (!string.IsNullOrWhiteSpace(initialUrl))
            Browser.LoadUrl(initialUrl);
    }

    private void OnAddressDpChanged(object? sender, EventArgs e) =>
        _main.OnTabAddressChanged(_tab, Browser.Address ?? "");

    private void OnTitleDpChanged(object? sender, EventArgs e) =>
        _main.OnTabTitleChanged(_tab, Browser.Title ?? "");

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        _main.OnTabLoaded(_tab, Browser.Address ?? "");
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs e)
    {
        // CEF already renders its own error page for main-frame failures;
        // surface the code in the tab status for diagnostics.
        if (e.ErrorCode == CefErrorCode.Aborted) return;   // user navigation
        _main.OnTabStatus(_tab, $"Load error {e.ErrorCode}: {e.ErrorText}");
    }

    // ---------------- proxy ----------------------------------------------------------------

    public async Task SetProxyAsync(string? host, int port)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host))
                await _context.SetProxyAsync("", null);
            else
                await _context.SetProxyAsync(host, port > 0 ? port : null);
            _tab.Proxy = string.IsNullOrWhiteSpace(host) ? "" : $"{host}:{port}";
        }
        catch (Exception ex)
        {
            DualNet.Core.SentrySetup.CaptureException(ex, "tab-setproxy");
        }
    }

    // ---------------- find / zoom / devtools / misc -----------------------------------------

    public void Find(string text, bool forward, bool findNext)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Browser.StopFinding(true);
            return;
        }
        Browser.Find(text, forward, false, findNext);
    }

    public void FindStopped() => Browser.StopFinding(true);
    public void FindHandlerResult(Action<int, int, int> onResult) => _findHandler.Result = onResult;

    public void ZoomIn() => Browser.ZoomInCommand.Execute(null);
    public void ZoomOut() => Browser.ZoomOutCommand.Execute(null);
    public void ZoomReset() => Browser.ZoomResetCommand.Execute(null);

    public void ShowDevTools() => Browser.ShowDevTools();

    public void ToggleMute()
    {
        Browser.ToggleAudioMuteCommand.Execute(null);
        _tab.IsMuted = !_tab.IsMuted;
    }

    public async Task<string?> ScreenshotAsync(string path)
    {
        try
        {
            var devTools = Browser.GetDevToolsClient();
            var shot = await devTools.Page.CaptureScreenshotAsync(
                CefSharp.DevTools.Page.CaptureScreenshotFormat.Png, null, null, null, true, null);
            if (shot?.Data is not { Length: > 0 } bytes) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            DualNet.Core.SentrySetup.CaptureException(ex, "screenshot");
            return null;
        }
    }

    /// <summary>Captures the tab's cookies for a URL (download auth context).</summary>
    public async Task<string> GetCookieHeaderAsync(string url)
    {
        try
        {
            var manager = _context.GetCookieManager(null);
            if (manager is null) return "";
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parts = new List<string>();
            manager.VisitUrlCookies(url, true, new CookieVisitor((cookie, count, total) =>
            {
                parts.Add($"{cookie.Name}={cookie.Value}");
                if (count >= total - 1)
                    tcs.TrySetResult(string.Join("; ", parts));
            }));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(1500));
            return done == tcs.Task ? await tcs.Task : string.Join("; ", parts);
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        try { Browser.Dispose(); } catch { }
        try { _context.Dispose(); } catch { }
    }

    // ---------------- CEF handler implementations -------------------------------------------

    private sealed class CookieVisitor : ICookieVisitor
    {
        private readonly Action<CefSharp.Cookie, int, int> _visit;
        public CookieVisitor(Action<CefSharp.Cookie, int, int> visit) => _visit = visit;
        public bool Visit(CefSharp.Cookie cookie, int count, int total, ref bool deleteCookie)
        {
            _visit(cookie, count, total);
            return true;
        }
        public void Dispose() { }
    }

    private sealed class FindHandlerImpl : IFindHandler
    {
        public Action<int, int, int>? Result;   // (count, activeMatchOrdinal, identifier)

        public void OnFindResult(IWebBrowser chromiumWebBrowser, IBrowser browser, int identifier, int count,
            Rect selectionRange, int activeMatchOrdinal, bool finalUpdate)
        {
            Result?.Invoke(count, activeMatchOrdinal, identifier);
        }
    }

    /// <summary>Blocks third-party trackers nothing else; kept minimal on purpose.</summary>
    private sealed class RequestHandlerImpl : CefSharp.Handler.RequestHandler
    {
        private readonly CefTabHost _host;
        public RequestHandlerImpl(CefTabHost host) => _host = host;

        protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, bool userGesture, bool isRedirect)
        {
            var url = request.Url ?? "";
            // dualnet:// links never occur on the web, but guard anyway.
            if (url.StartsWith("dualnet://", StringComparison.Ordinal))
            {
                _host._main.OpenInternalPage(_host._tab, url);
                return true;
            }
            return base.OnBeforeBrowse(chromiumWebBrowser, browser, frame, request, userGesture, isRedirect);
        }
    }

    private sealed class DownloadHandlerImpl : IDownloadHandler
    {
        private readonly CefTabHost _host;
        public DownloadHandlerImpl(CefTabHost host) => _host = host;

        public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
            => true;

        public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
            IBeforeDownloadCallback callback)
        {
            if (downloadItem is not { IsValid: true }) return false;

            var url = downloadItem.OriginalUrl ?? downloadItem.Url ?? "";
            var tabUrl = _host.Browser.Address ?? "";
            var info = new BrowserDownloadInfo
            {
                Url = url,
                SuggestedFileName = downloadItem.SuggestedFileName ?? "download",
                MimeType = downloadItem.MimeType ?? "",
                ContentDisposition = downloadItem.ContentDisposition ?? "",
                TotalBytes = downloadItem.TotalBytes,
                Referer = tabUrl.StartsWith("http", StringComparison.Ordinal) ? tabUrl : "",
                UserAgent = BrowserUserAgent,
            };

            _ = Task.Run(async () =>
            {
                try { info.Cookies = await _host.GetCookieHeaderAsync(url); }
                catch { }
                _host._main.BeginBrowserDownload(_host._tab, info);
            });
            // Returning false cancels Chromium's own downloader — the DualNet engine takes over.
            return false;
        }

        public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
            IDownloadItemCallback callback)
        {
            // CEF's downloader is cancelled in OnBeforeDownload; nothing to track here.
        }
    }

    private sealed class PopupLifeSpanHandler : CefSharp.Handler.LifeSpanHandler
    {
        private readonly CefTabHost _host;
        public PopupLifeSpanHandler(CefTabHost host) => _host = host;

        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
            IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
        {
            newBrowser = null;
            var url = targetUrl ?? "";
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file", StringComparison.OrdinalIgnoreCase))
            {
                _host._main.OpenPopupAsTab(url, targetDisposition != WindowOpenDisposition.NewForegroundTab);
            }
            return true;   // handled — never let CEF create a native popup window
        }
    }

    private sealed class KeyboardHandlerImpl : IKeyboardHandler
    {
        private readonly CefTabHost _host;
        public KeyboardHandlerImpl(CefTabHost host) => _host = host;

        public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int keyCode,
            int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut)
        {
            if (type != KeyType.RawKeyDown) return false;
            var handled = Dispatch(keyCode, modifiers);
            if (handled) isKeyboardShortcut = true;
            return handled;
        }

        public bool OnKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int keyCode,
            int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey) => false;

        private bool Dispatch(int keyCode, CefEventFlags modifiers)
        {
            var main = _host._main;
            var ctrl = modifiers.HasFlag(CefEventFlags.ControlDown);
            var shift = modifiers.HasFlag(CefEventFlags.ShiftDown);
            var alt = modifiers.HasFlag(CefEventFlags.AltDown);

            // Post to the WPF dispatcher — CEF keyboard callbacks come from the browser process.
            void Post(Action a) => main.PostToUi(a);

            if (ctrl && !shift && !alt)
            {
                switch (keyCode)
                {
                    case 0x54: Post(() => main.NewTabCommand.Execute(null)); return true;          // T
                    case 0x57: Post(() => main.CloseTabCommand.Execute(main.ActiveTab)); return true; // W
                    case 0x4C: Post(() => main.FocusOmniboxRequested?.Invoke()); return true;       // L
                    case 0x46: Post(() => main.StartFindRequested?.Invoke()); return true;          // F
                    case 0x4A: Post(() => main.OpenInternalPage(main.ActiveTab, "dualnet://app/downloads")); return true; // J
                    case 0x44: Post(() => main.ToggleBookmarkCommand.Execute(null)); return true;   // D
                    case 0x52: Post(() => main.ReloadCommand.Execute(null)); return true;           // R
                    case 0xBB: Post(() => main.ActiveTab?.Host?.ZoomIn()); return true;             // +
                    case 0xBD: Post(() => main.ActiveTab?.Host?.ZoomOut()); return true;            // -
                    case 0x30: Post(() => main.ActiveTab?.Host?.ZoomReset()); return true;          // 0
                    case 0x09: Post(() => main.CycleTab(shift ? -1 : 1)); return true; // Tab
                    case 0x31: Post(() => main.ActivateTabByIndex(0)); return true;                 // 1
                    case 0x32: Post(() => main.ActivateTabByIndex(1)); return true;                 // 2
                    case 0x33: Post(() => main.ActivateTabByIndex(2)); return true;                 // 3
                    case 0x34: Post(() => main.ActivateTabByIndex(3)); return true;                 // 4
                    case 0x35: Post(() => main.ActivateTabByIndex(4)); return true;                 // 5
                    case 0x36: Post(() => main.ActivateTabByIndex(5)); return true;                 // 6
                    case 0x37: Post(() => main.ActivateTabByIndex(6)); return true;                 // 7
                    case 0x38: Post(() => main.ActivateTabByIndex(7)); return true;                 // 8
                    case 0x39: Post(() => main.ActivateTabByIndex(8)); return true;                 // 9 (last)
                }
            }
            if (ctrl && shift && !alt)
            {
                switch (keyCode)
                {
                    case 0x54: Post(() => main.ReopenClosedTabCommand.Execute(null)); return true;  // Shift+T
                    case 0x4E: Post(() => main.NewPrivateTabCommand.Execute(null)); return true;    // Shift+N
                    case 0x41: Post(() => main.OpenInternalPage(main.ActiveTab, "dualnet://app/bookmarks")); return true; // Shift+A
                    case 0x42: Post(() => main.ToggleBookmarksBarCommand.Execute(null)); return true; // Shift+B
                }
            }
            if (keyCode == 0x74 && !ctrl) { Post(() => main.ReloadCommand.Execute(null)); return true; }      // F5
            if (keyCode == 0x75) { Post(() => main.ActiveTab?.Host?.ShowDevTools()); return true; }            // F12
            if (keyCode == 0x73 && ctrl) { Post(() => main.ScreenshotCommand.Execute(null)); return true; }    // Ctrl+F3
            if (alt && keyCode == 0x25) { Post(() => main.BackCommand.Execute(null)); return true; }           // Alt+Left
            if (alt && keyCode == 0x27) { Post(() => main.ForwardCommand.Execute(null)); return true; }        // Alt+Right
            return false;
        }
    }
}

/// <summary>Info the engine needs to reproduce the browser's auth context on a download.</summary>
public sealed class BrowserDownloadInfo
{
    public string Url { get; set; } = "";
    public string SuggestedFileName { get; set; } = "download";
    public string MimeType { get; set; } = "";
    public string ContentDisposition { get; set; } = "";
    public long TotalBytes { get; set; } = -1;
    public string Referer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Cookies { get; set; } = "";
}
