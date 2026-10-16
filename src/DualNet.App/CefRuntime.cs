using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CefSharp;
using DualNet.App.Services;
using DualNet.Core;

namespace DualNet.App;

/// <summary>
/// Bundled Chromium extensions (uBlock Origin / uBlock Origin Lite) shipped as zips in
/// Assets/Extensions and extracted to the user-data folder on first run. Loaded into the
/// default request context through the real Chromium extension system (--load-extension),
/// so full chrome.* background pages / webRequest filtering work — the reason the app
/// runs on CEF instead of WebView2 or Electron.
/// </summary>
public static class ExtensionManager
{
    public sealed record ExtensionInfo(string Id, string Name, string Version, string Dir, string ManifestPath, bool Bundled);

    private static List<ExtensionInfo>? _available;

    public static string ExtensionsDir => Path.Combine(AppBootstrapper.AppDataDir, "Extensions");

    /// <summary>Bundled zips: logical id → embedded resource file name.</summary>
    private static readonly (string Id, string FileName)[] Bundled =
    {
        ("uBlockOrigin", "uBlockOrigin-1.75.0.chromium.zip"),
        ("uBlockOriginLite", "uBlockOriginLite-2026.1012.chromium.zip"),
    };

    /// <summary>Extracts bundled zips (when missing) and scans both bundled + user folders.</summary>
    public static List<ExtensionInfo> Discover()
    {
        Directory.CreateDirectory(ExtensionsDir);

        foreach (var (id, fileName) in Bundled)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Extensions", fileName);
            var target = Path.Combine(ExtensionsDir, id);
            if (!File.Exists(source)) continue;
            if (!File.Exists(Path.Combine(target, "manifest.json")))
            {
                try
                {
                    Directory.CreateDirectory(target);
                    using var archive = ZipFile.OpenRead(source);
                    // zips may contain a top-level folder — flatten one level when needed
                    var entries = archive.Entries.Where(e => e.FullName.EndsWith("manifest.json", StringComparison.Ordinal)).ToList();
                    var prefix = entries.Count == 1
                        ? entries[0].FullName[..^"manifest.json".Length]
                        : "";
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // directories
                        var rel = prefix.Length > 0 && entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                            ? entry.FullName[prefix.Length..]
                            : entry.FullName;
                        if (rel.Length == 0 || rel.Contains("..")) continue;
                        var destPath = Path.Combine(target, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    SentrySetup.CaptureException(ex, "extension-extract:" + id);
                }
            }
        }

        // User-installed unpacked extensions live in ExtensionsDir\<Name>\ directly.
        var list = new List<ExtensionInfo>();
        foreach (var dir in Directory.EnumerateDirectories(ExtensionsDir))
        {
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                if (name.StartsWith("__MSG_"))
                {
                    // localized name (uBOLite) — resolve from _locales\en\messages.json
                    var resolved = ResolveLocalizedName(dir, name);
                    if (resolved is not null) name = resolved;
                }
                list.Add(new ExtensionInfo(Path.GetFileName(dir), name, version, dir, manifest,
                    Bundled.Any(b => b.Id == Path.GetFileName(dir))));
            }
            catch { }
        }
        _available = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return _available;
    }

    private static string? ResolveLocalizedName(string dir, string msgKey)
    {
        try
        {
            var key = msgKey.Replace("__MSG_", "").TrimEnd('_');
            foreach (var locale in new[] { "en_US", "en_GB", "en" })
            {
                var messages = Path.Combine(dir, "_locales", locale, "messages.json");
                if (!File.Exists(messages)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(messages));
                if (doc.RootElement.TryGetProperty(key, out var entry) &&
                    entry.TryGetProperty("message", out var m))
                    return m.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Directories to pass to --load-extension, honoring the enabled set in settings.</summary>
    public static List<string> EnabledExtensionDirs()
    {
        var all = Discover();
        var enabled = AppBootstrapper.Settings.EnabledExtensions;
        var dirs = new List<string>();
        foreach (var ext in all)
        {
            if (ext.Bundled && enabled.Count > 0 && !enabled.Contains(ext.Id, StringComparer.Ordinal))
                continue;   // bundled extensions are opt-out; user-installed always load
            dirs.Add(ext.Dir);
        }
        return dirs;
    }

    public static List<ExtensionInfo> Available => _available ?? Discover();
}

/// <summary>CEF bootstrap/shutdown and per-tab request contexts.</summary>
public static class CefRuntime
{
    private static bool _initialized;

    public static bool IsInitialized => _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        var settings = new CefSharp.Wpf.HwndHost.CefSettings
        {
            CachePath = Path.Combine(AppBootstrapper.AppDataDir, "CEF"),
            Locale = "en-US",
        };

        // Real browser UA (strip the CefSharp marker so sites treat it as Chrome).
        settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                             "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 DualNet/4.0";

        // Full extension support through Chromium's own loader.
        var extDirs = ExtensionManager.EnabledExtensionDirs();
        if (extDirs.Count > 0)
            settings.CefCommandLineArgs.Add("load-extension", string.Join(",", extDirs));

        // Privacy: block third-party cookies (phaseout flag), PDF in isolated iframe.
        if (AppBootstrapper.Settings.BlockThirdPartyCookies)
            settings.CefCommandLineArgs.Add("test-third-party-cookie-phaseout", "1");
        settings.CefCommandLineArgs.Add("enable-features", "PdfOopif");

        // Smooth rendering: leave GPU acceleration ON (no disable-gpu switches).
        settings.CachePath = Path.Combine(AppBootstrapper.AppDataDir, "CEF");

        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
        _initialized = true;
    }

    /// <summary>
    /// Creates a request context for a tab. When <paramref name="privateTab"/> is false the
    /// context SHARES the default context's storage (one browser profile: cookies, cache,
    /// and the loaded extensions all apply) while still allowing per-tab proxy rules.
    /// Private tabs get an in-memory context that is fully isolated and vanishes on close.
    /// </summary>
    public static IRequestContext CreateTabContext(bool privateTab, string? tabCacheDir = null)
    {
        if (privateTab)
        {
            return new RequestContext(new RequestContextSettings
            {
                PersistSessionCookies = false,
                // no CachePath = fully in-memory (incognito)
            });
        }
        // Shared with the default (global) context: same profile storage → extensions +
        // cookies behave like one browser; proxy remains configurable per context.
        return new RequestContext(Cef.GetGlobalRequestContext());
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        try { Cef.Shutdown(); } catch { }
        _initialized = false;
    }
}
