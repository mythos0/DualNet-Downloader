# DualNet Downloader

A **full Chromium browser** (Electron) that looks and feels like Chrome, with a **multi-network download manager** built in — and **per-tab proxies** with a live-tested free proxy list.

![DualNet Downloader](https://raw.githubusercontent.com/mythos0/DualNet-Downloader/main/screenshots/v3-light-newtab.png)

## Why v3 is a rewrite

v1/v2 were C#/.NET with embedded browser panels (reader-view, then WebView2) — close, but never a *real* browser. v3 switches the tech stack completely:

| | v1/v2 (.NET + Avalonia) | **v3 (Electron)** |
|---|---|---|
| Engine | HTML parser / WebView2 host | **Actual Chromium** (every tab is a full browser) |
| UI | Avalonia chrome imitation | **Pixel-level Chrome-style shell** (tabs, omnibox, dark mode) |
| Per-tab proxy | not possible | **Isolated Chromium session per tab + proxy rules** |
| Free proxies | — | **Fetch from 5 online sources → latency-test → keep working ones** |
| Web compatibility | partial | **100%** (gradients, video, web apps, logins) |

## Features

### Real Chromium browsing
- Chrome-identical UI: tab strip with rounded tabs, ＋ new tab, omnibox with lock/search chip, back/forward/reload, downloads button with badge, ⋮ menu, custom window controls, light/dark/system themes
- Bookmarks bar + star, history page (`dualnet://history`), new-tab shortcuts, Google search from the omnibox or NTP
- Every tab is an **isolated Chromium session** (own cookies/storage — close a tab, leave nothing behind)
- `will-download` interception: clicking any download link routes the file straight into the DualNet engine (with the page's cookies/referer/UA preserved)

### Multi-network download manager
- **Combined / per-NIC selection**: pick *Combined* (all adapters), or a single adapter (`net1`, `net2`, …) per download
- **Dynamic segment stealing** — the fix for "segments permanently divided between networks": files are split into many 1 MiB+ segments pushed into one global queue; every idle connection grabs the next pending segment. A fast NIC simply claims more segments; nothing sits lazy while the slow one crawls. *(Pinned by integration test `WORK STEALING: fast NIC claims majority of segments`.)*
- Multi-connection HTTP(S) with redirect following, resume (`.dualnet.json` sidecar meta survives app restarts), pause/resume/cancel/retry, SHA-verified writes, per-NIC live speed chips on every download card
- Per-download connection slider (2–32), global default in settings
- Downloads popup (⤓) + full downloads page with filters + crash-recovery resume of unfinished files

### Per-tab proxy
- Globe icon in the toolbar → proxy for the **current tab only**
- **Fetch & test free proxies online**: pulls candidate lists from TheSpeedX, monosans, ProxyScrape, proxy-list.download and geonode (≈900 candidates), validates them concurrently against a generate_204 endpoint and keeps the fastest responding ones with latency + country
- Or set a custom `host:port` (tested inline before applying)
- Tabs without a proxy are untouched; downloads always use your real networks

### Speed test
- Per-adapter throughput test (Cloudflare payload bound to each NIC) + combined sum

### Crash reporting
- Built-in Sentry client pointed at the project DSN (`DUALNET_SENTRY_DSN` env override, disable in Settings → Advanced). Verified: events accepted with HTTP 200 at release `dualnet-downloader@3.0.0`.

## Keyboard shortcuts
`Ctrl+T` new tab · `Ctrl+W` close tab · `Ctrl+L` omnibox · `Ctrl+J` downloads · `Ctrl+R`/`F5` reload · `Ctrl+Tab` cycle tabs · `Alt+←/→` history · `F12` devtools

## Build

```bash
npm install
npm start            # run from source
npm test             # headless engine test suite (9 tests)
npm run dist:win     # Windows portable + NSIS installer (x64)
```

## Notes
- Free public proxies are untrusted — avoid typing passwords while one is active. Dead proxies are filtered by the tester, but reliability changes minute-to-minute.
- Downloads deliberately bypass tab proxies so both NICs can aggregate; a site that only responds via proxy will need the file link copied into *Add download*.
- Windows binaries are produced on CI/sandbox without code signing (SmartScreen may warn — click *More info → Run anyway*).

## License
MIT — see [LICENSE](LICENSE).
