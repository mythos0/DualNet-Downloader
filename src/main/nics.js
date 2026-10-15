'use strict';
// Network interface (NIC) enumeration + per-NIC speed testing.
const os = require('os');
const httpx = require('./downloader/httpx');
const { URL } = require('url');

// Return usable IPv4 NICs: [{ name, ip, mac }]
function listNics() {
  const ifaces = os.networkInterfaces();
  const out = [];
  const seen = new Set();
  for (const [name, addrs] of Object.entries(ifaces)) {
    for (const a of addrs || []) {
      if (a.family !== 'IPv4' || a.internal) continue;
      const key = a.address;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ name, ip: a.address, mac: a.mac || '' });
    }
  }
  return out;
}

// Resolve NIC selection (names) to local bind addresses.
// Empty selection => [] => engine uses default routing (combined via OS).
function resolveBindIps(selectedNames) {
  const nics = listNics();
  if (!selectedNames || !selectedNames.length) return nics.map(n => n.ip);
  const ips = [];
  for (const name of selectedNames) {
    const nic = nics.find(n => n.name === name);
    if (nic && !ips.includes(nic.ip)) ips.push(nic.ip);
  }
  return ips; // may be [] if user-picked NICs vanished -> caller falls back to all
}

// Speed-test one NIC (or default route if localAddress null) by downloading `url`.
// Returns { ok, mbps, bytes, ms, error? }
async function speedTestNic(url, { localAddress = null, maxMs = 8000, maxBytes = 64 * 1024 * 1024, headers = {} } = {}) {
  const started = process.hrtime.bigint();
  let bytes = 0;
  try {
    await httpx.streamDownload(url, {
      headers: { 'User-Agent': 'DualNet-SpeedTest/3.0', ...headers },
      localAddress,
      insecure: false,
      signal: AbortSignal.timeout(maxMs),
      onData(chunk) {
        bytes += chunk.length;
        if (bytes >= maxBytes) throw new httpx.StopDownload();
      },
    });
    const ms = Number(process.hrtime.bigint() - started) / 1e6;
    return { ok: bytes > 0, mbps: +( (bytes * 8) / (ms / 1000) / 1e6 ).toFixed(2), bytes, ms: Math.round(ms) };
  } catch (err) {
    if (err instanceof httpx.StopDownload || err?.name === 'AbortError' || err?.code === 'ABORT_ERR') {
      const ms = Number(process.hrtime.bigint() - started) / 1e6;
      return { ok: bytes > 0, mbps: +( (bytes * 8) / (ms / 1000) / 1e6 ).toFixed(2), bytes, ms: Math.round(ms) };
    }
    return { ok: false, mbps: 0, bytes, ms: 0, error: (err && err.message) || String(err) };
  }
}

module.exports = { listNics, resolveBindIps, speedTestNic };
