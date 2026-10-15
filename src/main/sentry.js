'use strict';
// Minimal zero-dependency Sentry client for the main process.
// Sends exception/message envelopes to the user's real project DSN.
// Override with DUALNET_SENTRY_DSN env var; disable with sentryEnabled=false.
const https = require('https');
const zlib = require('zlib');
const crypto = require('crypto');

const DEFAULT_DSN = 'https://960688915c323a44c9569f8d9c189eee@o4512147759235072.ingest.us.sentry.io/4512249743147008';

function parseDsn(dsn) {
  try {
    const u = new URL(dsn);
    const projectId = u.pathname.replace('/', '');
    return {
      host: u.hostname, port: u.port || 443, projectId,
      publicKey: u.username, path: u.pathname,
    };
  } catch { return null; }
}

class SentryClient {
  constructor({ dsn, release, environment, enabled = true, maxQueue = 30 } = {}) {
    this.dsn = dsn || process.env.DUALNET_SENTRY_DSN || DEFAULT_DSN;
    this.parsed = parseDsn(this.dsn);
    this.release = release;
    this.environment = environment || 'production';
    this.enabled = enabled && !!this.parsed;
    this.queue = [];
    this.maxQueue = maxQueue;
    this.breadcrumbs = [];
  }

  addBreadcrumb(message, category = 'app', data = null) {
    this.breadcrumbs.push({ message, category, data, timestamp: Date.now() / 1000 });
    if (this.breadcrumbs.length > 50) this.breadcrumbs.shift();
  }

  _payload(event) {
    const now = new Date().toISOString();
    return {
      event_id: event.event_id || crypto.randomUUID(),
      timestamp: now,
      platform: 'node',
      environment: this.environment,
      release: this.release,
      server_name: require('os').hostname(),
      breadcrumbs: this.breadcrumbs.slice(-20).map(b => ({ message: b.message, category: b.category, data: b.data, timestamp: b.timestamp })),
      ...event,
    };
  }

  captureException(err, extra = {}) {
    if (!this.enabled) return;
    const event = this._payload({
      level: 'error',
      exception: {
        values: [{
          type: err && err.constructor ? err.constructor.name : 'Error',
          value: (err && err.message) || String(err),
          stacktrace: err && err.stack ? { frames: [{ filename: String(err.stack).split('\n')[1] || '' }] } : undefined,
        }],
      },
      extra,
    });
    this._enqueue(event);
  }

  captureMessage(message, level = 'info', extra = {}) {
    if (!this.enabled) return;
    this._enqueue(this._payload({ level, message, extra }));
  }

  _enqueue(event) {
    this.queue.push(event);
    if (this.queue.length > this.maxQueue) this.queue.shift();
    if (!this._flushTimer) {
      this._flushTimer = setTimeout(() => {
        this._flushTimer = null;
        this.flush().catch(() => {});
      }, 100);
    }
  }

  _send(event) {
    return new Promise((resolve) => {
      if (!this.enabled || !this.parsed) return resolve(false);
      const envelope = JSON.stringify({ event_id: event.event_id, sent_at: new Date().toISOString() }) + '\n' +
        JSON.stringify({ type: event.exception ? 'event' : 'event' }) + '\n' +
        JSON.stringify(event);
      const body = Buffer.from(envelope);
      const req = https.request({
        host: this.parsed.host, port: +this.parsed.port, path: `/api/${this.parsed.projectId}/envelope/`,
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-sentry-envelope',
          'Content-Length': body.length,
          'X-Sentry-Auth': `Sentry sentry_version=7, sentry_key=${this.parsed.publicKey}, sentry_client=dualnet/3.0`,
        },
        timeout: 8000,
      }, (res) => {
        res.resume();
        const ok = res.statusCode >= 200 && res.statusCode < 300;
        if (process.env.DUALNET_SENTRY_DEBUG) console.log(`[sentry] ${event.event_id?.slice(0, 8)} -> ${res.statusCode}`);
        resolve(ok);
      });
      req.on('error', () => resolve(false));
      req.on('timeout', () => { req.destroy(); resolve(false); });
      req.end(zlib.gzipSync ? body : body);
    });
  }

  async flush(timeoutMs = 6000) {
    if (this._flushTimer) { clearTimeout(this._flushTimer); this._flushTimer = null; }
    const pending = this.queue.splice(0, this.queue.length);
    if (!pending.length) return 0;
    let sent = 0;
    await Promise.race([
      Promise.all(pending.map(e => this._send(e).then(ok => { if (ok) sent++; }))),
      new Promise(r => setTimeout(r, timeoutMs)),
    ]);
    return sent;
  }
}

module.exports = { SentryClient, DEFAULT_DSN };
