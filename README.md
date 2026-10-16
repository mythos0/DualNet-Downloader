# DualNet Downloader

**A full Chromium browser (CEF) with a built-in dual-NIC download manager, per-tab proxies, and uBlock Origin preinstalled.**

> v4.1.0 — full Chromium engine (CefSharp/CEF 138), real Chrome extension support, DuckDuckGo default search,
> per-tab proxy **and** per-tab network (NIC) selection beside the proxy icon, online free-proxy finder,
> a **local-AI browser automation agent**, and the battle-tested DualNet multi-NIC download engine.

![downloads](https://img.shields.io/badge/Windows-10%2F11-blue) ![tests](https://img.shields.io/badge/engine_tests-166%2F166-brightgreen)

## NEW in v4.1: AI browser automation agent (local, private)

Open the sparkle ✨ button (or Ctrl+Shift+K) and give it a task:

- *"Find the official documentation for react, vue and svelte — open them side by side."*
- *"Compare these three laptops in a table."*
- *"Download all PDFs from this page."*
- *"Open my daily work tabs and arrange them side by side."*

The agent plans and acts step-by-step on the real browser (search, navigate, read pages,
list links, extract tables, queue downloads, open tab sets, split view). It runs a
**local Ollama model (`qwen3.5:2b` by default)** — nothing leaves your machine.

- **Confirmation-first**: purchases, form submissions, deletions and any direct page
  interaction pause and ask before running (Approve / Skip / Cancel).
- **RAM-friendly**: the model is unloaded from memory automatically after idle
  (default 120 s) and the app's working set is trimmed — "Free RAM now" on the agent page
  does it instantly. Tune the model, endpoint and idle timeout in Settings → AI Agent.
- **Results you can use**: extracted tables render as a sortable grid; found links come
  with Open / Download buttons (downloads flow through the dual-NIC engine).

## Why v4 is different

v1–v3 used embedded engines that could not run real Chrome extensions and rendered pages through
foreign surfaces (WebView2 / reader-mode / offscreen shells). v4.0.0 is the real thing:

- **Full Chromium** — the actual CEF (Chromium 138) engine: same rendering, same scrolling smoothness,
  same PDF viewer, same site compatibility as Chrome.
- **Real extension support** — extensions load through Chromium's own extension system
  (`--load-extension`), the same mechanism Chrome uses for unpacked extensions.
  Electron and WebView2 cannot do this.
- **uBlock Origin preinstalled** — the real extension (MV2 build 1.75.0), plus uBlock Origin Lite
  (MV3) bundled; toggle them in `dualnet://app/extensions` (menu → Extensions).
- **DuckDuckGo by default** — the omnibox and the New Tab page search with DuckDuckGo out of the box
  (Google, Bing, Brave, DDG-Lite selectable in Settings).

## Downloads (NIC) — combined / net1 / net2 / etc.

Every download goes through the DualNet engine:

- Pick **Combined** to bond all networks, or **a single NIC** (Ethernet / Wi-Fi / manual providers) —
  the selector lives **in the toolbar of every tab, right beside the proxy globe**.
- The engine keeps a **global segment queue with work stealing**: no NIC is ever "lazy" — the fast one
  simply claims more segments, and dynamic adaptive scaling keeps adding connections while throughput
  improves (up to 64 per download).
- Downloads captured from web pages keep their **cookies / referer / user-agent**, so authenticated
  links (cloud drives, file hosts) still work.
- Pause / resume / retry / queue reorder / per-download speed limits / scheduled window /
  automatic per-category folders / per-download proxy — the professional download-manager checklist.

## Per-tab proxy with free proxies from online

Click the **globe icon** in any tab:

1. **Direct** — no proxy.
2. **Tested free proxies** — DualNet fetches ~1000 candidates from 8 public sources
   (ProxyScrape, TheSpeedX, monosans, Geonode, …), probes them concurrently, and lists the working
   ones with measured latency.
3. **Custom host:port** — with an inline reachability test.

Each tab's proxy is isolated through its own Chromium request context.

## Browser features

- Chrome-style shell: tab strip (pin / mute / duplicate / close others / middle-click close),
  omnibox with history + bookmark suggestions, bookmarks bar, downloads bubble + full manager,
  light/dark/system themes.
- **Session restore** — reopens your tabs after a crash or restart; Ctrl+Shift+T reopens closed tabs.
- **Private tabs** (Ctrl+Shift+N) — in-memory profile, gone on close.
- Find-in-page (Ctrl+F), zoom (Ctrl +/−/0), DevTools (F12), screenshots (Ctrl+F3), PDF viewer built in.
- Bookmarks import/export (Netscape HTML — compatible with Chrome/Firefox exports).
- Third-party cookie blocking, HTTPS upgrades, safe Chromium defaults.
- Hardware acceleration on (HwndHost windowed rendering — no offscreen bitmap copying).

## Keyboard shortcuts

| Keys | Action |
|------|--------|
| Ctrl+T / Ctrl+W | New tab / close tab |
| Ctrl+Shift+N / Ctrl+Shift+T | Private tab / reopen closed tab |
| Ctrl+L / Ctrl+J | Focus omnibox / downloads |
| Ctrl+F / Enter / Shift+Enter | Find in page / next / previous match |
| Ctrl+D / Ctrl+Shift+B | Bookmark page / toggle bookmarks bar |
| Ctrl+1…9 | Switch to tab |
| Ctrl+R / F5 | Reload |
| Ctrl + / − / 0 | Zoom |
| F12 | DevTools |
| Ctrl+F3 | Screenshot |
| Ctrl+Shift+K | AI agent |
| Ctrl+Shift+S | Split view (side-by-side tabs) |
| Alt+← / Alt+→ | Back / forward |

## Install

### Recommended: MSI installer (v4.1.2+)

1. Download and run **`DualNetDownloader-4.1.2-setup.msi`**.
2. Follow the wizard (installs per-user to `%LOCALAPPDATA%\Programs\DualNet Downloader` —
   no admin rights needed, Start Menu + Desktop shortcuts created automatically).
3. **Updating:** future versions install straight over the old one — the MSI
   supersedes the previous install automatically; no uninstall needed.

### Portable zip

1. Unzip `DualNetDownloader-4.1.2-win-x64.zip` into a folder (extract the WHOLE zip —
   don't run the exe from inside the zip viewer).
2. Run **`DualNet Downloader/DualNet.App.exe`**.

First launch extracts uBlock Origin and starts downloading fresh proxies in the background.

Requirements: Windows 10/11 x64. All runtime dependencies (including the VC++ runtime and
.NET 8) are bundled — no installer or extra download is needed.

### If the app does not open (v4.1.1 fixes + how to get a useful error)

- **What was broken before v4.1.1:** the release zip was missing `ijwhost.dll` and the
  VC++ runtime DLLs, and the dark-theme resource file was missing entirely — so on most
  machines (Windows dark mode) the app died silently before any window appeared. v4.1.1
  bundles every missing file and no longer fails silently. **v4.1.2 additionally fixed a
  crash in the main window XAML (a context menu declared as a layout child) that hit
  every user at launch** — with a proper error dialog + `%APPDATA%\DualNet\logs` report
  when anything does go wrong.
- If a startup error dialog appears, it names the exact problem and saves a full report to
  `%APPDATA%\DualNet\logs\crash-*.log` — include that file when reporting an issue.
- If Windows SmartScreen shows "Windows protected your PC" for the downloaded exe, click
  **More info → Run anyway** (the binary is unsigned).
- If you extracted only some files, re-extract the full zip; `libcef.dll`, `ijwhost.dll`
  and the `locales` folder must sit next to `DualNet.App.exe`.

Settings, cookies (per profile), bookmarks, history, sessions and the proxy cache live in
`%APPDATA%\DualNet`.

## Build

```bash
dotnet test tests/DualNet.Tests/DualNet.Tests.csproj     # 166 engine tests (Linux-friendly)
dotnet publish src/DualNet.App/DualNet.App.csproj -c Release -r win-x64 --self-contained \
  -p:EnableWindowsTargeting=true                          # Windows build (cross-builds from Linux)
```

Projects: `DualNet.Core` (engine + proxy service + stores, net8.0) · `DualNet.App` (WPF + CefSharp shell).

## License

MIT
