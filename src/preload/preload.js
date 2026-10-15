'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('dualnet', {
  // window
  winMin: () => ipcRenderer.send('window:minimize'),
  winMax: () => ipcRenderer.send('window:maximize'),
  winClose: () => ipcRenderer.send('window:close'),
  winIsMax: () => ipcRenderer.invoke('window:isMaximized'),
  setChromeHeight: (h) => ipcRenderer.send('chrome-height', h),

  // tabs
  tabsList: () => ipcRenderer.invoke('tabs:list'),
  tabCreate: (opts) => ipcRenderer.send('tabs:create', opts),
  tabActivate: (id) => ipcRenderer.send('tabs:activate', id),
  tabClose: (id) => ipcRenderer.send('tabs:close', id),
  tabCloseOthers: (id) => ipcRenderer.send('tabs:closeOthers', id),
  tabDuplicate: (id) => ipcRenderer.send('tabs:duplicate', id),
  tabNavigate: (id, url) => ipcRenderer.send('tabs:navigate', { id, url }),
  tabBack: (id) => ipcRenderer.send('tabs:back', id),
  tabForward: (id) => ipcRenderer.send('tabs:forward', id),
  tabReload: (id) => ipcRenderer.send('tabs:reload', id),
  tabStop: (id) => ipcRenderer.send('tabs:stop', id),
  tabSetProxy: (id, proxy) => ipcRenderer.invoke('tabs:setProxy', { id, proxy }),
  omniboxPreview: (url) => ipcRenderer.invoke('tabs:omniboxPreview', url),

  // downloads
  downloadsList: () => ipcRenderer.invoke('downloads:list'),
  downloadAdd: (opts) => ipcRenderer.send('downloads:add', opts),
  downloadPause: (id) => ipcRenderer.send('downloads:pause', id),
  downloadResume: (id) => ipcRenderer.send('downloads:resume', id),
  downloadCancel: (id) => ipcRenderer.send('downloads:cancel', id),
  downloadRemove: (id) => ipcRenderer.send('downloads:remove', id),
  downloadRetry: (id) => ipcRenderer.send('downloads:retry', id),
  downloadsResumables: () => ipcRenderer.invoke('downloads:resumables'),
  downloadResumeDisk: (meta) => ipcRenderer.send('downloads:resumeDisk', meta),
  chooseDownloadDir: () => ipcRenderer.invoke('downloads:chooseDir'),
  openFolder: (dir) => ipcRenderer.send('downloads:openFolder', dir),
  openFile: (p) => ipcRenderer.send('downloads:openFile', p),
  showInFolder: (p) => ipcRenderer.send('downloads:showInFolder', p),

  // nics + speed test
  nicsList: () => ipcRenderer.invoke('nics:list'),
  speedTest: (names) => ipcRenderer.send('nics:speedtest', { names }),

  // proxies
  proxyList: () => ipcRenderer.invoke('proxy:list'),
  proxyRefresh: (force) => ipcRenderer.send('proxy:refresh', { force }),
  proxyTestCustom: (host, port) => ipcRenderer.invoke('proxy:testCustom', { host, port }),

  // settings
  settingsGet: () => ipcRenderer.invoke('settings:get'),
  settingsSet: (key, value) => ipcRenderer.send('settings:set', { key, value }),
  bookmarksList: () => ipcRenderer.invoke('bookmarks:list'),
  bookmarksToggle: (title, url) => ipcRenderer.send('bookmarks:toggle', { title, url }),
  historyList: () => ipcRenderer.invoke('history:list'),
  historyClear: () => ipcRenderer.send('history:clear'),
  appInfo: () => ipcRenderer.invoke('app:info'),

  // events from main
  onTabsUpdated: (cb) => ipcRenderer.on('tabs-updated', (e, tabs) => cb(tabs)),
  onDownloadProgress: (cb) => ipcRenderer.on('download-progress', (e, snap) => cb(snap)),
  onDownloadsChanged: (cb) => ipcRenderer.on('downloads-changed', (e, snap) => cb(snap)),
  onProxyProgress: (cb) => ipcRenderer.on('proxy-progress', (e, p) => cb(p)),
  onProxyNote: (cb) => ipcRenderer.on('proxy-note', (e, m) => cb(m)),
  onProxyDone: (cb) => ipcRenderer.on('proxy-done', (e, r) => cb(r)),
  onProxyError: (cb) => ipcRenderer.on('proxy-error', (e, m) => cb(m)),
  onSpeedtestResult: (cb) => ipcRenderer.on('speedtest-result', (e, r) => cb(r)),
  onSpeedtestDone: (cb) => ipcRenderer.on('speedtest-done', (e) => cb()),
  onThemeUpdated: (cb) => ipcRenderer.on('theme-updated', (e, t) => cb(t)),
  onBookmarksUpdated: (cb) => ipcRenderer.on('bookmarks-updated', (e, b) => cb(b)),
  onUI: (cb) => ipcRenderer.on('ui:focus-omnibox', () => cb('focus-omnibox')),
  onUIDownloads: (cb) => ipcRenderer.on('ui:open-downloads', () => cb('open-downloads')),
  onWindowMaximized: (cb) => ipcRenderer.on('window-maximized', (e, m) => cb(m)),
});
