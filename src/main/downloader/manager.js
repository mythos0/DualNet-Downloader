'use strict';
// DownloadManager: owns all DownloadJobs, exposes app-level operations,
// throttled progress broadcasting, and resume-scan support.
const fs = require('fs');
const path = require('path');
const { EventEmitter } = require('events');
const { DownloadJob } = require('./job');

class DownloadManager extends EventEmitter {
  constructor({ store, resolveBindIps }) {
    super();
    this.store = store;
    this.resolveBindIps = resolveBindIps || (() => []);
    this.jobs = new Map();      // id -> job
    this.history = [];          // finished snapshots (completed/failed/cancelled) capped
  }

  defaultDir() {
    return this.store.get('downloadDir') || this.store._defaultDownloadDir || '/tmp';
  }

  /**
   * add({ url, headers, fileName?, nicNames?|nicIps?, connections?, proxy?, directory? })
   */
  add(opts = {}) {
    const directory = opts.directory || this.defaultDir();
    const nicIps = opts.nicIps && opts.nicIps.length
      ? opts.nicIps
      : this.resolveBindIps(opts.nicNames || this.store.get('selectedNics'));
    const job = new DownloadJob({
      url: opts.url,
      headers: opts.headers || {},
      directory,
      fileName: opts.fileName || null,
      bindIps: nicIps,
      connections: opts.connections || this.store.get('maxConnections') || 12,
      insecureTLS: opts.insecureTLS ?? this.store.get('insecureTLS'),
      proxy: opts.proxy || null,
    });
    this.jobs.set(job.id, job);
    job.on('state', (snap) => {
      this.emit('job-state', snap);
      if (['completed', 'failed', 'cancelled'].includes(snap.state)) {
        this.history.unshift(snap);
        this.history = this.history.slice(0, 100);
        this.jobs.delete(job.id);
      }
    });
    job.on('progress', (snap) => this.emit('job-progress', snap));
    job.start().catch(() => {});
    return job.snapshot();
  }

  snapshotAll() {
    const active = [...this.jobs.values()].map(j => j.snapshot());
    return [...active, ...this.history];
  }

  get(id) {
    const job = this.jobs.get(id);
    if (job) return job.snapshot();
    return this.history.find(s => s.id === id) || null;
  }

  pause(id) { this.jobs.get(id)?.pause(); }
  resume(id) {
    const job = this.jobs.get(id);
    if (job) { job.resume().catch(() => {}); return; }
    // resume from disk meta
    const snap = this.history.find(s => s.id === id);
    if (snap) this.retry(id);
  }

  cancel(id) {
    const job = this.jobs.get(id);
    if (job) { job.cancel(); return; }
    this.history = this.history.filter(s => s.id !== id);
  }

  remove(id) {
    const job = this.jobs.get(id);
    if (job) { job.cancel(); }
    this.history = this.history.filter(s => s.id !== id);
  }

  retry(id) {
    const snap = this.history.find(s => s.id === id) || this.get(id);
    if (!snap) return null;
    this.remove(id);
    return this.add({ url: snap.url, fileName: null, directory: snap.dir });
  }

  // scan download dir for *.dualnet.json produced by previous sessions
  findResumables() {
    const dir = this.defaultDir();
    try {
      return fs.readdirSync(dir)
        .filter(f => f.endsWith('.dualnet.json'))
        .map(f => DownloadJob.readResumable(path.join(dir, f)))
        .filter(Boolean)
        .map(meta => ({ file: meta.file, url: meta.url, dir: meta.dir, received: meta.segments.reduce((a, s) => a + (s.got || 0), 0), total: meta.total }));
    } catch { return []; }
  }

  resumeFromDisk(meta) {
    return this.add({
      url: meta.url,
      fileName: meta.file,
      directory: meta.dir,
      // engine picks up existing .dualnet.json automatically in _probeAndPrepare
    });
  }
}

module.exports = { DownloadManager };
