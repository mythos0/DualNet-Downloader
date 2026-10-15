'use strict';
/* DualNet Downloader — chrome shell renderer */

const api = window.dualnet;
const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => [...document.querySelectorAll(sel)];

// ---------------- state ----------------
const S = {
  tabs: [],
  downloads: new Map(),     // id -> snapshot
  settings: null,
  nics: [],
  proxies: [],
  speedResults: [],
  speedRunning: false,
  filter: 'all',
  activeTab: null,
  bookmarks: [],
  history: [],
  proxyTesting: false,
};
const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };
const esc = (s) => String(s ?? '').replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));

function fmtBytes(n) {
  if (n == null || isNaN(n)) return '—';
  if (n < 1024) return n + ' B';
  const u = ['KB', 'MB', 'GB', 'TB'];
  let i = -1; let v = n;
  do { v /= 1024; i++; } while (v >= 1024 && i < u.length - 1);
  return v.toFixed(v >= 100 ? 0 : 1) + ' ' + u[i];
}
function fmtSpeed(bps) { return bps >= 0 ? fmtBytes(bps) + '/s' : '—'; }
function fmtEta(received, total, speed) {
  if (!total || !speed) return '';
  const s = Math.round((total - received) / speed);
  if (!isFinite(s) || s < 0) return '';
  if (s < 60) return s + 's left';
  if (s < 3600) return Math.floor(s / 60) + 'm ' + (s % 60) + 's left';
  return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm left';
}
function timeAgo(ts) {
  const d = Date.now() - ts;
  if (d < 60e3) return 'just now';
  if (d < 3600e3) return Math.floor(d / 60e3) + ' min ago';
  if (d < 86400e3) return Math.floor(d / 3600e3) + ' h ago';
  return new Date(ts).toLocaleDateString();
}
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.style.display = 'block';
  clearTimeout(toast._t);
  toast._t = setTimeout(() => { t.style.display = 'none'; }, 2600);
}

// ---------------- chrome height ----------------
function reportChromeHeight() {
  const h = $('#chrome').getBoundingClientRect().height;
  api.setChromeHeight(Math.ceil(h));
}
window.addEventListener('resize', reportChromeHeight);
new ResizeObserver(reportChromeHeight).observe($('#chrome'));

// ---------------- theme ----------------
function applyTheme(dark) {
  document.body.classList.toggle('theme-dark', dark);
  document.body.classList.toggle('theme-light', !dark);
}
api.onThemeUpdated(({ dark }) => applyTheme(dark));

// ---------------- tabs ----------------
function renderTabs(tabs) {
  S.tabs = tabs;
  const host = $('#tabs');
  host.innerHTML = '';
  for (const t of tabs) {
    const tab = el('div', 'tab' + (t.active ? ' active' : ''));
    tab.dataset.id = t.id;
    let lead;
    if (t.loading) lead = '<span class="spinner"></span>';
    else if (t.favicon && /^https?:/.test(t.url)) lead = `<span class="fav"><img src="${esc(t.favicon)}" onerror="this.parentNode.innerHTML='&nbsp;'"></span>`;
    else if (t.kind === 'internal') lead = `<span class="fav">${I[(t.page === 'speedtest' ? 'gauge' : t.page === 'settings' ? 'settings' : t.page === 'history' ? 'clock' : 'download')] || I.download}</span>`;
    else lead = `<span class="fav">${I.globe}</span>`;
    tab.innerHTML = `${lead}<span class="title">${esc(t.title || 'New Tab')}</span>
      <button class="tclose" title="Close tab"><svg viewBox="0 0 12 12"><path d="M2 2l8 8M10 2l-8 8" stroke="currentColor" stroke-width="1.4"/></svg></button>`;
    tab.addEventListener('click', (e) => {
      if (e.target.closest('.tclose')) { api.tabClose(t.id); e.stopPropagation(); return; }
      api.tabActivate(t.id);
    });
    tab.addEventListener('auxclick', (e) => { if (e.button === 1) api.tabClose(t.id); });
    tab.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      showTabCtx(e, t);
    });
    host.appendChild(tab);
  }
  syncToolbar();
}
api.onTabsUpdated(renderTabs);

function activeTab() { return S.tabs.find(t => t.active) || null; }

// ---------------- omnibox ----------------
function syncToolbar() {
  const t = activeTab();
  S.activeTab = t;
  const input = $('#omni-input');
  if (!t) return;
  const isEditing = document.activeElement === input && input.value !== '' && input.dataset.editing === '1';
  if (!isEditing) {
    input.value = t.url === 'dualnet://newtab' ? '' : (t.url || '');
    setOmniIcon(t.url);
  }
  $('#nav-back').disabled = !t.canBack;
  $('#nav-fwd').disabled = !t.canFwd;
  $('#nav-reload').innerHTML = t.loading
    ? '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>'
    : '<svg id="reload-icon" viewBox="0 0 24 24"><path d="M17.65 6.35A8 8 0 1 0 20 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><path d="M18 3v4h-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  // proxy chip
  const chip = $('#proxy-chip');
  if (t.proxy) {
    chip.textContent = '⇄ ' + t.proxy.replace(/^https?:\/\//, '');
    chip.style.display = 'inline-block';
  } else chip.style.display = 'none';
  // star
  const marked = S.bookmarks.some(b => b.url === t.url);
  $('#btn-star').classList.toggle('on', marked);
  // pages visibility
  showPageForTab(t);
}

function setOmniIcon(url) {
  const secure = /^https:/i.test(url || '');
  $('#omni-lock').style.display = secure ? '' : 'none';
  $('#omni-search').style.display = secure ? 'none' : '';
}

const omni = $('#omni-input');
omni.addEventListener('focus', () => { omni.select(); omni.dataset.editing = '1'; });
omni.addEventListener('blur', () => { omni.dataset.editing = '0'; syncToolbar(); });
omni.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    const t = activeTab();
    api.tabNavigate(t && t.id, omni.value.trim());
    omni.blur();
  } else if (e.key === 'Escape') { omni.blur(); }
});
$('#nav-back').addEventListener('click', () => api.tabBack());
$('#nav-fwd').addEventListener('click', () => api.tabForward());
$('#nav-reload').addEventListener('click', () => {
  const t = activeTab();
  if (t && t.loading) api.tabStop();
  else api.tabReload();
});
$('#btn-newtab').addEventListener('click', () => api.tabCreate({}));
$('#btn-star').addEventListener('click', async () => {
  const t = activeTab();
  if (!t || !t.url || t.url.startsWith('dualnet://')) return;
  api.bookmarksToggle(t.title, t.url);
});

// window controls
$('#wc-min').addEventListener('click', () => api.winMin());
$('#wc-max').addEventListener('click', () => api.winMax());
$('#wc-close').addEventListener('click', () => api.winClose());
api.onWindowMaximized((m) => {
  $('#wc-max').innerHTML = m
    ? '<svg viewBox="0 0 12 12"><rect x="1" y="3.5" width="6" height="6" rx="1" fill="none" stroke="currentColor" stroke-width="1.1"/><path d="M3.5 3.5v-2h6v6h-2" fill="none" stroke="currentColor" stroke-width="1.1"/></svg>'
    : '<svg viewBox="0 0 12 12"><rect x="2.5" y="2.5" width="7" height="7" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>';
});

// ---------------- bubbles ----------------
function hideBubbles(except) {
  for (const id of ['bubble-dl', 'bubble-proxy', 'bubble-menu', 'tab-ctx']) {
    if (id !== except) $('#' + id).style.display = 'none';
  }
}
document.addEventListener('mousedown', (e) => {
  if (!e.target.closest('.bubble') && !e.target.closest('#btn-downloads') && !e.target.closest('#btn-proxy') && !e.target.closest('#btn-menu') && !e.target.closest('.tab')) {
    hideBubbles();
  }
});

// main menu
$('#btn-menu').addEventListener('click', (e) => {
  e.stopPropagation();
  const b = $('#bubble-menu');
  const showing = b.style.display !== 'none';
  hideBubbles();
  if (!showing) {
    b.style.display = 'block';
    $('[data-act="bookmarksbar"]').innerHTML = 'Show bookmarks bar <span class="menu-k">' + (S.settings && S.settings.showBookmarksBar ? 'On' : 'Off') + '</span>';
  }
});
$('#bubble-menu').addEventListener('click', (e) => {
  const act = e.target.closest('.menu-item')?.dataset.act;
  if (!act) return;
  hideBubbles();
  const t = activeTab();
  switch (act) {
    case 'newtab': api.tabCreate({}); break;
    case 'downloads': openInternal('downloads'); break;
    case 'history': openInternal('history'); break;
    case 'adddownload': openAddModal(); break;
    case 'openfolder': api.openFolder(S.settings && S.settings.downloadDir); break;
    case 'speedtest': openInternal('speedtest'); break;
    case 'settings': openInternal('settings'); break;
    case 'bookmarksbar': {
      const v = !(S.settings && S.settings.showBookmarksBar);
      api.settingsSet('showBookmarksBar', v);
      S.settings.showBookmarksBar = v;
      renderBookmarks();
      break;
    }
    case 'theme': api.settingsSet('theme', document.body.classList.contains('theme-dark') ? 'light' : 'dark'); break;
    case 'about': openInternal('settings'); break;
    case 'exit': api.winClose(); break;
  }
});

// tab context menu
let ctxTabId = null;
function showTabCtx(e, t) {
  ctxTabId = t.id;
  const m = $('#tab-ctx');
  hideBubbles('tab-ctx');
  m.style.display = 'block';
  m.style.left = Math.min(e.clientX, innerWidth - 230) + 'px';
  m.style.top = Math.min(e.clientY + 6, 120) + 'px';
}
$('#tab-ctx').addEventListener('click', (e) => {
  const act = e.target.closest('.menu-item')?.dataset.act;
  if (!act || ctxTabId == null) return;
  hideBubbles();
  if (act === 'ctx-close') api.tabClose(ctxTabId);
  if (act === 'ctx-closeothers') api.tabCloseOthers(ctxTabId);
  if (act === 'ctx-duplicate') api.tabDuplicate(ctxTabId);
  if (act === 'ctx-newright') api.tabCreate({});
});

// ---------------- downloads UI ----------------
api.onDownloadProgress((snap) => {
  S.downloads.set(snap.id, snap);
  renderDownloads();
});
api.onDownloadsChanged((snap) => {
  if (['completed', 'failed', 'cancelled'].includes(snap.state)) {
    S.downloads.set(snap.id, snap);
    if (snap.state === 'completed') toast('Downloaded: ' + snap.file);
  }
  renderDownloads();
});

async function refreshDownloads() {
  const list = await api.downloadsList();
  S.downloads = new Map(list.map(d => [d.id, d]));
  renderDownloads();
}

function drow(d) {
  const row = el('div', 'drow');
  const pct = d.total ? Math.min(100, d.received / d.total * 100) : 0;
  const isActive = ['downloading', 'probing', 'queued'].includes(d.state);
  const stateTxt = {
    downloading: fmtSpeed(d.speed) + (d.total ? ' · ' + pct.toFixed(0) + '%' : '') + (d.activeConns ? ` · ${d.activeConns} connections` : ''),
    probing: 'Starting…', queued: 'Queued',
    paused: 'Paused · ' + fmtSpeed(0),
    completed: fmtBytes(d.total ?? d.received) + ' · Done',
    failed: 'Failed — ' + (d.error || 'error'),
    cancelled: 'Cancelled',
  }[d.state] || d.state;
  const eta = d.state === 'downloading' ? ' · ' + fmtEta(d.received, d.total, d.speed) : '';
  const nicTxt = d.perNic && Object.keys(d.perNic).length
    ? Object.entries(d.perNic).map(([ip, v]) => `<span class="nic-chip">${esc(shortIp(ip))} ${fmtSpeed(v.speed)}</span>`).join('')
    : '';
  let icon = I.file;
  const ext = (d.file || '').split('.').pop().toLowerCase();
  if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'svg', 'bmp'].includes(ext)) icon = I.image;
  else if (['mp3', 'wav', 'ogg', 'flac', 'm4a'].includes(ext)) icon = I.audio;
  else if (['mp4', 'mkv', 'avi', 'mov', 'webm'].includes(ext)) icon = I.video;
  else if (['zip', 'rar', '7z', 'tar', 'gz'].includes(ext)) icon = I.zip;
  row.innerHTML = `
    <div class="ficon">${icon}</div>
    <div class="dmain">
      <div class="dname">${esc(d.file || d.url)}</div>
      <div class="dmeta">${esc(stateTxt)}${eta} ${nicTxt}</div>
      ${isActive ? `<div class="dbar"><div style="width:${pct}%"></div></div>` : ''}
    </div>
    <div class="dacts"></div>`;
  const acts = row.querySelector('.dacts');
  const mkBtn = (iconSvg, title, fn) => {
    const b = el('button', 'tbtn', iconSvg);
    b.title = title;
    b.addEventListener('click', fn);
    acts.appendChild(b);
  };
  if (isActive) mkBtn(I.pause, 'Pause', () => api.downloadPause(d.id));
  if (d.state === 'paused' || d.state === 'failed') mkBtn(I.play, 'Resume', () => api.downloadResume(d.id));
  if (isActive || d.state === 'paused') mkBtn(I.close, 'Cancel', () => api.downloadCancel(d.id));
  if (['completed', 'failed', 'cancelled'].includes(d.state)) {
    mkBtn(I.refresh, 'Retry', () => api.downloadRetry(d.id));
    mkBtn(I.folder, 'Show in folder', () => api.showInFolder(d.dir && d.file ? d.dir + '/' + d.file : d.dir));
    mkBtn(I.trash, 'Remove from list', () => { S.downloads.delete(d.id); api.downloadRemove(d.id); renderDownloads(); });
  }
  return row;
}

function shortIp(ip) {
  if (!ip) return '';
  if (ip.includes('|')) return ip.split('|')[1] || ip;
  return ip;
}

function renderDownloads() {
  // popup: 6 most recent
  const items = [...S.downloads.values()].sort((a, b) => (b.createdAt || 0) - (a.createdAt || 0));
  const popupList = $('#bubble-dl-list');
  popupList.innerHTML = '';
  for (const d of items.slice(0, 6)) popupList.appendChild(drow(d));
  if (!items.length) popupList.innerHTML = '<div class="hint" style="padding:14px 16px">No downloads yet — grab something!</div>';
  // badge
  const activeCount = items.filter(d => ['downloading', 'probing', 'queued'].includes(d.state)).length;
  const badge = $('#dl-badge');
  badge.style.display = activeCount ? 'flex' : 'none';
  badge.textContent = activeCount;
  // page
  const filtered = items.filter(d => {
    if (S.filter === 'active') return ['downloading', 'probing', 'queued', 'paused'].includes(d.state);
    if (S.filter === 'done') return d.state === 'completed';
    return true;
  });
  const list = $('#dlp-list');
  list.innerHTML = '';
  for (const d of filtered) list.appendChild(drow(d));
  if (!filtered.length) list.innerHTML = '<div class="hint" style="padding:20px 0">Nothing here.</div>';
}

$('#btn-downloads').addEventListener('click', (e) => {
  e.stopPropagation();
  const b = $('#bubble-dl');
  const showing = b.style.display !== 'none';
  hideBubbles();
  if (!showing) { b.style.display = 'block'; refreshDownloads(); }
});
$('#dl-seeall').addEventListener('click', () => { hideBubbles(); openInternal('downloads'); });
$('#dlp-folder').addEventListener('click', () => api.openFolder(S.settings && S.settings.downloadDir));
$('#dlp-add').addEventListener('click', () => openAddModal());
$('#dlp-clear').addEventListener('click', () => {
  for (const d of [...S.downloads.values()]) {
    if (['completed', 'failed', 'cancelled'].includes(d.state)) { S.downloads.delete(d.id); api.downloadRemove(d.id); }
  }
  renderDownloads();
});
$('#dlp-filters').addEventListener('click', (e) => {
  const chip = e.target.closest('.chip');
  if (!chip) return;
  $$('#dlp-filters .chip').forEach(c => c.classList.toggle('sel', c === chip));
  S.filter = chip.dataset.f;
  renderDownloads();
});
api.onUIDownloads(() => openInternal('downloads'));

// ---------------- proxy popup ----------------
$('#btn-proxy').addEventListener('click', async (e) => {
  e.stopPropagation();
  const b = $('#bubble-proxy');
  const showing = b.style.display !== 'none';
  hideBubbles();
  if (!showing) {
    b.style.display = 'block';
    const t = activeTab();
    $('#proxy-tab-name').textContent = t ? (t.title || '').slice(0, 30) : '';
    S.proxies = await api.proxyList();
    renderProxyList();
    if (!S.proxies.length && !S.proxyTesting) api.proxyRefresh(false);
  }
});
$('#proxy-refresh').addEventListener('click', () => {
  if (S.proxyTesting) return;
  S.proxyTesting = true;
  $('#proxy-progress').style.display = 'block';
  $('#proxy-progress').textContent = 'Fetching candidate lists…';
  api.proxyRefresh(true);
});
api.onProxyProgress((p) => {
  $('#proxy-progress').style.display = 'block';
  if (p.phase === 'testing') $('#proxy-progress').textContent = `Testing proxies ${p.done}/${p.total} — ${p.found} working…`;
  else if (p.phase === 'fetching') $('#proxy-progress').textContent = 'Fetching candidate lists…';
  else if (p.phase === 'done') $('#proxy-progress').textContent = `Done — ${p.found} working proxies.`;
});
api.onProxyNote((m) => { /* subtle; skip */ });
api.onProxyDone(async () => {
  S.proxyTesting = false;
  S.proxies = await api.proxyList();
  renderProxyList();
});
api.onProxyError((m) => {
  S.proxyTesting = false;
  $('#proxy-progress').textContent = 'Failed: ' + m;
});

function renderProxyList() {
  const list = $('#proxy-list');
  list.innerHTML = '';
  const t = activeTab();
  const current = t && t.proxy ? t.proxy : 'off';
  $$('input[name=proxysel]').forEach(r => r.checked = r.value === (current === 'off' ? 'off' : '_custom'));
  if (current !== 'off') {
    const custom = $('#proxy-custom');
    custom.value = current;
  }
  for (const p of S.proxies) {
    const row = el('label', 'proxy-row');
    row.innerHTML = `<input type="radio" name="proxysel" value="${esc(p.host + ':' + p.port)}">
      <span class="phost">${esc(p.host)}:${p.port}</span>
      ${p.country ? `<span class="pgeo">${esc(p.country)}</span>` : ''}
      <span class="plat">${p.latency} ms</span>`;
    row.querySelector('input').addEventListener('change', () => applyProxyToActive(p.host + ':' + p.port));
    list.appendChild(row);
  }
  if (!S.proxies.length) {
    list.innerHTML = '<div class="hint">No tested proxies yet — click "Fetch & test" above. This fetches public lists online and keeps the ones that actually respond.</div>';
  }
}
$$('input[name=proxysel]').forEach(r => r.addEventListener('change', () => {
  if (r.value === 'off' && r.checked) applyProxyToActive(null);
}));
$('#proxy-custom-apply').addEventListener('click', async () => {
  const v = $('#proxy-custom').value.trim();
  const status = $('#proxy-custom-status');
  if (!v) return;
  const m = /^(https?:\/\/)?([\w.-]+):(\d{2,5})$/.exec(v);
  if (!m) { status.textContent = 'Format: host:port'; return; }
  status.textContent = 'Testing ' + v + '…';
  const ms = await api.proxyTestCustom(m[2], m[3]);
  status.textContent = ms != null ? `Working — ${ms} ms. Applied to this tab.` : 'Did not respond. Applied anyway (you can keep it or turn it off).';
  applyProxyToActive(v);
});
async function applyProxyToActive(proxy) {
  const t = activeTab();
  if (!t) return;
  await api.tabSetProxy(t.id, proxy);
  toast(proxy ? 'Proxy set for this tab: ' + proxy : 'Proxy disabled for this tab');
}

// ---------------- internal pages ----------------
function openInternal(page) {
  api.tabNavigate(null, 'dualnet://' + page);
}
function showPageForTab(t) {
  const pages = ['newtab', 'downloads', 'settings', 'speedtest', 'history'];
  const isInternal = t && t.kind === 'internal' && pages.includes(t.page);
  document.body.classList.toggle('internal-active', !!isInternal);
  for (const p of pages) $('#' + 'page-' + p).style.display = (isInternal && t.page === p) ? 'block' : 'none';
  if (isInternal) renderInternalPage(t.page);
  reportChromeHeight();
}

function renderInternalPage(page) {
  if (page === 'downloads') { refreshDownloads(); renderResumables(); }
  else if (page === 'settings') renderSettings();
  else if (page === 'newtab') renderNTP();
  else if (page === 'speedtest') renderSpeedtest();
  else if (page === 'history') renderHistory();
}

// ---- new tab page ----
const SHORTCUTS = [
  { name: 'Google', url: 'https://www.google.com', letter: 'G', color: '#4285f4' },
  { name: 'YouTube', url: 'https://www.youtube.com', letter: '▶', color: '#ff0000' },
  { name: 'GitHub', url: 'https://github.com', letter: '', color: '#24292e', gh: true },
  { name: 'Wikipedia', url: 'https://www.wikipedia.org', letter: 'W', color: '#202122' },
  { name: 'Reddit', url: 'https://www.reddit.com', letter: 'r/', color: '#ff4500' },
  { name: 'X', url: 'https://x.com', letter: '𝕏', color: '#000' },
];
function renderNTP() {
  const tiles = $('#ntp-tiles');
  tiles.innerHTML = '';
  for (const s of SHORTCUTS) {
    const t = el('button', 'ntp-tile');
    t.innerHTML = `<div class="tlogo" style="color:${s.color}">${s.gh ? GH_MARK : s.letter}</div><div class="tname">${s.name}</div>`;
    t.addEventListener('click', () => api.tabNavigate(null, s.url));
    tiles.appendChild(t);
  }
  const cards = $('#ntp-cards');
  cards.innerHTML = '';
  const cardDefs = [
    { icon: I.plus, label: 'Add download', fn: () => openAddModal() },
    { icon: I.download, label: 'Downloads', fn: () => openInternal('downloads') },
    { icon: I.gauge, label: 'Speed test', fn: () => openInternal('speedtest') },
    { icon: I.network, label: 'Proxies', fn: () => $('#btn-proxy').click() },
    { icon: I.settings, label: 'Settings', fn: () => openInternal('settings') },
  ];
  for (const c of cardDefs) {
    const b = el('button', 'ntp-card', `${c.icon}<span>${c.label}</span>`);
    b.addEventListener('click', c.fn);
    cards.appendChild(b);
  }
  renderNicPills();
}
const GH_MARK = '<svg width="26" height="26" viewBox="0 0 16 16" fill="currentColor"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z"/></svg>';

async function renderNicPills() {
  S.nics = await api.nicsList();
  const sel = S.settings ? (S.settings.selectedNics || []) : [];
  const host = $('#ntp-nics');
  host.innerHTML = '';
  const chips = S.nics.length ? S.nics : [];
  for (const n of chips) {
    const on = !sel.length || sel.includes(n.name);
    host.appendChild(el('div', 'nic-pill', `<span class="dot" style="background:${on ? 'var(--green)' : 'var(--text2)'}"></span> ${esc(n.name)} <span style="font-family:Consolas,monospace;font-size:11px">${esc(n.ip)}</span>`));
  }
  if (chips.length > 1) {
    host.appendChild(el('div', 'nic-pill', `<span class="dot"></span> Combined mode — every download uses ${sel.length ? sel.length + ' selected' : 'all'} network${sel.length === 1 ? '' : 's'}`));
  }
}

$('#ntp-search').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    const v = e.target.value.trim();
    if (v) api.tabNavigate(null, v);
  }
});

// ---- settings page ----
async function renderSettings() {
  S.settings = await api.settingsGet();
  S.nics = await api.nicsList();
  const b = $('#settings-body');
  const sel = S.settings.selectedNics || [];
  const conns = S.settings.maxConnections || 12;
  b.innerHTML = `
  <div class="set-section"><h2>Downloads</h2>
    <div class="set-row">
      <div class="lab"><div class="t">Location</div><div class="d">Where finished files are saved</div></div>
      <div style="display:flex;gap:8px;align-items:center">
        <input type="text" id="set-dir" value="${esc(S.settings.downloadDir)}" readonly style="max-width:280px;cursor:pointer">
        <button class="pbtn" id="set-dir-browse">Change</button>
      </div>
    </div>
    <div class="set-row">
      <div class="lab"><div class="t">Connections per download</div><div class="d">Parallel segments; segments are claimed dynamically by whichever network is free</div></div>
      <input type="range" id="set-conns" min="2" max="32" value="${conns}"><span id="set-conns-v" style="width:30px;text-align:right">${conns}</span>
    </div>
  </div>

  <div class="set-section"><h2>Networks</h2>
    <div class="set-row" style="flex-direction:column;align-items:stretch">
      <div class="lab" style="margin-bottom:8px"><div class="t">Adapters used for downloads</div><div class="d">Nothing selected = Combined mode (all adapters). Every free adapter grabs the next pending segment — a faster network simply finishes more.</div></div>
      <div id="set-nics">${S.nics.map(n => `
        <label class="nic-check"><input type="checkbox" value="${esc(n.name)}" ${sel.includes(n.name) ? 'checked' : ''}>
          <span>${esc(n.name)}</span><span class="nip">${esc(n.ip)}</span></label>`).join('') || '<div class="hint">No external IPv4 adapters found.</div>'}
      </div>
      <div style="display:flex;gap:8px;margin-top:6px">
        <button class="pbtn" id="set-nics-refresh">${I.refresh} Rescan</button>
        <button class="pbtn" id="set-nics-clear">Use all (combined)</button>
      </div>
    </div>
  </div>

  <div class="set-section"><h2>Proxies</h2>
    <div class="set-row">
      <div class="lab"><div class="t">Per-tab proxy</div><div class="d">Use the globe icon in the toolbar to route an individual tab through a proxy. Free proxy lists are fetched online and tested for latency — only responding proxies are offered. Downloads always go through your real networks.</div></div>
      <button class="pbtn" id="set-proxy-fetch">Fetch &amp; test proxies</button>
    </div>
  </div>

  <div class="set-section"><h2>Appearance</h2>
    <div class="set-row">
      <div class="lab"><div class="t">Theme</div></div>
      <select id="set-theme">
        <option value="system" ${S.settings.theme === 'system' ? 'selected' : ''}>System</option>
        <option value="light" ${S.settings.theme === 'light' ? 'selected' : ''}>Light</option>
        <option value="dark" ${S.settings.theme === 'dark' ? 'selected' : ''}>Dark</option>
      </select>
    </div>
    <div class="set-row">
      <div class="lab"><div class="t">Show bookmarks bar</div></div>
      <label class="switch"><input type="checkbox" id="set-bmb" ${S.settings.showBookmarksBar ? 'checked' : ''}><span class="sl"></span></label>
    </div>
  </div>

  <div class="set-section"><h2>Advanced</h2>
    <div class="set-row">
      <div class="lab"><div class="t">Ignore TLS certificate errors</div><div class="d">For sites/fridge appliances with broken certificates. Less secure.</div></div>
      <label class="switch"><input type="checkbox" id="set-tls" ${S.settings.insecureTLS ? 'checked' : ''}><span class="sl"></span></label>
    </div>
    <div class="set-row">
      <div class="lab"><div class="t">Crash &amp; error reporting (Sentry)</div><div class="d">Sends exceptions and diagnostics to help improve DualNet</div></div>
      <label class="switch"><input type="checkbox" id="set-sentry" ${S.settings.sentryEnabled ? 'checked' : ''}><span class="sl"></span></label>
    </div>
  </div>

  <div class="set-section"><h2>About DualNet Downloader</h2>
    <div class="hint" style="padding:6px 0 2px">Version ${esc(S.settings.version)} · Chromium ${esc(S.settings.chrome)} · Electron ${esc(S.settings.electron)}<br>
    Full Chromium browser + multi-NIC download manager (combined / per-adapter selection, dynamic segment stealing) + per-tab proxies.<br><br>
    <a href="#" id="set-gh" style="color:var(--accent)">github.com/mythos0/DualNet-Downloader</a></div>
  </div>`;
  $('#set-dir-browse').addEventListener('click', async () => {
    const d = await api.chooseDownloadDir();
    if (d) { $('#set-dir').value = d; S.settings.downloadDir = d; }
  });
  $('#set-dir').addEventListener('click', () => api.openFolder(S.settings.downloadDir));
  $('#set-conns').addEventListener('input', (e) => { $('#set-conns-v').textContent = e.target.value; });
  $('#set-conns').addEventListener('change', (e) => api.settingsSet('maxConnections', +e.target.value));
  $('#set-nics').addEventListener('change', () => {
    const chosen = $$('#set-nics input:checked').map(i => i.value);
    api.settingsSet('selectedNics', chosen);
    S.settings.selectedNics = chosen;
  });
  $('#set-nics-refresh').addEventListener('click', () => renderSettings());
  $('#set-nics-clear').addEventListener('click', () => { api.settingsSet('selectedNics', []); renderSettings(); });
  $('#set-proxy-fetch').addEventListener('click', () => { api.proxyRefresh(true); toast('Fetching & testing proxies in the background…'); });
  $('#set-theme').addEventListener('change', (e) => api.settingsSet('theme', e.target.value));
  $('#set-bmb').addEventListener('change', (e) => { api.settingsSet('showBookmarksBar', e.target.checked); renderBookmarks(); });
  $('#set-tls').addEventListener('change', (e) => api.settingsSet('insecureTLS', e.target.checked));
  $('#set-sentry').addEventListener('change', (e) => api.settingsSet('sentryEnabled', e.target.checked));
  $('#set-gh').addEventListener('click', () => window.open('https://github.com/mythos0/DualNet-Downloader'));
}

// ---- speed test ----
$('#st-run').addEventListener('click', async () => {
  if (S.speedRunning) return;
  S.speedRunning = true;
  S.speedResults = [];
  $('#st-results').innerHTML = '';
  const host = $('#st-results');
  host.appendChild(el('div', 'hint', 'Running — downloading test payload on each adapter…'));
  S.nics = await api.nicsList();
  api.speedTest(null); // all adapters
});
api.onSpeedtestResult((r) => {
  S.speedResults.push(r);
  const host = $('#st-results');
  const row = el('div', 'st-row' + (r.ok ? '' : ' err'));
  row.innerHTML = r.ok
    ? `<div><div class="nm">${esc(r.name)}</div><div class="ip">${esc(r.ip)} · ${fmtBytes(r.bytes)} in ${(r.ms / 1000).toFixed(1)}s</div></div>
       <div class="mbps">${r.mbps.toFixed(1)}<small>Mbps</small></div>`
    : `<div><div class="nm">${esc(r.name)}</div><div class="ip">${esc(r.ip)}</div></div>
       <div class="mbps">Failed — ${esc(r.error || 'no data')}</div>`;
  if (S.speedResults.length === 1) host.innerHTML = '';
  host.appendChild(row);
});
api.onSpeedtestDone(() => {
  S.speedRunning = false;
  const total = S.speedResults.filter(r => r.ok).reduce((a, r) => a + r.mbps, 0);
  if (S.speedResults.some(r => r.ok) && S.speedResults.length > 1) {
    $('#st-results').appendChild(el('div', 'st-row', `<div><div class="nm">Combined</div><div class="ip">sum across adapters</div></div><div class="mbps">${total.toFixed(1)}<small>Mbps</small></div>`));
  }
});
function renderSpeedtest() { /* results live-rendered */ }

// ---- history ----
async function renderHistory() {
  S.history = await api.historyList();
  const host = $('#hist-list');
  host.innerHTML = '';
  for (const h of S.history.slice(0, 100)) {
    const row = el('div', 'hist-row');
    row.innerHTML = `<span class="ht">${timeAgo(h.ts)}</span><a href="${esc(h.url)}">${esc(h.title || h.url)}</a>`;
    row.querySelector('a').addEventListener('click', (e) => { e.preventDefault(); api.tabNavigate(null, h.url); });
    host.appendChild(row);
  }
  if (!S.history.length) host.innerHTML = '<div class="hint">No history yet.</div>';
}
$('#hist-clear').addEventListener('click', () => { api.historyClear(); renderHistory(); });

// ---- resumables (crash recovery) ----
async function renderResumables() {
  const list = await api.downloadsResumables();
  const host = $('#dlp-resumables');
  host.innerHTML = '';
  if (!list.length) return;
  host.appendChild(el('div', 'bubble-sub', 'Unfinished from a previous session'));
  for (const m of list) {
    const row = el('div', 'drow');
    row.innerHTML = `<div class="ficon">${I.refresh}</div>
      <div class="dmain"><div class="dname">${esc(m.file)}</div>
      <div class="dmeta">${fmtBytes(m.received)} of ${m.total ? fmtBytes(m.total) : '?'} · ${esc(m.url.slice(0, 80))}</div></div>
      <div class="dacts"></div>`;
    const b = el('button', 'pbtn primary', 'Resume');
    b.addEventListener('click', () => { api.downloadResumeDisk(m); toast('Resuming ' + m.file); setTimeout(renderResumables, 400); });
    row.querySelector('.dacts').appendChild(b);
    host.appendChild(row);
  }
}

// ---------------- bookmarks ----------------
async function renderBookmarks() {
  S.bookmarks = await api.bookmarksList();
  const bar = $('#bookmarksbar');
  bar.innerHTML = '';
  const show = S.settings && S.settings.showBookmarksBar;
  bar.style.display = show ? 'flex' : 'none';
  if (!show) { reportChromeHeight(); return; }
  for (const bm of S.bookmarks) {
    const b = el('button', 'bm', `<img src="${faviconFor(bm.url)}" width="14" height="14" onerror="this.style.visibility='hidden'"><span>${esc(bm.title || bm.url)}</span>`);
    b.title = bm.url;
    b.addEventListener('click', () => api.tabNavigate(null, bm.url));
    bar.appendChild(b);
  }
  reportChromeHeight();
}
api.onBookmarksUpdated((b) => { S.bookmarks = b; renderBookmarks(); syncToolbar(); });

// ---------------- add download modal ----------------
async function openAddModal() {
  $('#modal-back').style.display = 'flex';
  S.nics = await api.nicsList();
  const sel = $('#add-nic');
  sel.innerHTML = '<option value="combined">Combined (all / selected networks)</option>' +
    S.nics.map(n => `<option value="${esc(n.name)}">${esc(n.name)} — ${esc(n.ip)}</option>`).join('');
  $('#add-url').value = '';
  $('#add-name').value = '';
  $('#add-conns').value = S.settings ? S.settings.maxConnections : 12;
  setTimeout(() => $('#add-url').focus(), 30);
}
$('#add-cancel').addEventListener('click', () => $('#modal-back').style.display = 'none');
$('#modal-back').addEventListener('mousedown', (e) => { if (e.target.id === 'modal-back') $('#modal-back').style.display = 'none'; });
$('#add-start').addEventListener('click', () => {
  const url = $('#add-url').value.trim();
  if (!url) return;
  api.downloadAdd({
    url,
    fileName: $('#add-name').value.trim() || null,
    nicMode: $('#add-nic').value,
    connections: +$('#add-conns').value || undefined,
  });
  $('#modal-back').style.display = 'none';
  toast('Download started');
  refreshDownloads();
});

// ---------------- keyboard ----------------
window.addEventListener('keydown', (e) => {
  const ctrl = e.ctrlKey || e.metaKey;
  const k = e.key.toLowerCase();
  if (ctrl && k === 'l') { e.preventDefault(); omni.focus(); }
  else if (ctrl && k === 't') { e.preventDefault(); api.tabCreate({}); }
  else if (ctrl && k === 'w') { e.preventDefault(); const t = activeTab(); if (t) api.tabClose(t.id); }
  else if (ctrl && k === 'j') { e.preventDefault(); openInternal('downloads'); }
  else if (ctrl && k === 'r') { e.preventDefault(); api.tabReload(); }
  else if (e.key === 'F5') { e.preventDefault(); api.tabReload(); }
  else if (e.key === 'Escape') { hideBubbles(); }
});

// ---------------- boot ----------------
(async function boot() {
  S.settings = await api.settingsGet();
  S.bookmarks = await api.bookmarksList();
  applyTheme(matchMedia('(prefers-color-scheme: dark)').matches); // corrected on first theme event
  const tabs = await api.tabsList();
  renderTabs(tabs);
  renderBookmarks();
  refreshDownloads();
  reportChromeHeight();
  const info = await api.appInfo();
  S.appInfo = info;
})();
