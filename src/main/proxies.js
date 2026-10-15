'use strict';
// Free-proxy provider: fetches candidate lists from several public sources,
// dedupes, then validates concurrently (latency measured against a
// generate_204 endpoint). Results cached in memory + store.
const http = require('http');
const https = require('https');
const { EventEmitter } = require('events');

const SOURCES = [
  { name: 'TheSpeedX', url: 'https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt', fmt: 'lines' },
  { name: 'monosans', url: 'https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt', fmt: 'lines' },
  { name: 'proxyscrape', url: 'https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=6000&anonymity=all', fmt: 'lines' },
  { name: 'proxy-list.download', url: 'https://www.proxy-list.download/api/v1/get?type=http', fmt: 'lines' },
  { name: 'geonode', url: 'https://proxylist.geonode.com/api/proxy-list?protocols=http&limit=500&page=1&sort_by=lastChecked&sort_type=desc', fmt: 'geonode' },
];

const TEST_URL = 'http://www.gstatic.com/generate_204';
const MAX_CANDIDATES = 900;
const CONCURRENCY = 28;
const TIMEOUT_MS = 7000;
const KEEP_BEST = 80;

function fetchUrl(url, { timeout = 12000, headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const lib = u.protocol === 'https:' ? https : http;
    const req = lib.get(u, {
      headers: { 'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) DualNet/3.0', ...headers },
      timeout,
    }, (res) => {
      if ([301, 302, 303, 307, 308].includes(res.statusCode) && res.headers.location) {
        res.resume();
        fetchUrl(new URL(res.headers.location, url).href, { timeout, headers }).then(resolve, reject);
        return;
      }
      if (res.statusCode >= 400) { res.resume(); return reject(new Error('HTTP ' + res.statusCode)); }
      const chunks = [];
      let size = 0;
      res.on('data', c => { size += c.length; if (size < 8 * 1024 * 1024) chunks.push(c); });
      res.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
      res.on('error', reject);
    });
    req.on('timeout', () => { req.destroy(new Error('timeout')); });
    req.on('error', reject);
  });
}

function geonodeUrl(u) { try { const x = new URL(u); x.searchParams.set('page', '2'); return x.href; } catch { return null; } }

async function fetchCandidates(onNote) {
  const note = onNote || (() => {});
  const jobs = SOURCES.map(async (src) => {
    try {
      const text = await fetchUrl(src.url);
      const out = [];
      if (src.fmt === 'lines') {
        for (const line of text.split(/\r?\n/)) {
          const m = /^\s*(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})\s*$/.exec(line);
          if (m) out.push({ host: m[1], port: +m[2], source: src.name });
        }
      } else if (src.fmt === 'geonode') {
        try {
          const data = JSON.parse(text);
          for (const p of (data.data || [])) {
            if (p.ip && p.port) out.push({ host: p.ip, port: +p.port, source: src.name, country: p.country, anonymity: p.anonymityLevel });
          }
        } catch {}
      }
      note(`fetched ${out.length} from ${src.name}`);
      return out;
    } catch (e) {
      note(`source ${src.name} failed: ${e.message}`);
      return [];
    }
  });
  // second geonode page
  jobs.push((async () => {
    try {
      const u2 = geonodeUrl(SOURCES[4].url);
      if (!u2) return [];
      const text = await fetchUrl(u2);
      const data = JSON.parse(text);
      const out = (data.data || []).map(p => ({ host: p.ip, port: +p.port, source: 'geonode2', country: p.country, anonymity: p.anonymityLevel }));
      note(`fetched ${out.length} from geonode p2`);
      return out;
    } catch { return []; }
  })());
  const lists = await Promise.all(jobs);
  const seen = new Set();
  const merged = [];
  for (const list of lists) {
    for (const p of list) {
      const key = `${p.host}:${p.port}`;
      if (seen.has(key)) continue;
      seen.add(key);
      merged.push(p);
    }
  }
  // fair sample across sources, cap total
  if (merged.length > MAX_CANDIDATES) {
    const bySource = {};
    for (const p of merged) (bySource[p.source] ||= []).push(p);
    const per = Math.ceil(MAX_CANDIDATES / Object.keys(bySource).length);
    const out = [];
    for (const [, arr] of Object.entries(bySource)) out.push(...arr.slice(0, per));
    return out.slice(0, MAX_CANDIDATES);
  }
  return merged;
}

// validate one proxy: HTTP GET through it; returns latency ms or null
function testProxy(host, port, { timeout = TIMEOUT_MS, url = TEST_URL } = {}) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    let settled = false;
    const done = (ms) => { if (!settled) { settled = true; try { req.destroy(); } catch {} resolve(ms); } };
    const req = http.request({
      host, port, path: url, method: 'GET',
      headers: { 'User-Agent': 'Mozilla/5.0 DualNet', 'Connection': 'close' },
      timeout,
    }, (res) => {
      const ms = Date.now() - t0;
      res.resume();
      if (res.statusCode >= 200 && res.statusCode < 400) done(ms);
      else done(null);
    });
    req.on('timeout', () => done(null));
    req.on('error', () => done(null));
    req.end();
  });
}

async function testBatch(candidates, { concurrency = CONCURRENCY, onProgress } = {}) {
  const results = [];
  let idx = 0, doneCount = 0;
  async function worker() {
    while (idx < candidates.length) {
      const i = idx++;
      const c = candidates[i];
      const ms = await testProxy(c.host, c.port);
      doneCount++;
      if (onProgress && (doneCount % 10 === 0 || doneCount === candidates.length)) {
        onProgress({ done: doneCount, total: candidates.length, found: results.length });
      }
      if (ms != null) results.push({ ...c, latency: ms });
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, candidates.length) }, worker));
  return results;
}

class ProxyProvider extends EventEmitter {
  constructor(store) {
    super();
    this.store = store;
    this.testing = false;
    this.progress = null;
    const cached = store.get('proxyCache');
    if (cached && cached.proxies) this.proxies = cached.proxies;
    else this.proxies = [];
  }

  get list() { return this.proxies; }

  async refresh({ force = false } = {}) {
    if (this.testing) return { skipped: true };
    const fresh = this.proxies.length && (Date.now() - (this.store.get('proxyCache')?.ts || 0) < 30 * 60 * 1000);
    if (fresh && !force) return { cached: true, count: this.proxies.length };
    this.testing = true;
    this.progress = { phase: 'fetching' };
    this.emit('progress', this.progress);
    try {
      const candidates = await fetchCandidates((m) => this.emit('note', m));
      this.progress = { phase: 'testing', total: candidates.length, done: 0, found: 0 };
      this.emit('progress', this.progress);
      const working = await testBatch(candidates, {
        onProgress: (p) => {
          this.progress = { phase: 'testing', ...p };
          this.emit('progress', this.progress);
        },
      });
      working.sort((a, b) => a.latency - b.latency);
      this.proxies = working.slice(0, KEEP_BEST).map(p => ({
        host: p.host, port: p.port, latency: p.latency,
        country: p.country || null, anonymity: p.anonymity || null, source: p.source,
      }));
      this.store.set('proxyCache', { ts: Date.now(), proxies: this.proxies });
      this.progress = { phase: 'done', found: this.proxies.length };
      this.emit('progress', this.progress);
      return { count: this.proxies.length };
    } finally {
      this.testing = false;
    }
  }
}

module.exports = { ProxyProvider, fetchCandidates, testProxy, testBatch, fetchUrl };
