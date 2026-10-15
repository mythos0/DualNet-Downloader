'use strict';
// TabManager: Chrome-like tabs on top of WebContentsView.
//  - every tab gets its OWN Chromium session (partition `tab-<id>`) so that
//    per-tab proxy rules and cookies are fully isolated;
//  - the chrome UI (tab strip / toolbar) is itself a WebContentsView kept
//    stacked ABOVE page views;
//  - internal pages (newtab/downloads/settings/...) are rendered by the
//    chrome view and hide the page view while active.
const { WebContentsView, session, app } = require('electron');
const path = require('path');
const { EventEmitter } = require('events');

const INTERNAL_PAGES = ['newtab', 'downloads', 'settings', 'speedtest', 'history'];

function stripElectronFromUA(ua) {
  return ua
    .replace(/\s*Electron\/[\d.]+/i, '')
    .replace(/\s*DualNet\/[\d.]+/i, '')
    .trim() + ' DualNet/3.0';
}

class TabManager extends EventEmitter {
  constructor({ win, store, sentry, onPageDownload }) {
    super();
    this.win = win;
    this.store = store;
    this.sentry = sentry;
    this.onPageDownload = onPageDownload; // (info) => void
    this.tabs = new Map();       // id -> tab
    this.order = [];             // tab ids in strip order
    this.activeId = null;
    this.chromeHeight = 88;
    this._nextId = 1;
    this.chromeView = null;

    this._buildChromeView();
  }

  // ---------------- chrome UI view ----------------
  _buildChromeView() {
    this.chromeView = new WebContentsView({
      webPreferences: {
        preload: path.join(__dirname, '..', 'preload', 'preload.js'),
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: false, // preload uses only electron APIs; sandbox true breaks ipcRenderer.invoke? (it works) keep false for safety
        backgroundThrottling: false,
      },
    });
    this.chromeView.setBackgroundColor('#00000000'); // transparent below the chrome header
    this.chromeView.webContents.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));
    this.win.contentView.addChildView(this.chromeView);
    this.chromeView.webContents.on('before-input-event', (e, input) => {
      if (input.type !== 'keyDown') return;
      const ctrl = input.control || input.meta;
      const k = (input.key || '').toLowerCase();
      const stop = () => { e.preventDefault(); };
      if (ctrl && k === 't') { stop(); this.createTab({}); return; }
      if (ctrl && k === 'w') { stop(); this.closeTab(this.activeId); return; }
      if (ctrl && k === 'l') { stop(); this.chromeView.webContents.focus(); this.chromeView.webContents.send('ui:focus-omnibox'); return; }
      if (ctrl && k === 'j') { stop(); this.chromeView.webContents.send('ui:open-downloads'); return; }
      if (ctrl && k === 'r') { stop(); this.reload(this.activeId); return; }
      if (ctrl && k === 'tab') { stop(); this.cycleTab(input.shift ? -1 : 1); return; }
      if (input.alt && input.key === 'ArrowLeft') { stop(); this.goBack(this.activeId); return; }
      if (input.alt && input.key === 'ArrowRight') { stop(); this.goForward(this.activeId); return; }
      if (input.key === 'F5') { stop(); this.reload(this.activeId); return; }
      if (input.key === 'F12') { stop(); this.toggleDevtools(this.activeId); return; }
    });
  }

  setChromeHeight(px) {
    this.chromeHeight = Math.max(0, Math.round(px || 0));
    this.layout();
  }

  layout() {
    if (!this.win || this.win.isDestroyed()) return;
    const { width, height } = this.win.getContentBounds();
    const chromeH = this.win.isFullScreen() ? 0 : this.chromeHeight;
    // chrome view spans the whole window; its DOM is transparent below the header
    this.chromeView.setBounds({ x: 0, y: 0, width, height });
    for (const tab of this.tabs.values()) {
      if (tab.view) {
        tab.view.setBounds({ x: 0, y: chromeH, width, height: Math.max(0, height - chromeH) });
      }
    }
  }

  // ---------------- sessions ----------------
  _sessionFor(tab, inheritedPartition) {
    if (tab._session) return tab._session;
    const partition = inheritedPartition || `tab-${tab.id}`;
    const ses = session.fromPartition(partition);
    const ua = stripElectronFromUA(app.userAgentFallback || ses.getUserAgent());
    try { ses.setUserAgent(ua); } catch {}
    if (!ses._dualnetWired) {
      ses._dualnetWired = true;
      ses.setPermissionRequestHandler((wc, permission, cb) => {
        const deny = ['geolocation', 'notifications', 'mediaKeySystem'];
        cb(!deny.includes(permission));
      });
      ses.on('will-download', (event, item, wc) => {
        // read info BEFORE preventDefault (item is unusable afterwards)
        const info = {
          url: item.getURL(),
          filename: item.getFilename(),
          mimeType: item.getMimeType(),
          total: item.getTotalBytes(),
          tabId: this._tabIdForWebContents(wc),
          headers: {},
        };
        // reconstruct auth context from the browsing session (cookies + UA + referer)
        try {
          const cookies = ses.cookies.get({ url: info.url });
          const cookieStr = cookies.map(c => `${c.name}=${c.value}`).join('; ');
          if (cookieStr) info.headers.Cookie = cookieStr;
          info.headers['User-Agent'] = ses.getUserAgent();
          const pageUrl = (wc && wc.getURL && wc.getURL()) || '';
          if (pageUrl.startsWith('http')) info.headers.Referer = pageUrl;
        } catch {}
        event.preventDefault(); // cancel Chromium's downloader; route to our manager
        try { this.onPageDownload(info); } catch (e) { this.sentry.captureException(e, { where: 'will-download' }); }
      });
    }
    tab._session = ses;
    tab.partition = partition;
    return ses;
  }

  _tabIdForWebContents(wc) {
    for (const tab of this.tabs.values()) {
      if (tab.view && tab.view.webContents === wc) return tab.id;
    }
    return null;
  }

  // ---------------- tab lifecycle ----------------
  createTab({ url, activate = true, background = false, inheritedPartition = null } = {}) {
    const id = this._nextId++;
    const tab = {
      id, kind: 'web', title: 'New Tab', url: url || 'dualnet://newtab',
      favicon: null, loading: false, canBack: false, canFwd: false,
      proxy: null, // null | 'host:port' | custom rules
      view: null, _session: null, partition: null, createdAt: Date.now(),
    };
    this.tabs.set(id, tab);
    this.order.push(id);

    const ses = this._sessionFor(tab, inheritedPartition);
    const view = new WebContentsView({
      webPreferences: {
        partition: tab.partition,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        autoplayPolicy: 'no-user-gesture-required',
      },
    });
    tab.view = view;
    const wc = view.webContents;
    this._wireWebContents(tab, wc);

    // page view sits BELOW chrome view
    this.win.contentView.addChildView(view);
    // chrome always on top
    this.win.contentView.removeChildView(this.chromeView);
    this.win.contentView.addChildView(this.chromeView);

    if (activate !== false && !background) this.activate(id, { silent: true });
    this.layout();
    const startUrl = url || 'dualnet://newtab';
    if (startUrl.startsWith('dualnet://')) {
      // web tab that shows an internal page initially
      tab.url = startUrl;
      this._markInternal(tab, startUrl);
    } else {
      wc.loadURL(startUrl).catch(() => {});
    }
    this.broadcast();
    return id;
  }

  _wireWebContents(tab, wc) {
    wc.on('page-title-updated', (e, title) => { tab.title = title || tab.title; this.broadcast(); });
    wc.on('page-favicon-updated', (e, icons) => { tab.favicon = icons && icons.length ? icons[icons.length - 1] : tab.favicon; this.broadcast(); });
    const onNav = (e, url) => {
      tab.url = url;
      tab.canBack = wc.navigationHistory.canGoBack();
      tab.canFwd = wc.navigationHistory.canGoForward();
      this.broadcast();
      if (url.startsWith('http')) {
        try {
          const u = new URL(url);
          this.store.pushHistory({ title: tab.title || u.hostname, url, ts: Date.now() });
        } catch {}
      }
    };
    wc.on('did-navigate', onNav);
    wc.on('did-navigate-in-page', (e, url, isMainFrame) => { if (isMainFrame !== false) onNav(e, url); });
    wc.on('did-start-loading', () => { tab.loading = true; this.broadcast(); });
    wc.on('did-stop-loading', () => {
      tab.loading = false;
      tab.canBack = wc.navigationHistory.canGoBack();
      tab.canFwd = wc.navigationHistory.canGoForward();
      this.broadcast();
    });
    wc.on('did-fail-load', (e, code, desc, validatedURL, isMainFrame) => {
      if (isMainFrame === false || code === -3) return; // subframe or aborted
      const errPage = errorPageHtml(validatedURL || tab.url, code, desc);
      wc.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(errPage)).catch(() => {});
    });
    wc.on('render-process-gone', (e, details) => {
      this.sentry.captureMessage('render-process-gone', 'warning', details);
      const errPage = errorPageHtml(tab.url, -2, 'The renderer process crashed (' + details.reason + ')');
      wc.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(errPage)).catch(() => {});
    });
    wc.setWindowOpenHandler(({ url, disposition }) => {
      if (url.startsWith('http') || url.startsWith('file')) {
        this.createTab({ url, background: disposition !== 'foreground-tab' });
      }
      return { action: 'deny' };
    });
    wc.on('enter-html-full-screen', () => this.layout());
    wc.on('leave-html-full-screen', () => this.layout());
  }

  _markInternal(tab, url) {
    tab.kind = 'internal';
    tab.page = url.replace('dualnet://', '').replace(/\/$/, '');
    tab.title = ({ newtab: 'New Tab', downloads: 'Downloads', settings: 'Settings', speedtest: 'Speed Test', history: 'History' })[tab.page] || tab.page;
  }

  closeTab(id) {
    const tab = this.tabs.get(id);
    if (!tab) return;
    const idx = this.order.indexOf(id);
    this.order = this.order.filter(x => x !== id);
    this.tabs.delete(id);
    if (tab.view) {
      try { this.win.contentView.removeChildView(tab.view); } catch {}
      const ses = tab._session;
      try { tab.view.webContents.close(); } catch {}
      tab.view = undefined;
      if (ses) { try { ses.clearStorageData(); } catch {} try { ses.closeAllConnections(); } catch {} }
    }
    if (this.order.length === 0) {
      this.createTab({});
      return;
    }
    if (this.activeId === id) {
      this.activate(this.order[Math.min(idx, this.order.length - 1)]);
    } else this.broadcast();
  }

  closeOthers(id) {
    for (const t of [...this.order]) if (t !== id) this.closeTab(t);
  }

  duplicateTab(id) {
    const tab = this.tabs.get(id);
    if (!tab) return;
    this.createTab({ url: tab.url && !tab.url.startsWith('dualnet://') ? tab.url : undefined });
  }

  activate(id, { silent = false } = {}) {
    if (!this.tabs.has(id)) return;
    this.activeId = id;
    const t = this.tabs.get(id);
    // web views visible only for the active web tab; internal tabs show chrome-owned pages
    for (const tab of this.tabs.values()) {
      if (!tab.view) continue;
      const visible = tab.id === id && tab.kind === 'web';
      try { tab.view.setVisible(visible); } catch {}
    }
    this.layout();
    if (!silent) this.broadcast();
    if (t.kind === 'web') setTimeout(() => { try { t.view.webContents.focus(); } catch {} }, 50);
  }

  cycleTab(dir) {
    const i = this.order.indexOf(this.activeId);
    if (i < 0) return;
    const n = this.order.length;
    this.activate(this.order[(i + dir + n) % n]);
  }

  // ---------------- navigation ----------------
  navigate(id, input) {
    const tab = this.tabs.get(id);
    if (!tab) return;
    const url = normalizeInput(input);
    if (url.startsWith('dualnet://')) {
      tab.kind = 'internal';
      this._markInternal(tab, url);
      tab.url = url;
      this.activate(id);
      return;
    }
    tab.kind = 'web';
    if (!tab.view) return; // shouldn't happen
    tab.view.webContents.loadURL(url).catch(() => {});
  }

  goBack(id) { const t = this.tabs.get(id); if (t?.view) { try { t.view.webContents.navigationHistory.goBack(); } catch {} } }
  goForward(id) { const t = this.tabs.get(id); if (t?.view) { try { t.view.webContents.navigationHistory.goForward(); } catch {} } }
  reload(id) {
    const t = this.tabs.get(id);
    if (t?.view) { try { t.view.webContents.reload(); } catch {} }
    else if (t && t.kind === 'internal') this.broadcast();
  }
  stop(id) { const t = this.tabs.get(id); if (t?.view) { try { t.view.webContents.stop(); } catch {} } }
  toggleDevtools(id) {
    const t = this.tabs.get(id);
    if (t?.view) { try { t.view.webContents.openDevTools({ mode: 'detach' }); } catch {} }
  }

  // ---------------- proxy ----------------
  async setProxy(id, proxy) {
    const tab = this.tabs.get(id);
    if (!tab) return;
    tab.proxy = proxy || null;
    const ses = this._sessionFor(tab);
    try {
      await ses.setProxy(proxy ? { proxyRules: proxy, proxyBypassRules: '<local>' } : { mode: 'direct' });
      this.sentry.addBreadcrumb(`proxy for tab ${id}: ${proxy || 'off'}`, 'proxy');
    } catch (e) {
      this.sentry.captureException(e, { where: 'setProxy' });
    }
    this.broadcast();
  }

  // ---------------- state ----------------
  list() {
    return this.order.map(id => {
      const t = this.tabs.get(id);
      return {
        id: t.id, kind: t.kind, page: t.page || null, title: t.title, url: t.url,
        favicon: t.favicon, loading: t.loading, canBack: t.canBack, canFwd: t.canFwd,
        proxy: t.proxy, active: t.id === this.activeId,
      };
    });
  }

  active() { return this.tabs.get(this.activeId); }

  broadcast() {
    try { this.chromeView.webContents.send('tabs-updated', this.list()); } catch {}
  }
}

function normalizeInput(input) {
  const s = (input || '').trim();
  if (!s) return 'dualnet://newtab';
  if (/^(https?|file|dualnet):/i.test(s)) return s;
  if (/^[\w-]+(\.[\w-]+)+(:\d+)?(\/.*)?$/.test(s) || /^localhost(:\d+)?/.test(s)) return 'https://' + s;
  if (/^\d{1,3}(\.\d{1,3}){3}(:\d+)?(\/.*)?$/.test(s)) return 'http://' + s;
  // search
  return 'https://www.google.com/search?q=' + encodeURIComponent(s);
}

function errorPageHtml(url, code, desc) {
  const safe = (s) => String(s || '').replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
  return `<!doctype html><html><head><meta charset="utf-8"><title>Site can't be reached</title>
<style>
  body{font-family:'Segoe UI',system-ui,sans-serif;background:#fff;color:#202124;margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh}
  .box{max-width:560px;padding:40px}
  h1{font-size:24px;font-weight:400;margin:0 0 12px}
  .code{color:#5f6368;font-size:14px;margin-bottom:24px}
  button{background:#1a73e8;color:#fff;border:0;border-radius:4px;padding:9px 18px;font-size:14px;cursor:pointer}
</style></head><body><div class="box">
<svg width="96" height="96" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="10" stroke="#dadce0" stroke-width="2"/><path d="M8 15s1.5-2 4-2 4 2 4 2" stroke="#dadce0" stroke-width="2" stroke-linecap="round"/><circle cx="9" cy="9.5" r="1.2" fill="#dadce0"/><circle cx="15" cy="9.5" r="1.2" fill="#dadce0"/></svg>
<h1>Site can't be reached</h1>
<div class="code"><b>${safe(url)}</b><br>${safe(desc)} (ERR ${safe(code)})<br><br>Tips: check the address, your connection, or set a proxy for this tab via the globe icon.</div>
<button onclick="location.reload()">Reload</button>
</div></body></html>`;
}

module.exports = { TabManager, INTERNAL_PAGES, normalizeInput };
