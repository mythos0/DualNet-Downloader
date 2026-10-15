# DualNet Downloader

**A Windows download manager that uses your Ethernet cable AND Wi-Fi adapter at the same time for maximum download speed.**

DualNet splits every file into byte-range segments and streams them **simultaneously through multiple network adapters**, then reassembles everything into the original file — byte-identical, verified by size. Segments are **never permanently assigned to one network**: the moment a connection finishes a piece, it immediately claims the next piece — from whichever network still has work — so a fast NIC never sits idle while a slow one crawls. Each download tab can also be pinned to a single adapter (Ethernet-only or Wi-Fi-only).

The app itself is a **Chrome-style browser**: Chromium (WebView2) renders every tab, uBlock Origin Lite ships preinstalled, and the download manager lives in the downloads shelf and the ⋮ menu — a real browser with dual-NIC downloading built in.

```
                  ┌──────────────────────┐
                  │   bigfile.zip        │
                  │   8/16 segments      │
                  └──────────┬───────────┘
              ┌──────────────┴───────────────┐
              ▼                              ▼
     ┌─────────────────┐            ┌─────────────────┐
     │  Ethernet NIC   │            │   Wi-Fi NIC     │
     │  192.168.1.10   │            │  192.168.0.5    │
     │  seg 0,2,4,6…   │            │  seg 1,3,5,7…   │
     └────────┬────────┘            └────────┬────────┘
              └──────────►  Internet  ◄──────┘
                     (aggregated bandwidth)
```

## Features

- **True dual-adapter downloading** — every TCP connection is force-bound to a specific
  adapter's IP (`SocketsHttpHandler.ConnectCallback` + local socket bind), so the OS routes
  each segment through the cable *and* the radio at the same time.
- **Automatic file splitting** — 8 segments below 500 MB, 16 above, never smaller than 2 MB
  each, interleaved across adapters (segment *i* → adapter *i % n*).
- **Chrome-style UI (v2.0.0)** — the whole window is rebuilt to look and behave like Google
  Chrome: a real **tab strip** (rounded Chrome tabs, favicon/glyph, ✕ close, ＋ new tab,
  Ctrl+T / Ctrl+W / Ctrl+L), a Chrome **toolbar** (◀ ▶ ↻/✕ ⌂ + omnibox with lock icon,
  ★ bookmark, ⤓ downloads button with a red active-count badge, 🌐 networks flyout, ⋮ menu),
  a Chrome-style **New Tab page** (search box + shortcut tiles), and a **downloads shelf**
  docked under the page that shows per-NIC speeds, a live speed graph and every download card.
  A 🌐 flyout lists all networks (add/remove manual providers, rescan), and the ⋮ menu holds
  pause/resume/retry-all, clipboard monitor, auto-shutdown, theme, history export and settings.
- **Real Chromium browser in every tab** — tabs host **Microsoft Edge WebView2** (the same
  engine as Edge/Chrome: full JavaScript, CSS, cookies, logins, PDFs).
  Every managed **download is intercepted** and routed into DualNet's dual-NIC segment engine,
  so files grabbed while browsing still use Ethernet + Wi-Fi simultaneously. Pop-ups and
  target=_blank stay in-tab. On machines without the WebView2 Runtime (or when disabled in
  settings) tabs fall back to the **built-in reader browser**, fetched through the tab's own
  adapters: address bar with search fallback (DuckDuckGo Lite / Bing / Google), Back ◀ /
  Forward ▶ / Reload ↻ / Stop ✕ / Home ⌂, reader-view rendering (headings, paragraphs, lists,
  quotes, code, images, tables), a **Links panel** with a `Files only` filter and one-click
  **⤓ download through the tab's network**, automatic **file detection**, **bookmarks** (☆),
  **browser history**, address-bar suggestions, **📖 reader view** on demand, and
  **↗ open in system browser** as a last resort. Switching tabs and coming back restores the
  page automatically.
- **uBlock Origin Lite preinstalled (v2.0.0)** — the official **uBOLite MV3** package
  (GPLv3, © Raymond Hill & contributors) ships inside the app: on first run it is extracted
  to `%LOCALAPPDATA%\DualNet\Extensions\uBlockOriginLite` and loaded as a **native WebView2
  browser extension** (`AreBrowserExtensionsEnabled`), so ads and trackers are filtered in
  every Chromium tab out of the box — exactly like installing the Chrome extension, no setup.
  Updates replace the extracted package when a new build ships with a newer uBOLite version.
- **Dynamic segment scheduling v3 (max combined speed, no lazy NICs)** — every connection
  is a worker that **loops**: the moment it finishes its piece it atomically claims the tail
  of the **largest remaining piece — from any adapter** (claim-on-finish, zero idle gaps).
  Segments are never permanently divided per network; the fast NIC keeps pulling work until
  the last byte. On top of that, the scaler keeps evaluating throughput with EMA-smoothed
  multi-second windows: if extra connections buy more bandwidth it **opens new connections**
  by splitting the largest remaining segments (up to 64, default cap 32 — configurable in
  settings), escalates while the gains keep coming, and a scaler that hits a server-side cap
  **stands down gracefully but re-probes every ~30 s**. Combined downloads show the **live
  per-NIC Mbps share** right on the download card. This is what lets Combined downloads
  actually saturate Ethernet + Wi-Fi even when the server caps throughput per connection
  (IDM-style dynamic file segmentation).
- **Clipboard link watcher** — copy a link anywhere and a one-click **⤓ Download** banner
  appears in the status bar (toggle 📋 in the toolbar).
- **Bulk actions** — ⏸ pause all, ▶ resume all, ↻ retry failed, 🧹 clear finished — per tab.
- **Per-download extras** — copy URL, download again (restart), remove from list, and
  **SHA-256 integrity check** on completed files (hash is copied to the clipboard).
- **Global speed limiter** — cap the combined throughput of *all* adapters and jobs
  (settings, KB/s, 0 = unlimited).
- **Auto shutdown** — toggle ⏻ to shut Windows down once every download has finished
  (great for big overnight grabs; abort with `shutdown /a`).
- **Per-network tabs** — a `Combined` tab (uses every active adapter) plus one tab per
  detected adapter; downloads started in a tab use **only** that tab's network(s).
- **Auto-refreshing networks** — the app rescans adapters every 5 s: plug in a cable or join
  a Wi-Fi network and **its tab appears automatically** (a toast announces it); when a
  network disappears, its tab is removed and its downloads move to the Combined tab. The
  header **↻** button rescans on demand.
- **Manual network providers** — for networks the auto-scan does not offer (VPN tunnels,
  virtual switches, multi-IP NICs, adapters without a gateway): click **＋** in the header,
  give the provider a name and a local IPv4 address, and it becomes a first-class network
  chip + tab (greyed out while that IP is not present on the PC; never receives download
  binds while offline).
- **Dark + Light theme** — follows the system theme by default, switchable in the toolbar.
  All secondary text colors come from the app's own contrast-guaranteed palettes — nothing
  hides on either background.
- **Pause / Resume** — segment progress is persisted to disk; you can pause, close the app,
  reboot, and resume where you left off (guarded by ETag / Last-Modified revalidation).
- **Auto-retry** — failed segments are retried up to 5 times with exponential backoff,
  including stalled-connection watchdogs (90 s without bytes → reconnect).
- **Download queue** — up to 3 simultaneous downloads by default (configurable).
- **Crash-safe recovery** — unfinished downloads are detected at startup and restored as
  paused jobs.
- **Live speed graph** — per-tab sparkline plus total throughput in the status bar.
- **History** — completed/failed downloads recorded locally (JSON), with a History tab.
- **Single-stream fallback** — servers without HTTP Range support are downloaded normally.
- **Per-adapter speed test** — one-click throughput test per network, bounded by a payload
  cap and an 8-second window with live MB progress, friendly error messages (DNS, blocked,
  refused — never a silent "failed"), and a custom endpoint option in settings.
- **IDM-style scheduler** — restrict downloading to a daily time window (e.g. overnight),
  with automatic pause/resume at the window edges.
- **Category folders** — optionally sort finished files into `Video/`, `Audio/`,
  `Compressed/`, `Documents/`, `Programs/`, `Images/` sub-folders (IDM-style).
- **Local integration API** — optional localhost HTTP endpoint (`GET /add?url=…`, default
  port 45454) so browsers, scripts and other apps can push downloads into DualNet.
- **Proxy support** — system proxy or a manual proxy host/port for all download traffic.
- **Queue reordering** — move waiting downloads up/down (IDM-style priority).
- **Sentry telemetry** — logs, metrics (`download_started/completed/canceled`,
  `download_duration`, `download_speed`) and global crash reporting wired in from launch.
  The shipped DSN tracks the current project; stale persisted DSNs from retired projects are
  migrated automatically at startup (a rotated DSN is rejected server-side, which used to look
  like "nothing appears in Sentry"). Settings → Telemetry can point anywhere else, and
  **Send test event** verifies the pipeline end-to-end.
- **Zero install** — one self-contained `.exe`, runs on any Windows 10/11 x64 machine.

## Install

Download **`DualNetDownloader-2.0.0-win-x64.exe`** from the
[Releases page](https://github.com/mythos0/DualNet-Downloader/releases) and run it —
no .NET runtime or installer required. Full browsing uses the **Evergreen WebView2 Runtime**
(preinstalled on Windows 10/11; the app falls back to the reader browser when absent).

## Usage

1. Pick a tab:
   - **⚡ Combined** — file is split across **all** adapters (default, fastest).
   - **🔌 <Ethernet name>** — downloads use only the Ethernet cable.
   - **📶 <Wi-Fi name>** — downloads use only the Wi-Fi adapter.
   - **🌐 New tab** (＋) — a fresh Chromium browser tab over all networks.
   - **🕘 History** — past downloads.
2. **Browse or download from the omnibox** — Enter opens the page in the tab's real
   Chromium browser (with uBlock Origin Lite already filtering ads; falls back to the
   reader view when WebView2 is unavailable; ◀ ▶ ↻/✕ ⌂ navigate like any browser); when
   you land on a file, the segmented download starts automatically over that tab's networks.
3. **⤓ toolbar button** — show/hide the downloads shelf (live per-NIC speeds, speed graph,
   every download card). The red badge shows how many downloads are running.
   **⤓ Download** queues the links directly without browsing. Non-URL input searches via
   DuckDuckGo Lite / Bing / Google (choose in settings). Multiple links (one per line) become
   multiple downloads. The **Links panel** on the right lists every link of the open page —
   switch it to `Files only` and grab what you need with one click.
3. Watch progress, pause/resume anytime, then open the file ⤢, reveal it 📁, verify its
   SHA-256 #, or download it again ↻.

The toolbar shows every detected adapter as a chip (name, IP, negotiated link speed), the
📋 and ⏻ toggles (clipboard watcher / auto shutdown) and the ⚙ button opens settings
(download folder, max simultaneous downloads, retry budget, speed limit, clipboard watcher,
auto shutdown).

## How the dual-network magic works

1. DualNet enumerates your adapters (`NetworkInterface.GetAllNetworkInterfaces()`), keeping
   up-and-running Ethernet/Wi-Fi interfaces that own an IPv4 and a default gateway.
2. For each adapter it builds a dedicated `HttpClient` whose `ConnectCallback` creates the
   TCP socket, **binds it to that adapter's local IP**, then connects. TLS/SNI still uses the
   original host name, so HTTPS just works.
3. The file is split into ranges via `Range: bytes=…` requests; segment *i* is downloaded by
   adapter *i % adapterCount*. Every segment streams directly into its final offset of a
   pre-allocated file — the "merge" is implicit and the finished file is byte-identical to
   the original.
4. **Dynamic scaling**: a background loop watches throughput every 2 s — if more connections
   still buy bandwidth, the largest remaining segments are split in half and fresh connections
   open on the least-busy adapter; if an adapter runs out of work entirely, it steals half of
   a busy segment from the other adapter. When extra connections stop helping (server cap
   reached), scaling stops. Pause/resume persists the split segment list.
5. If the server doesn't support ranges, DualNet transparently falls back to one
   conventional stream.

> **Note:** aggregated throughput depends on the server/CDN allowing many parallel range
> requests and on your adapters sharing the same internet uplink. DualNet can't exceed what
> the remote server is willing to serve, but it removes the single-adapter bottleneck.

## Build from source

```bash
# Prerequisite: .NET 8 SDK
git clone https://github.com/mythos0/DualNet-Downloader.git
cd DualNet-Downloader

dotnet build                                    # build
dotnet test                                     # 46 unit + integration + UI tests

# Windows single-file release (also works cross-compiling from Linux/macOS):
dotnet publish src/DualNet.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

## Project layout

```
DualNet-Downloader/
├── src/
│   ├── DualNet.Core/           # engine — no UI dependencies
│   │   ├── Engine/             #   DownloadJob, DownloadManager, SegmentPlanner, AdaptiveScalePolicy
│   │   │   └── Browser/        #   BrowserService, HtmlDocumentParser (reader view), LinkClassifier
│   │   ├── Net/                #   HttpConnectionFactory (per-adapter socket binding), adapter detection
│   │   ├── Storage/            #   JSON settings + history
│   │   └── Models/             #   shared models
│   └── DualNet.App/            # Avalonia 11 UI (MVVM, CommunityToolkit)
│       ├── Views/              #   MainWindow, DownloadTabView, BrowserPane, SettingsWindow
│       ├── ViewModels/         #   MainViewModel, DownloadTabViewModel, DownloadItemViewModel…
│       ├── Controls/           #   SpeedGraph sparkline
│       └── SentrySetup.cs      # Sentry logs/metrics/crash reporting
└── tests/DualNet.Tests/        # xUnit + a real range-capable test HTTP server + headless UI tests
```

## Testing

The test suite (136 tests) includes a purpose-built HTTP server with real byte-range and
per-source-IP throttling, and covers: segment planning, **adaptive scale policy + end-to-end
work-stealing/splitting downloads verified by SHA-256**, the full browser pipeline (HTML
parsing, link classification, file detection, bookmarks/history stores, headless browser
view-model navigation), the speed-test service (success, refused endpoint, payload cap), the
global speed limiter, URL normalization, **Sentry integration with a recording transport**
(events, exceptions, structured logs, metrics), file-name resolution, **dual-source-IP
downloads**, no-range fallback, pause → close → restore → resume, remote-file-changed
revalidation, automatic retry on 503s, the concurrency queue, and headless UI smoke tests.

## Privacy

Sentry error reporting uses the project's own DSN and captures app errors, download
lifecycle logs and metrics. Downloaded file contents and personal data are never sent.

## License

MIT — see [LICENSE](LICENSE).
