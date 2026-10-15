'use strict';
const { app, BrowserWindow, ipcMain, nativeTheme, shell, clipboard, Menu, dialog } = require('electron');
const path = require('path');
const { Store } = require('./store');
const { TabManager, normalizeInput } = require('./tabs');
const { DownloadController } = require('./downloads');
const { ProxyProvider } = require('./proxies');
const nics = require('./nics');
const { SentryClient, DEFAULT_DSN } = require('./sentry');

app.setName('DualNet Downloader');

// container/root compatibility
if (process.getuid && process.getuid() === 0) {
  app.commandLine.appendSwitch('no-sandbox');
}
if (process.env.DUALNET_TEST) {
  app.commandLine.appendSwitch('disable-gpu');
}

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) { app.quit(); }

let win = null;
let store, sentry, tabs, downloads, proxyProvider;

// ---------- sentry ----------
sentry = new SentryClient({
  release: 'dualnet-downloader@' + app.getVersion(),
  environment: process.env.DUALNET_ENV || 'production',
});
process.on('uncaughtException', (err) => {
  try { sentry.captureException(err, { where: 'uncaughtException' }); sentry.flush(1500); } catch {}
  console.error(err);
});

function bootstrap() {
  store = new Store(path.join(app.getPath('userData'), 'settings.json'));
  store._defaultDownloadDir = app.getPath('downloads');
  if (!store.get('downloadDir')) store.set('downloadDir', store._defaultDownloadDir);
  sentry.enabled = store.get('sentryEnabled') !== false;
  if (store.get('insecureTLS')) {
    app.commandLine.appendSwitch('ignore-certificate-errors');
  }

  downloads = new DownloadController({ store, sentry });

  createWindow();

  tabs = new TabManager({
    win, store, sentry,
    onPageDownload: (info) => downloads.addFromBrowser(info),
  });
  downloads.attachWindow(win); // sends via chrome view
  proxyProvider = new ProxyProvider(store);
  wireProgress();
  wireIpc();

  const startUrl = process.argv.slice(1).find(a => /^https?:\/\//.test(a));
  tabs.createTab({ url: startUrl || 'dualnet://newtab' });
  sentry.captureMessage('app-started', 'info', { version: app.getVersion() });
  sentry.flush(2000);

  applyTheme();

  if (process.env.DUALNET_TEST) runAutopilot();
}

// ---------- visual test autopilot (sandbox only) ----------
async function runAutopilot() {
  const { desktopCapturer } = require('electron');
  const fs = require('fs');
  const dir = '/tmp/shots';
  fs.mkdirSync(dir, { recursive: true });
  const shot = async (name) => {
    await new Promise(r => setTimeout(r, 1200));
    const sources = await desktopCapturer.getSources({ types: ['screen'], thumbnailSize: { width: 1600, height: 1000 } });
    if (sources[0]) fs.writeFileSync(`${dir}/${name}.png`, sources[0].thumbnail.toPNG());
    console.log('[autopilot] shot', name);
  };
  try {
    await shot('01-newtab');
    // internal downloads page
    tabs.navigate(tabs.activeId, 'dualnet://downloads');
    await shot('02-downloads');
    // settings
    tabs.navigate(tabs.activeId, 'dualnet://settings');
    await shot('03-settings');
    // speedtest
    tabs.navigate(tabs.activeId, 'dualnet://speedtest');
    await shot('04-speedtest');
    // real web page (local test server with rich HTML)
    const { makeHtmlServer } = require('../../tests/webserver');
    const hport = await makeHtmlServer();
    const webTab = tabs.createTab({ url: `http://127.0.0.1:${hport}/` });
    await shot('05-webpage');
    // REAL browser-download interception: click the /big download link in the page
    tabs.activate(webTab, { silent: true });
    try {
      await tabs.tabs.get(webTab).view.webContents.executeJavaScript("document.querySelector('a.dl').click()");
      console.log('[autopilot] clicked download link');
    } catch (e) { console.log('[autopilot] link click failed', e.message); }
    await new Promise(r => setTimeout(r, 2500));
    tabs.navigate(tabs.activeId, 'dualnet://downloads');
    await shot('06-download-active');
    // proxy popup
    try {
      await tabs.chromeView.webContents.executeJavaScript("document.getElementById('btn-proxy').click()");
    } catch {}
    await shot('07-proxy-popup');
    // dark theme
    store.set('theme', 'dark');
    applyTheme();
    try { await tabs.chromeView.webContents.executeJavaScript("document.getElementById('btn-proxy').click()"); } catch {}
    tabs.navigate(tabs.activeId, 'dualnet://newtab');
    await shot('08-dark-newtab');
    console.log('[autopilot] DONE');
  } catch (e) {
    console.error('[autopilot] error', e);
  } finally {
    setTimeout(() => app.quit(), 800);
  }
}

function createWindow() {
  const bounds = store.get('windowBounds') || { width: 1366, height: 860 };
  const maximized = store.get('windowMaximized');
  win = new BrowserWindow({
    width: bounds.width, height: bounds.height,
    x: bounds.x, y: bounds.y,
    minWidth: 640, minHeight: 420,
    frame: false,
    show: false,
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#202124' : '#dee1e6',
    title: 'DualNet Downloader',
    webPreferences: { show: false },
  });
  if (maximized) win.maximize();
  win.show(); // chrome UI lives in a WebContentsView; ready-to-show won't fire for the empty base window
  win.on('resize', () => tabs && tabs.layout());
  win.on('maximize', () => sendUI('window-maximized', true));
  win.on('unmaximize', () => sendUI('window-maximized', false));
  win.on('close', () => {
    if (!win.isMaximized()) store.set('windowBounds', win.getBounds());
    store.set('windowMaximized', win.isMaximized());
    store.flush();
  });
  win.on('closed', () => { win = null; });
}

function sendUI(channel, payload) {
  if (!tabs || !tabs.chromeView) return;
  try { tabs.chromeView.webContents.send(channel, payload); } catch {}
}

function applyTheme() {
  if (!store) return;
  const theme = store.get('theme');
  nativeTheme.themeSource = theme === 'system' ? 'system' : theme;
  sendUI('theme-updated', { dark: nativeTheme.shouldUseDarkColors });
}

function wireProgress() {
  proxyProvider.on('progress', (p) => sendUI('proxy-progress', p));
  proxyProvider.on('note', (m) => sendUI('proxy-note', m));
}

function wireIpc() {
  // ---- window ----
  ipcMain.on('window:minimize', () => win?.minimize());
  ipcMain.on('window:maximize', () => win && (win.isMaximized() ? win.unmaximize() : win.maximize()));
  ipcMain.on('window:close', () => win?.close());
  ipcMain.handle('window:isMaximized', () => win?.isMaximized());
  ipcMain.on('chrome-height', (e, h) => tabs.setChromeHeight(h));

  // ---- tabs ----
  ipcMain.handle('tabs:list', () => tabs.list());
  ipcMain.on('tabs:create', (e, { url, background } = {}) => tabs.createTab({ url, background }));
  ipcMain.on('tabs:activate', (e, id) => tabs.activate(id));
  ipcMain.on('tabs:close', (e, id) => tabs.closeTab(id));
  ipcMain.on('tabs:closeOthers', (e, id) => tabs.closeOthers(id));
  ipcMain.on('tabs:duplicate', (e, id) => tabs.duplicateTab(id));
  ipcMain.on('tabs:navigate', (e, { id, url }) => tabs.navigate(id ?? tabs.activeId, url));
  ipcMain.on('tabs:back', (e, id) => tabs.goBack(id ?? tabs.activeId));
  ipcMain.on('tabs:forward', (e, id) => tabs.goForward(id ?? tabs.activeId));
  ipcMain.on('tabs:reload', (e, id) => tabs.reload(id ?? tabs.activeId));
  ipcMain.on('tabs:stop', (e, id) => tabs.stop(id ?? tabs.activeId));
  ipcMain.handle('tabs:setProxy', async (e, { id, proxy }) => { await tabs.setProxy(id ?? tabs.activeId, proxy); return tabs.list(); });
  ipcMain.handle('tabs:omniboxPreview', (e, url) => normalizeInput(url));

  // ---- downloads ----
  ipcMain.handle('downloads:list', () => downloads.list());
  ipcMain.on('downloads:add', (e, opts) => downloads.addManual(opts || {}));
  ipcMain.on('downloads:pause', (e, id) => downloads.pause(id));
  ipcMain.on('downloads:resume', (e, id) => downloads.resume(id));
  ipcMain.on('downloads:cancel', (e, id) => downloads.cancel(id));
  ipcMain.on('downloads:remove', (e, id) => downloads.remove(id));
  ipcMain.on('downloads:retry', (e, id) => downloads.retry(id));
  ipcMain.handle('downloads:resumables', () => downloads.resumables());
  ipcMain.on('downloads:resumeDisk', (e, meta) => downloads.resumeFromDisk(meta));
  ipcMain.handle('downloads:chooseDir', () => downloads.chooseDir(win));
  ipcMain.on('downloads:openFolder', (e, dir) => downloads.openFolder(dir));
  ipcMain.on('downloads:openFile', (e, p) => downloads.openFile(p));
  ipcMain.on('downloads:showInFolder', (e, p) => downloads.showInFolder(p));

  // ---- nics / speedtest ----
  ipcMain.handle('nics:list', () => nics.listNics());
  ipcMain.on('nics:speedtest', async (e, { names } = {}) => {
    const url = store.get('speedTestUrl');
    const targets = (names && names.length ? names : nics.listNics().map(n => n.name))
      .map(name => nics.listNics().find(n => n.name === name))
      .filter(Boolean);
    for (const nic of targets) {
      const res = await nics.speedTestNic(url, { localAddress: nic.ip, maxMs: 8000 });
      sendUI('speedtest-result', { name: nic.name, ip: nic.ip, ...res });
    }
    sendUI('speedtest-done', {});
  });

  // ---- proxies ----
  ipcMain.handle('proxy:list', () => proxyProvider.list);
  ipcMain.on('proxy:refresh', (e, { force } = {}) => {
    proxyProvider.refresh({ force }).then(r => sendUI('proxy-done', r)).catch(err => {
      sentry.captureException(err, { where: 'proxy-refresh' });
      sendUI('proxy-error', err.message);
    });
  });
  ipcMain.handle('proxy:testCustom', async (e, { host, port }) => {
    const { testProxy } = require('./proxies');
    const ms = await testProxy(host, +port, { timeout: 6000 });
    return ms;
  });

  // ---- settings ----
  ipcMain.handle('settings:get', () => ({ ...store.data, version: app.getVersion(), platform: process.platform, sentryDsnSet: true }));
  ipcMain.on('settings:set', (e, { key, value }) => {
    const allowed = ['downloadDir', 'maxConnections', 'askWhereToSave', 'insecureTLS', 'theme', 'selectedNics', 'sentryEnabled', 'speedTestUrl', 'showBookmarksBar'];
    if (allowed.includes(key)) {
      store.set(key, value);
      if (key === 'sentryEnabled') sentry.enabled = value !== false;
      if (key === 'theme') applyTheme();
    }
  });
  ipcMain.handle('bookmarks:list', () => store.get('bookmarks'));
  ipcMain.on('bookmarks:toggle', (e, { title, url }) => {
    const added = store.toggleBookmark(title, url);
    sendUI('bookmarks-updated', store.get('bookmarks'));
    e.returnValue = added;
  });
  ipcMain.handle('history:list', () => store.get('history'));
  ipcMain.on('history:clear', () => store.set('history', []));

  ipcMain.handle('app:info', () => ({
    version: app.getVersion(),
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    node: process.versions.node,
    platform: process.platform,
    dsn: (process.env.DUALNET_SENTRY_DSN || DEFAULT_DSN).replace(/(.*\/)(\d+)$/, '$1…$2').slice(0, 60) + '…',
  }));
}

// ---------- lifecycle ----------
app.whenReady().then(() => {
  nativeTheme.on('updated', () => sendUI('theme-updated', { dark: nativeTheme.shouldUseDarkColors }));
  try { bootstrap(); } catch (err) {
    sentry.captureException(err, { where: 'bootstrap' });
    throw err;
  }
});

app.on('second-instance', (e, argv) => {
  if (win) {
    if (win.isMinimized()) win.restore();
    win.focus();
    const url = argv.slice(1).find(a => /^https?:\/\//.test(a));
    if (url && tabs) tabs.createTab({ url });
  }
});

app.on('window-all-closed', () => {
  try {
    for (const j of downloads.manager.jobs.values()) j.pause(); // persist meta for resume
  } catch {}
  store.flush();
  sentry.flush(1500).finally?.(() => app.quit());
  setTimeout(() => app.quit(), 2200);
});

app.on('before-quit', () => {
  try { store.flush(); } catch {}
  try { sentry.flush(1500); } catch {}
});
