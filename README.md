# DualNet Downloader

**A Windows download manager that uses your Ethernet cable AND Wi-Fi adapter at the same time for maximum download speed.**

DualNet splits every file into byte-range segments and streams them **simultaneously through multiple network adapters**, then reassembles everything into the original file — byte-identical, verified by size. Each download tab can also be pinned to a single adapter (Ethernet-only or Wi-Fi-only).

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
- **Per-network tabs** — a `Combined` tab (uses every active adapter) plus one tab per
  detected adapter; downloads started in a tab use **only** that tab's network(s).
- **Dark + Light theme** — follows the system theme by default, switchable in the toolbar.
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
- **Sentry telemetry** — logs, metrics (`download_started/completed/canceled`,
  `download_duration`, `download_speed`) and global crash reporting wired in from launch.
- **Zero install** — one self-contained `.exe`, runs on any Windows 10/11 x64 machine.

## Install

Download **`DualNetDownloader-1.0.0-win-x64.exe`** from the
[Releases page](https://github.com/dualnet-bot/DualNet-Downloader/releases) and run it —
no .NET runtime or installer required.

## Usage

1. Pick a tab:
   - **⚡ Combined** — file is split across **all** adapters (default, fastest).
   - **🔌 <Ethernet name>** — downloads use only the Ethernet cable.
   - **📶 <Wi-Fi name>** — downloads use only the Wi-Fi adapter.
   - **🕘 History** — past downloads.
2. Paste one or more `http(s)://` URLs (one per line is fine) and press **⤓ Download**.
3. Watch progress, pause/resume anytime, click 📁 to reveal the file.

The toolbar shows every detected adapter as a chip (name, IP, negotiated link speed) and the
⚙ button opens settings (download folder, max simultaneous downloads, retry budget).

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
4. If the server doesn't support ranges, DualNet transparently falls back to one
   conventional stream.

> **Note:** aggregated throughput depends on the server/CDN allowing many parallel range
> requests and on your adapters sharing the same internet uplink. DualNet can't exceed what
> the remote server is willing to serve, but it removes the single-adapter bottleneck.

## Build from source

```bash
# Prerequisite: .NET 8 SDK
git clone https://github.com/dualnet-bot/DualNet-Downloader.git
cd DualNet-Downloader

dotnet build                                    # build
dotnet test                                     # 24 unit + integration + UI tests

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
│   │   ├── Engine/             #   DownloadJob, DownloadManager, SegmentPlanner, FileNameGuesser
│   │   ├── Net/                #   HttpConnectionFactory (per-adapter socket binding), adapter detection
│   │   ├── Storage/            #   JSON settings + history
│   │   └── Models/             #   shared models
│   └── DualNet.App/            # Avalonia 11 UI (MVVM, CommunityToolkit)
│       ├── Views/              #   MainWindow, DownloadTabView, SettingsWindow
│       ├── ViewModels/         #   MainViewModel, DownloadTabViewModel, DownloadItemViewModel…
│       ├── Controls/           #   SpeedGraph sparkline
│       └── SentrySetup.cs      # Sentry logs/metrics/crash reporting
└── tests/DualNet.Tests/        # xUnit + a real range-capable test HTTP server + headless UI tests
```

## Testing

The test suite (24 tests) includes a purpose-built HTTP server with real byte-range support
and covers: segment planning, file-name resolution, **dual-source-IP downloads verified by
SHA-256**, no-range fallback, pause → close → restore → resume, remote-file-changed
revalidation, automatic retry on 503s, the concurrency queue, and headless UI smoke tests.

## Privacy

Sentry error reporting uses the project's own DSN and captures app errors, download
lifecycle logs and metrics. Downloaded file contents and personal data are never sent.

## License

MIT — see [LICENSE](LICENSE).
