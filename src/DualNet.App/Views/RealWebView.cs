using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace DualNet.App.Views;

/// <summary>
/// A real Chromium browser surface (Microsoft Edge WebView2) hosted natively inside an
/// Avalonia tab via <see cref="NativeControlHost"/>.
///
/// Windows-only at runtime: needs the Evergreen WebView2 Runtime (preinstalled on
/// Windows 10/11 — same engine as Edge/Chrome, full JS/CSS/cookies/logins). Every
/// managed download is intercepted and routed to DualNet's dual-NIC download engine,
/// so files grabbed while browsing still use Ethernet + Wi-Fi simultaneously.
///
/// On non-Windows platforms (dev machine, test runs) the control is inert and
/// <see cref="DualNet.Core.Engine.BrowserModePolicy"/> keeps the reader view.
/// </summary>
public sealed class RealWebView : NativeControlHost
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>WebView2 finished initializing — the engine is usable.</summary>
    public event Action? Ready;
    public event Action<string>? NavigationStarting;                    // uri
    public event Action<string>? NavigationCompleted;                   // final uri
    public event Action<string>? TitleChanged;
    public event Action? HistoryChanged;
    public event Action<string, string>? DownloadStarting;              // uri, suggested file name
    public event Action<string>? InitializationFailed;                  // reason (runtime missing…)

    public bool IsReady { get; private set; }

    /// <summary>How many times the engine finished initializing (2+ = re-attached after a tab switch).</summary>
    public int ReadyCount { get; private set; }

    public bool CanGoBack => _core?.CanGoBack ?? false;
    public bool CanGoForward => _core?.CanGoForward ?? false;
    public string? CurrentUrl => IsReady ? _core?.Source : _pendingUrl;

    private IntPtr _hwnd;
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private string? _pendingUrl;      // navigate target captured before the engine was ready
    private bool _destroyed;

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            // Predefined "STATIC" window class — a plain child surface for WebView2.
            _hwnd = CreateWindowExW(0, "STATIC", null,
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            InitAsync(_hwnd);
            return new PlatformHandle(_hwnd, "HWND");
        }
        return base.CreateNativeControlCore(parent);
    }

    private async void InitAsync(IntPtr hwnd)
    {
        try
        {
            // Throws when the Evergreen runtime is missing (WebView2RuntimeNotFoundException).
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true   // uBlock Origin Lite ships as a native browser extension
            };
            _env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder(),
                options: options).ConfigureAwait(true);

            _controller = await _env.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
            if (_destroyed)
            {
                try { _controller.Close(); } catch { }
                return;
            }
            _core = _controller.CoreWebView2;

            UpdateBounds();
            var s = _core.Settings;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsZoomControlEnabled = true;
            s.IsBuiltInErrorPageEnabled = true;
            s.IsStatusBarEnabled = false;
            s.IsScriptEnabled = true;

            _core.NavigationStarting += (_, e) =>
            {
                if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
                    return;
                NavigationStarting?.Invoke(e.Uri);
            };
            _core.NavigationCompleted += (_, _) =>
                NavigationCompleted?.Invoke(_core.Source ?? "");
            _core.DocumentTitleChanged += (_, _) =>
                TitleChanged?.Invoke(_core.DocumentTitle ?? "");
            _core.HistoryChanged += (_, _) => HistoryChanged?.Invoke();

            // Pop-ups and target=_blank stay inside this tab (browser-style navigation).
            _core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(e.Uri))
                    Navigate(e.Uri);
            };

            // The core feature: managed downloads never go through the browser's own
            // downloader — they go through the dual-NIC segment engine.
            _core.DownloadStarting += (_, e) =>
            {
                e.Handled = true;
                e.Cancel = true;
                string uri = "";
                string name = "";
                try
                {
                    uri = e.DownloadOperation?.Uri ?? "";
                    name = System.IO.Path.GetFileName(e.DownloadOperation?.ResultFilePath) ?? "";
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(uri))
                    DownloadStarting?.Invoke(uri, name);
            };

            _controller.IsVisible = true;
            IsReady = true;
            WasEverReady = true;
            ReadyCount++;

            _ = InstallUBlockLiteAsync();   // fire-and-forget — never blocks first paint

            if (!string.IsNullOrWhiteSpace(_pendingUrl))
            {
                var url = _pendingUrl;
                _pendingUrl = null;
                _core.Navigate(url);
            }
            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            InitializationFailed?.Invoke(ex.Message);
        }
    }

    public bool WasEverReady { get; private set; }

    /// <summary>
    /// Extracts the bundled uBlock Origin Lite (MV3) package to %LOCALAPPDATA% (idempotent,
    /// version-marked) and loads it into this WebView2 profile as a native browser extension.
    /// Returns the extension folder, or null when unavailable.
    /// </summary>
    private static string? EnsureUBlockLiteExtracted()
    {
        try
        {
            const string resourceName = "DualNet.App.Assets.Extensions.uBOLite.chromium.zip";
            using var zipStream = typeof(RealWebView).Assembly.GetManifestResourceStream(resourceName);
            if (zipStream is null)
                return null;

            using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);
            string version = "0";
            if (archive.GetEntry("manifest.json") is { } manifestEntry)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(manifestEntry.Open());
                version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "0" : "0";
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = AppContext.BaseDirectory;
            var root = Path.Combine(baseDir, "DualNet", "Extensions", "uBlockOriginLite");
            var marker = Path.Combine(root, ".dualnet-version");

            if (Directory.Exists(root) && File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == version)
                return root;   // already extracted at this version

            if (Directory.Exists(root))
                try { Directory.Delete(root, true); } catch { }
            Directory.CreateDirectory(root);
            archive.ExtractToDirectory(root, overwriteFiles: true);
            File.WriteAllText(marker, version);
            return root;
        }
        catch
        {
            return null;
        }
    }

    private async Task InstallUBlockLiteAsync()
    {
        try
        {
            var folder = EnsureUBlockLiteExtracted();
            if (folder is null || _core is null || _destroyed)
                return;
            await _core.Profile.AddBrowserExtensionAsync(folder).ConfigureAwait(true);
            SentrySetup.TrackDiagnostic("ublock_extension", "uBlock Origin Lite loaded from " + versionMarker(folder));
        }
        catch (Exception ex)
        {
            // Ad blocking is an enhancement — an older WebView2 runtime simply runs without it.
            SentrySetup.TrackDiagnostic("ublock_extension", "not loaded: " + ex.Message);
        }

        static string versionMarker(string folder) =>
            File.Exists(Path.Combine(folder, ".dualnet-version"))
                ? File.ReadAllText(Path.Combine(folder, ".dualnet-version")).Trim()
                : "?";
    }

    public void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!IsReady)
        {
            _pendingUrl = url;
            return;
        }
        try { _core!.Navigate(url); } catch { }
    }

    public void GoBack() { try { if (_core?.CanGoBack == true) _core.GoBack(); } catch { } }
    public void GoForward() { try { if (_core?.CanGoForward == true) _core.GoForward(); } catch { } }
    public void Reload() { try { _core?.Reload(); } catch { } }
    public void Stop() { try { _core?.Stop(); } catch { } }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        IsReady = false;
        try { _controller?.Close(); } catch { }
        _controller = null;
        _core = null;
        _env = null;
        if (_hwnd != IntPtr.Zero)
        {
            try { DestroyWindow(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty)
            UpdateBounds();
    }

    /// <summary>Keeps the WebView2 render target matched to the native child window.</summary>
    private void UpdateBounds()
    {
        if (_controller is null || !OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
            return;
        try
        {
            if (GetClientRect(_hwnd, out var r) && r.Right > 0 && r.Bottom > 0)
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, r.Right, r.Bottom);
        }
        catch { }
    }

    private static string UserDataFolder()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(
            string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir,
            "DualNet", "WebView2");
    }
}
