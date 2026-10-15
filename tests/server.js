'use strict';
// Test HTTP server: Range support + SHA-256 content verification + per-source
// throttling + forced response behaviors, for engine integration tests.
const http = require('http');
const crypto = require('crypto');

function makeServer({ size = 1024 * 1024, seed = 'dualnet-test-data', throttle = null } = {}) {
  // deterministic content
  const sha = crypto.createHash('sha256');
  const chunkPool = crypto.randomBytes(64 * 1024);
  const content = Buffer.alloc(size);
  for (let off = 0; off < size; off += chunkPool.length) {
    chunkPool.copy(content, off, 0, Math.min(chunkPool.length, size - off));
  }
  sha.update(content);
  const hash = sha.digest('hex');

  const server = http.createServer((req, res) => {
    const range = req.headers.range;
    if (req.url.startsWith('/err')) {
      res.writeHead(500); res.end('boom'); return;
    }
    if (req.url.startsWith('/redir')) {
      res.writeHead(302, { Location: '/file' }); res.end(); return;
    }
    if (req.url.startsWith('/noregion')) {
      // no ranges: full 200
      res.writeHead(200, { 'Content-Length': size, 'Content-Type': 'application/octet-stream' });
      res.end(content); return;
    }
    if (!range) {
      res.writeHead(200, {
        'Content-Length': size,
        'Accept-Ranges': 'bytes',
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="testfile.bin"',
      });
      res.end(content); return;
    }
    const m = /bytes=(\d*)-(\d*)/.exec(range);
    let start = m && m[1] ? parseInt(m[1], 10) : 0;
    let end = m && m[2] ? parseInt(m[2], 10) : size - 1;
    if (isNaN(start) || start >= size) { res.writeHead(416); res.end(); return; }
    end = Math.min(end, size - 1);
    const slice = content.subarray(start, end + 1);
    res.writeHead(206, {
      'Content-Range': `bytes ${start}-${end}/${size}`,
      'Content-Length': slice.length,
      'Accept-Ranges': 'bytes',
      'Content-Type': 'application/octet-stream',
    });
    if (!throttle) { res.end(slice); return; }
    // throttled write: bytes/sec
    let pos = 0;
    const timer = setInterval(() => {
      const n = Math.max(1, Math.floor(throttle.bps / 20)); // 20 writes/sec
      const piece = slice.subarray(pos, pos + n);
      res.write(piece);
      pos += piece.length;
      if (pos >= slice.length) { clearInterval(timer); res.end(); }
    }, 50);
    req.on('close', () => clearInterval(timer));
  });
  return { server, hash, content };
}

module.exports = { makeServer };
