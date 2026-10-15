'use strict';
// DownloadJob: segmented downloader with a GLOBAL pending-segment queue.
//
// Design (fixes "segments permanently divided per NIC"):
//   - The file is split into many small segments (more than connections).
//   - There is NO permanent mapping segment -> NIC. Workers (each optionally
//     bound to a NIC via localAddress) pop the NEXT pending segment from one
//     shared queue. A fast NIC simply finishes segments sooner and claims more.
//   - Failed segments are re-queued (with retry cap). Pause/cancel abort
//     in-flight requests; progress per segment is kept for resume.
const fs = require('fs');
const path = require('path');
const { EventEmitter } = require('events');
const httpx = require('./httpx');

const MIN_CHUNK = 1024 * 1024;              // don't split below 1 MiB (IDM-style)
const MAX_SEGMENTS = 2048;
const MAX_RETRIES = 6;
const META_VERSION = 1;

function uid() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

class DownloadJob extends EventEmitter {
  /**
   * opts: {
   *   url, headers {}, directory, fileName?,
   *   bindIps: [ip,...]        // NIC local addresses; [] => default routing
   *   connections: 12,         // max parallel workers
   *   insecureTLS: false,
   *   proxy: 'http://host:port'|null,  // optional download proxy
   *   nicLimits: {ip: bytesPerSec}     // TEST HOOK: client-side token bucket
   * }
   */
  constructor(opts) {
    super();
    this.id = opts.id || uid();
    this.url = opts.url;
    this.headers = opts.headers || {};
    this.directory = opts.directory;
    this.fileName = opts.fileName || null;      // may be resolved after probe
    this.bindIps = (opts.bindIps && opts.bindIps.length) ? [...opts.bindIps] : [];
    this.connections = Math.max(1, Math.min(32, opts.connections || 12));
    this.insecureTLS = !!opts.insecureTLS;
    this.proxy = opts.proxy || null;
    this.nicLimits = opts.nicLimits || null;    // test hook
    this.fakeBind = !!opts.fakeBind;            // test hook: label NICs without kernel binding

    this.state = 'queued';        // queued|probing|downloading|paused|completed|failed|cancelled
    this.error = null;
    this.total = null;
    this.received = 0;            // committed bytes (on disk)
    this.acceptsRanges = false;
    this.finalUrl = opts.url;
    this.speed = 0;               // bytes/s (EMA)
    this.activeConns = 0;
    this.perNic = {};             // ip -> {bytes, speed}
    this.segments = [];           // {i,start,end,got,state:'pending'|'active'|'done',retries}
    this.workers = new Map();     // wid -> {localAddress, abort, seg}
    this.createdAt = Date.now();
    this.completedAt = null;
    this._fd = null;
    this._queue = [];             // indices of pending segments (work queue)
    this._abortAll = new AbortController();
    this._tick = null;
    this._lastBytes = 0;
    this._stopReason = null;
    this._startPromise = null;
  }

  get filePath() { return this.fileName ? path.join(this.directory, this.fileName) : null; }
  get metaPath() { return this.filePath ? this.filePath + '.dualnet.json' : null; }

  // ---------------- lifecycle ----------------
  async start() {
    if (this._startPromise) return this._startPromise;
    this._startPromise = this._run();
    return this._startPromise;
  }

  async _run() {
    try {
      this.state = 'probing';
      this._emitState();
      await this._probeAndPrepare();
      this.state = 'downloading';
      this._emitState();
      await this._downloadLoop();
      if (this.state === 'downloading') {
        this.state = 'completed';
        this.completedAt = Date.now();
        this._cleanupMeta();
        this._closeFd();
        this._emitState();
        this.emit('done');
      }
    } catch (err) {
      if (this._stopReason === 'pause') {
        this.state = 'paused';
        this._persistMeta();
        this._closeFd();
        this._emitState();
      } else if (this._stopReason === 'cancel') {
        this.state = 'cancelled';
        this._cleanupFiles();
        this._emitState();
      } else {
        this.state = 'failed';
        this.error = (err && err.message) || String(err);
        this._closeFd();
        this._persistMeta();
        this._emitState();
      }
    }
  }

  pause() {
    if (this.state !== 'downloading' && this.state !== 'probing') return;
    this._stopReason = 'pause';
    try { this._abortAll.abort(); } catch {}
  }

  cancel() {
    if (['completed', 'cancelled'].includes(this.state)) return;
    this._stopReason = 'cancel';
    try { this._abortAll.abort(); } catch {}
  }

  // resume from paused state (or pick up meta from disk). Fire-and-forget:
  // completion is observed via 'state'/'progress' events, not the returned promise.
  async resume() {
    if (this.state !== 'paused' && this.state !== 'failed') return;
    this._stopReason = null;
    this.error = null;
    this._startPromise = null;
    this._abortAll = new AbortController();
    this.start().catch(() => {});
  }

  // ---------------- probe / prepare ----------------
  async _probeAndPrepare() {
    const info = await httpx.probe(this.url, {
      headers: this.headers,
      localAddress: (this.fakeBind ? undefined : this.bindIps[0]) || undefined,
      insecure: this.insecureTLS, proxy: this.proxy,
    });
    this.finalUrl = info.finalUrl;
    this.acceptsRanges = info.acceptsRanges;
    this.total = info.total;
    if (!this.fileName) this.fileName = await DownloadJob.uniqueName(this.directory, info.filename || 'download');
    await fs.promises.mkdir(this.directory, { recursive: true });

    if (fs.existsSync(this.metaPath)) {
      const restored = this._loadMeta();
      if (restored && restored.total === this.total && this.total != null) {
        // resume from disk meta
        return;
      }
      this._cleanupMeta();
    }
    // fresh plan
    this.received = 0;
    this.segments = this._planSegments(this.total, this.acceptsRanges);
    if (this.total != null) {
      // create + preallocate (sparse) so segment writers can seek to their offsets
      const fh = await fs.promises.open(this.filePath, 'w');
      await fh.truncate(this.total);
      await fh.close();
    }
    this._persistMeta();
  }

  _planSegments(total, acceptsRanges) {
    if (total == null || total <= 0) {
      return [{ i: 0, start: 0, end: null, got: 0, state: 'pending', retries: 0 }];
    }
    if (!acceptsRanges) {
      return [{ i: 0, start: 0, end: total - 1, got: 0, state: 'pending', retries: 0 }];
    }
    const desired = Math.max(this.connections * 6, 12);
    let segSize = Math.max(MIN_CHUNK, Math.ceil(total / desired));
    if (Math.ceil(total / segSize) > MAX_SEGMENTS) segSize = Math.ceil(total / MAX_SEGMENTS);
    const count = Math.ceil(total / segSize);
    const segs = [];
    for (let i = 0; i < count; i++) {
      segs.push({ i, start: i * segSize, end: Math.min(total, (i + 1) * segSize) - 1, got: 0, state: 'pending', retries: 0 });
    }
    return segs;
  }

  // ---------------- download loop (work stealing core) ----------------
  async _downloadLoop() {
    try {
      this._fd = await fs.promises.open(this.filePath, 'r+');
    } catch {
      this._fd = await fs.promises.open(this.filePath, 'w+'); // unknown-size streams: create
    }
    const pending = this.segments.filter(s => s.state !== 'done');
    this._queue = pending.map(s => s.i);
    this._recountReceived();

    const parallel = Math.min(this.connections, Math.max(1, this._queue.length));
    const workers = [];
    for (let i = 0; i < parallel; i++) {
      const localAddress = this.bindIps.length ? this.bindIps[i % this.bindIps.length] : undefined;
      workers.push(this._worker(localAddress, i));
    }
    this._startTicker();
    await Promise.all(workers);
    this._stopTicker();

    if (this._stopReason) throw new Error(this._stopReason);
    if (this.total != null && this.received < this.total) {
      // any un-retryable leftover?
      const bad = this.segments.find(s => s.state !== 'done');
      if (bad) throw new Error(`segment ${bad.i} failed: ${bad.errText || 'unknown error'}`);
      throw new Error('incomplete download');
    }
  }

  _claim() {
    // Atomically pop next pending segment (FIFO). Workers return here when idle.
    while (this._queue.length) {
      const i = this._queue.shift();
      const seg = this.segments[i];
      if (seg && seg.state === 'pending') { seg.state = 'active'; return seg; }
    }
    return null;
  }

  _requeue(seg) {
    seg.state = 'pending';
    this._queue.push(seg.i);
  }

  async _worker(localAddress, wid) {
    const widStr = `${wid}@${localAddress || 'default'}`;
    const bindAddress = this.fakeBind ? undefined : localAddress;
    while (!this._stopReason) {
      const seg = this._claim();
      if (!seg) return;
      this.workers.set(widStr, { localAddress, seg });
      try {
        await this._downloadSegment(seg, localAddress, bindAddress);
      } catch (err) {
        if (this._stopReason) return;
        seg.retries++;
        seg.errText = (err && err.message) || String(err);
        if (seg.retries > MAX_RETRIES) {
          seg.state = 'failed';
          this.workers.delete(widStr);
          this._stopReason = 'fail'; // stop siblings before they claim more work
          this._abortAll.abort();
          throw new Error(`segment ${seg.i} exceeded retries: ${seg.errText}`);
        }
        this._requeue(seg);
        await new Promise(r => setTimeout(r, Math.min(8000, 500 * seg.retries)));
      }
      this.workers.delete(widStr);
    }
  }

  async _downloadSegment(seg, label, bindAddress) {
    const from = seg.start + seg.got;
    if (seg.end != null && from > seg.end) { seg.state = 'done'; return; }
    const headers = { ...this.headers };
    if (seg.end != null) headers.range = `bytes=${from}-${seg.end}`;
    else if (seg.got > 0 && !this.acceptsRanges) throw new Error('cannot resume non-range stream');

    const localAddress = bindAddress; // what the socket actually binds to
    const widStr = label;
    const nicBucket = label ? (this.perNic[label] ||= { bytes: 0, speed: 0 }) : null;
    let limiter = null;
    if (this.nicLimits && label && this.nicLimits[label]) {
      limiter = tokenBucket(this.nicLimits[label]);
    }

    const opened = await httpx.openStream(this.finalUrl, {
      headers, localAddress, insecure: this.insecureTLS, proxy: this.proxy,
      signal: this._abortAll.signal,
    });
    const { res, socket } = opened;
    try {
      if (seg.end != null) {
        if (res.statusCode === 200 && (from > 0 || seg.start > 0)) {
          throw new Error('server ignored range request');
        }
        if (res.statusCode !== 206 && res.statusCode !== 200) {
          throw new Error(`HTTP ${res.statusCode}`);
        }
      } else if (res.statusCode >= 400) {
        throw new Error(`HTTP ${res.statusCode}`);
      }
      for await (const chunk of res) {
        let buf = chunk;
        if (limiter) await limiter.pace(buf.length, this._abortAll.signal);
        if (seg.end != null && seg.got + buf.length > seg.end - seg.start + 1) {
          buf = buf.subarray(0, seg.end - seg.start + 1 - seg.got);
        }
        if (buf.length) {
          await this._fd.write(buf, 0, buf.length, seg.start + seg.got);
          seg.got += buf.length;
          this.received += buf.length;
          if (nicBucket) { nicBucket.bytes += buf.length; }
        }
        if (seg.end != null && seg.got >= seg.end - seg.start + 1) break;
      }
      if (seg.end != null && seg.got < seg.end - seg.start + 1 && !this._stopReason) {
        throw new Error('connection ended early');
      }
      seg.state = 'done';
    } finally {
      try { res.destroy(); } catch {}
      try { socket.destroy(); } catch {}
    }
  }

  // ---------------- progress ----------------
  _startTicker() {
    this._lastPerNic = {};
    this._tick = setInterval(() => {
      const dt = 0.4;
      const delta = Math.max(0, this.received - this._lastBytes);
      this._lastBytes = this.received;
      const inst = delta / dt;
      this.speed = this.speed ? this.speed * 0.65 + inst * 0.35 : inst;
      for (const ip of Object.keys(this.perNic)) {
        const b = this.perNic[ip];
        const d = Math.max(0, b.bytes - (this._lastPerNic[ip] || 0));
        this._lastPerNic[ip] = b.bytes;
        b.speed = b.speed ? b.speed * 0.65 + (d / dt) * 0.35 : d / dt;
      }
      this._persistMetaTick();
      this.emit('progress', this.snapshot());
    }, 400);
    this._tick.unref?.();
  }

  _stopTicker() { if (this._tick) { clearInterval(this._tick); this._tick = null; } }

  snapshot() {
    return {
      id: this.id, url: this.url, file: this.fileName, dir: this.directory,
      state: this.state, error: this.error,
      received: this.received, total: this.total, speed: Math.round(this.speed),
      activeConns: this.workers.size,
      connections: this.connections,
      nics: this.bindIps,
      perNic: Object.fromEntries(Object.entries(this.perNic).map(([ip, v]) => [ip, { bytes: v.bytes, speed: Math.round(v.speed) }])),
      segmentsTotal: this.segments.length,
      segmentsDone: this.segments.filter(s => s.state === 'done').length,
      createdAt: this.createdAt, completedAt: this.completedAt,
    };
  }

  _emitState() { this.emit('state', this.snapshot()); }

  // ---------------- meta persistence (resume support) ----------------
  _persistMeta() {
    if (!this.metaPath || !this.fileName) return;
    try {
      const meta = {
        v: META_VERSION, url: this.url, finalUrl: this.finalUrl,
        file: this.fileName, dir: this.directory, total: this.total,
        acceptsRanges: this.acceptsRanges, headers: this.headers,
        bindIps: this.bindIps, connections: this.connections,
        segments: this.segments.map(s => ({ i: s.i, start: s.start, end: s.end, got: s.got, done: s.state === 'done' })),
      };
      fs.writeFileSync(this.metaPath, JSON.stringify(meta));
    } catch {}
  }
  _persistMetaTick() {
    // lightweight periodic save (every ~3s)
    const now = Date.now();
    if (!this._lastMetaSave || now - this._lastMetaSave > 3000) {
      this._lastMetaSave = now;
      this._persistMeta();
    }
  }
  _loadMeta() {
    try {
      const meta = JSON.parse(fs.readFileSync(this.metaPath, 'utf8'));
      if (meta.v !== META_VERSION) return false;
      this.total = meta.total;
      this.finalUrl = meta.finalUrl || this.url;
      this.acceptsRanges = !!meta.acceptsRanges;
      this.segments = meta.segments.map(s => ({ ...s, state: s.done ? 'done' : 'pending', retries: 0 }));
      this._recountReceived();
      return true;
    } catch { return false; }
  }
  _cleanupMeta() { try { fs.unlinkSync(this.metaPath); } catch {} }
  _cleanupFiles() {
    this._closeFd();
    this._cleanupMeta();
    try { fs.unlinkSync(this.filePath); } catch {}
  }
  _closeFd() { if (this._fd) { const f = this._fd; this._fd = null; f.close().catch(() => {}); } }
  _recountReceived() {
    this.received = this.segments.reduce((a, s) => a + s.got, 0);
  }

  // ---------------- helpers ----------------
  static async uniqueName(dir, name) {
    let clean = (name || 'download').split('?')[0].split('#')[0].trim() || 'download';
    clean = clean.replace(/[\\/:*?"<>|]/g, '_');
    let candidate = clean, n = 1;
    const ext = path.extname(clean), base = path.basename(clean, ext);
    while (fs.existsSync(path.join(dir, candidate)) || fs.existsSync(path.join(dir, candidate + '.dualnet.json'))) {
      candidate = `${base} (${n++})${ext}`;
    }
    return candidate;
  }

  static readResumable(metaPath) {
    try {
      const meta = JSON.parse(fs.readFileSync(metaPath, 'utf8'));
      if (meta.v !== META_VERSION || !meta.file) return null;
      return meta;
    } catch { return null; }
  }
}

// paced release used by tests to emulate fast/slow NICs deterministically
function tokenBucket(bytesPerSec) {
  return {
    async pace(n, signal) {
      const ms = (n / bytesPerSec) * 1000;
      if (ms > 0) await new Promise((res, rej) => {
        const t = setTimeout(res, ms);
        if (signal) signal.addEventListener('abort', () => { clearTimeout(t); rej(new Error('paused')); }, { once: true });
      });
    },
  };
}

module.exports = { DownloadJob };
