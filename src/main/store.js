'use strict';
// Persistent settings + simple JSON store (userData/settings.json)
const fs = require('fs');
const path = require('path');

const DEFAULTS = {
  downloadDir: null,          // resolved at runtime => app.getPath('downloads')
  maxConnections: 12,         // per download (2..32)
  askWhereToSave: false,
  insecureTLS: false,
  theme: 'system',            // 'system' | 'light' | 'dark'
  selectedNics: [],           // empty = all usable
  speedTestUrl: 'https://speed.cloudflare.com/__down?bytes=26214400',
  sentryEnabled: true,
  bookmarks: [],              // {title,url}
  history: [],                // {title,url,ts} capped
  proxyCache: null,           // {ts, proxies:[{host,port,latency,country?,source}]}
  windowBounds: null,
  windowMaximized: false,
  showBookmarksBar: false,
};

class Store {
  constructor(file) {
    this.file = file;
    this.data = { ...DEFAULTS };
    this._saveTimer = null;
    try {
      if (fs.existsSync(file)) {
        const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
        this.data = { ...DEFAULTS, ...raw };
      }
    } catch (e) { /* corrupted -> defaults */ }
  }
  get(key) { return this.data[key]; }
  all() { return { ...this.data }; }
  set(key, value) {
    this.data[key] = value;
    if (!this._saveTimer) {
      this._saveTimer = setTimeout(() => {
        this._saveTimer = null;
        this.flush();
      }, 250);
    }
  }
  flush() {
    if (this._saveTimer) { clearTimeout(this._saveTimer); this._saveTimer = null; }
    try {
      fs.mkdirSync(path.dirname(this.file), { recursive: true });
      fs.writeFileSync(this.file, JSON.stringify(this.data, null, 2));
    } catch (e) { /* read-only fs etc. */ }
  }
  pushHistory(entry) {
    const h = Array.isArray(this.data.history) ? this.data.history : [];
    h.unshift(entry);
    this.data.history = h.filter((x, i, a) =>
      i < 200 && !(x.url === entry.url && x.ts !== entry.ts && a.findIndex(y => y.url === x.url) === i));
    // simple dedupe by url keep-newest, cap 200
    const seen = new Set();
    this.data.history = this.data.history.filter(x => {
      if (seen.has(x.url)) return false;
      seen.add(x.url); return true;
    }).slice(0, 200);
    this.set('history', this.data.history);
  }
  toggleBookmark(title, url) {
    const list = Array.isArray(this.data.bookmarks) ? [...this.data.bookmarks] : [];
    const idx = list.findIndex(b => b.url === url);
    if (idx >= 0) list.splice(idx, 1);
    else list.push({ title: title || url, url });
    this.set('bookmarks', list);
    return idx < 0; // true = now bookmarked
  }
}

module.exports = { Store, DEFAULTS };
