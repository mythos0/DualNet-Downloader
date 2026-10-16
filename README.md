# DualNet Downloader

**A full Chromium browser (CEF) with a built-in dual-NIC download manager, per-tab proxies, and uBlock Origin preinstalled.**

> v4.0.0 — full Chromium engine (CefSharp/CEF 138), real Chrome extension support, DuckDuckGo default search,
> per-tab proxy **and** per-tab network (NIC) selection beside the proxy icon, online free-proxy finder,
> and the battle-tested DualNet multi-NIC download engine.

![downloads](https://img.shields.io/badge/Windows-10%2F11-blue) ![tests](https://img.shields.io/badge/engine_tests-151%2F151-brightgreen)

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
| Alt+← / Alt+→ | Back / forward |

## Install

1. Unzip `DualNetDownloader-4.0.0-win-x64.zip` anywhere.
2. Run **`DualNet Downloader/DualNet.App.exe`**.
3. Optional: the first launch extracts uBlock Origin and starts downloading fresh proxies in the
   background.

Requirements: Windows 10/11 x64. If the app fails to start, install the
[Visual C++ 2015–2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe).

Settings, cookies (per profile), bookmarks, history, sessions and the proxy cache live in
`%APPDATA%\DualNet`.

## Build

```bash
dotnet test tests/DualNet.Tests/DualNet.Tests.csproj     # 151 engine tests (Linux-friendly)
dotnet publish src/DualNet.App/DualNet.App.csproj -c Release -r win-x64 --self-contained \
  -p:EnableWindowsTargeting=true                          # Windows build (cross-builds from Linux)
```

Projects: `DualNet.Core` (engine + proxy service + stores, net8.0) · `DualNet.App` (WPF + CefSharp shell).

## License

MIT
