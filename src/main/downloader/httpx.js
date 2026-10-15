'use strict';
// Low-level HTTP(S) helpers with:
//  - localAddress binding (per-NIC routing)
//  - manual redirect following (keeping our socket options)
//  - optional HTTP proxy (absolute-URI for http, CONNECT for https)
//  - streaming with abort support
const http = require('http');
const https = require('https');
const net = require('net');
const tls = require('tls');
const { URL } = require('url');

class StopDownload extends Error {
  constructor() { super('stop'); this.name = 'StopDownload'; }
}

function parseProxy(proxyUrl) {
  if (!proxyUrl) return null;
  try {
    const u = new URL(proxyUrl);
    return { protocol: u.protocol.replace(':', ''), host: u.hostname, port: u.port ? +u.port : (u.protocol === 'https:' ? 443 : 80) };
  } catch { return null; }
}

// Establish a socket to target, optionally through an HTTP proxy.
function connectSocket({ targetHost, targetPort, useTLS, localAddress, proxy, timeout = 20000, insecure }) {
  return new Promise((resolve, reject) => {
    const finish = (socket, alreadyTLS) => {
      socket.setTimeout(0);
      resolve(alreadyTLS ? socket : { socket });
    };
    if (proxy && proxy.protocol === 'https') {
      // TLS-to-proxy then (optionally) CONNECT
      const ps = tls.connect({ host: proxy.host, port: proxy.port, rejectUnauthorized: !insecure }, () => {
        if (!useTLS) return finish(ps, true); // weird but treat as open pipe
        ps.write(`CONNECT ${targetHost}:${targetPort} HTTP/1.1\r\nHost: ${targetHost}:${targetPort}\r\n\r\n`);
      });
      ps.setTimeout(timeout, () => ps.destroy(new Error('proxy connect timeout')));
      let buf = '';
      const onData = (d) => {
        buf += d.toString('latin1');
        if (buf.includes('\r\n\r\n')) {
          ps.removeListener('data', onData);
          if (!/^HTTP\/1\.[01] 200/.test(buf)) { ps.destroy(); return reject(new Error('proxy CONNECT failed: ' + buf.split('\r\n')[0])); }
          const s = useTLS
            ? tls.connect({ socket: ps, servername: targetHost, rejectUnauthorized: !insecure })
            : ps;
          if (useTLS) s.once('secureConnect', () => finish(s, true)); else finish(s, true);
          if (useTLS) s.once('error', reject);
        }
      };
      ps.on('data', onData);
      ps.once('error', reject);
      return;
    }
    if (proxy) { // plain http proxy
      const s = net.connect({ host: proxy.host, port: proxy.port, localAddress }, () => {
        if (useTLS) {
          s.write(`CONNECT ${targetHost}:${targetPort} HTTP/1.1\r\nHost: ${targetHost}:${targetPort}\r\n\r\n`);
        } else {
          finish(s, false); // request will use absolute URI
        }
      });
      s.setTimeout(timeout, () => s.destroy(new Error('proxy connect timeout')));
      let buf = '';
      const onData = (d) => {
        buf += d.toString('latin1');
        if (buf.includes('\r\n\r\n')) {
          s.removeListener('data', onData);
          if (!/^HTTP\/1\.[01] 200/.test(buf)) { s.destroy(); return reject(new Error('proxy CONNECT failed: ' + buf.split('\r\n')[0])); }
          const t = tls.connect({ socket: s, servername: targetHost, rejectUnauthorized: !insecure });
          t.once('secureConnect', () => finish(t, true));
          t.once('error', reject);
        }
      };
      s.on('data', onData);
      s.once('error', reject);
      return;
    }
    // direct
    const s = net.connect({ host: targetHost, port: targetPort, localAddress, family: localAddress && localAddress.includes(':') ? 6 : 4 }, () => {
      if (useTLS) {
        const t = tls.connect({ socket: s, servername: targetHost, rejectUnauthorized: !insecure });
        t.once('secureConnect', () => finish(t, true));
        t.once('error', reject);
      } else finish(s, false);
    });
    s.setTimeout(timeout, () => s.destroy(new Error('connect timeout')));
    s.once('error', reject);
  });
}

// Issue one HTTP request over an established (possibly TLS) socket.
// Returns { res, req }.
function rawRequest({ socket, method, urlObj, headers, useTLS, throughProxy }) {
  return new Promise((resolve, reject) => {
    const lib = useTLS ? https : http;
    const path = (throughProxy && !useTLS) ? urlObj.href : (urlObj.pathname + urlObj.search);
    const hdrs = { host: urlObj.host, 'accept-encoding': 'identity', connection: 'close', ...headers };
    const req = lib.request({
      // NOTE: do NOT pass agent:false — that builds a default Agent which
      // ignores createConnection and dials host:80 itself. Omitting agent
      // makes ClientRequest use our createConnection directly.
      createConnection: () => socket,
      method,
      path,
      headers: hdrs,
    }, (res) => resolve({ res, req }));
    req.on('error', reject);
    req.end();
  });
}

// High-level: GET with redirects. Returns { status, headers, res, finalUrl, req }.
// opts: { headers, localAddress, proxy, insecure, signal, maxRedirects }
async function openStream(url, opts = {}) {
  const maxRedirects = opts.maxRedirects ?? 12;
  let current = url;
  for (let i = 0; i <= maxRedirects; i++) {
    const u = new URL(current);
    const useTLS = u.protocol === 'https:';
    if (u.protocol !== 'http:' && u.protocol !== 'https:') throw new Error('unsupported protocol: ' + u.protocol);
    const proxy = parseProxy(opts.proxy);
    const port = u.port ? +u.port : (useTLS ? 443 : 80);
    const conn = await connectSocket({
      targetHost: u.hostname, targetPort: port, useTLS,
      localAddress: opts.localAddress || undefined,
      proxy, insecure: opts.insecure,
    });
    const sock = conn.socket || conn;
    const { res, req } = await rawRequest({
      socket: sock, method: 'GET', urlObj: u,
      headers: opts.headers || {}, useTLS, throughProxy: !!proxy,
    });
    // wire abort
    if (opts.signal) {
      const onAbort = () => { try { req.destroy(new Error('aborted')); } catch {} try { sock.destroy(); } catch {} };
      if (opts.signal.aborted) onAbort();
      else opts.signal.addEventListener('abort', onAbort, { once: true });
    }
    if ([301, 302, 303, 307, 308].includes(res.statusCode)) {
      const loc = res.headers.location;
      res.resume(); // drain
      try { sock.destroy(); } catch {}
      if (!loc) throw new Error(`redirect ${res.statusCode} without Location`);
      current = new URL(loc, current).href;
      continue;
    }
    res.on('error', () => { try { sock.destroy(); } catch {} });
    return { status: res.statusCode, headers: res.headers, res, finalUrl: current, req, socket: sock };
  }
  throw new Error('too many redirects');
}

// Convenience: stream a body to onData (used by speed test / probing small files)
async function streamDownload(url, { headers, localAddress, insecure, signal, onData, proxy }) {
  const { res, socket } = await openStream(url, { headers, localAddress, insecure, signal, proxy });
  if (res.statusCode >= 400) { res.resume(); try { socket.destroy(); } catch {} throw new Error(`HTTP ${res.statusCode}`); }
  try {
    for await (const chunk of res) {
      onData(chunk);
    }
  } finally {
    try { socket.destroy(); } catch {}
  }
}

// Probe: range support + size + filename. Uses HEAD then falls back to Range GET.
async function probe(url, { headers = {}, localAddress, insecure, proxy } = {}) {
  const tryOpen = async (rangeHeader) => {
    const { status, headers: h, res, finalUrl, socket } = await openStream(url, {
      headers: { ...headers, ...(rangeHeader ? { range: rangeHeader } : {}) },
      localAddress, insecure, proxy,
    });
    return { status, headers: h, res, finalUrl, socket };
  };
  const close = (x) => { try { x.res.resume(); } catch {} try { x.socket.destroy(); } catch {} };

  let r = await tryOpen('bytes=0-0');
  if (r.status === 206) {
    const cr = r.headers['content-range'] || '';
    const m = /bytes\s+\d+-\d+\/(\d+|\*)/i.exec(cr);
    const total = m && m[1] !== '*' ? parseInt(m[1], 10) : null;
    const out = {
      ok: true, status: 206, total, acceptsRanges: true, finalUrl: r.finalUrl,
      filename: filenameFromHeaders(r.headers, r.finalUrl),
      contentType: r.headers['content-type'] || '',
    };
    close(r);
    return out;
  }
  if (r.status >= 400 && r.status < 500 && r.status !== 416) {
    close(r);
    throw new Error(`HTTP ${r.status}`);
  }
  // full response (200) — read total from content-length, no ranges
  const totalRaw = r.headers['content-length'];
  const out = {
    ok: r.status < 400, status: r.status,
    total: totalRaw != null ? parseInt(totalRaw, 10) : null,
    acceptsRanges: false, finalUrl: r.finalUrl,
    filename: filenameFromHeaders(r.headers, r.finalUrl),
    contentType: r.headers['content-type'] || '',
  };
  close(r);
  return out;
}

function filenameFromHeaders(headers, url) {
  const cd = headers['content-disposition'] || '';
  let m = /filename\*=UTF-8''([^;]+)/i.exec(cd);
  if (m) { try { return decodeURIComponent(m[1]).replace(/["\\]/g, '').trim(); } catch {} }
  m = /filename\s*=\s*"([^"]+)"/i.exec(cd) || /filename\s*=\s*([^;]+)/i.exec(cd);
  if (m) return m[1].replace(/["']/g, '').trim();
  try {
    const p = new URL(url).pathname;
    const base = decodeURIComponent(p.split('/').pop() || '');
    return base || 'download';
  } catch { return 'download'; }
}

module.exports = { openStream, streamDownload, probe, StopDownload, parseProxy };
