'use strict';
// Rich HTML test server for visual verification (real Chromium rendering).
const http = require('http');

const PAGE = `<!doctype html><html><head><meta charset="utf-8"><title>DualNet Test Page — Modern Web</title>
<style>
  *{margin:0;padding:0;box-sizing:border-box}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:linear-gradient(135deg,#667eea 0%,#764ba2 100%);min-height:100vh;color:#fff}
  header{padding:28px 40px;display:flex;justify-content:space-between;align-items:center;background:rgba(255,255,255,.08);backdrop-filter:blur(8px)}
  h1{font-size:26px;font-weight:600}
  .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:18px;padding:34px 40px}
  .card{background:rgba(255,255,255,.12);border-radius:14px;padding:22px;backdrop-filter:blur(6px);box-shadow:0 8px 24px rgba(0,0,0,.18);transition:transform .2s}
  .card:hover{transform:translateY(-4px)}
  .card h2{font-size:17px;margin-bottom:8px}
  .card p{font-size:13px;opacity:.85;line-height:1.55}
  button{margin-top:14px;background:#fff;color:#5b3fa0;border:0;border-radius:999px;padding:8px 18px;font-weight:600;cursor:pointer}
  #counter{font-size:40px;font-weight:700;margin:6px 0}
  footer{padding:20px 40px;font-size:12px;opacity:.7}
  .dl{color:#ffd76a;text-decoration:none;font-weight:600}
</style></head><body>
<header><h1>⚡ DualNet Chromium Test</h1><div>JS + CSS + gradients working</div></header>
<div class="grid">
  <div class="card"><h2>JavaScript</h2><div id="counter">0</div><button onclick="tick()">Count up</button></div>
  <div class="card"><h2>CSS Grid &amp; blur</h2><p>This card uses grid layout, rgba backgrounds and backdrop-filter — impossible in reader-view renderers.</p></div>
  <div class="card"><h2>Download links</h2><p>Files on the engine server: <a class="dl" href="/big" download>big-file.bin (8 MB)</a></p></div>
  <div class="card"><h2>Real DOM APIs</h2><p id="clock">—</p></div>
</div>
<footer>rendered by actual Chromium · ${new Date().toISOString()}</footer>
<script>
  let n = 0;
  function tick(){ document.getElementById('counter').textContent = ++n; }
  setInterval(()=>{ document.getElementById('clock').textContent = new Date().toLocaleTimeString(); }, 500);
</script>
</body></html>`;

function makeHtmlServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      if (req.url.startsWith('/big')) {
        const total = 8 * 1024 * 1024;
        res.writeHead(200, {
          'Content-Type': 'application/octet-stream',
          'Content-Length': total,
          'Content-Disposition': 'attachment; filename="big-file.bin"',
          'Accept-Ranges': 'bytes',
        });
        const chunk = Buffer.alloc(256 * 1024, 0x41);
        let sent = 0;
        const iv = setInterval(() => {
          const n = Math.min(chunk.length, total - sent);
          res.write(chunk.subarray(0, n));
          sent += n;
          if (sent >= total) { clearInterval(iv); res.end(); }
        }, 5);
        req.on('close', () => clearInterval(iv));
        return;
      }
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(PAGE);
    });
    server.listen(0, '127.0.0.1', () => resolve(server.address().port));
    server._port = null;
  });
}

module.exports = { makeHtmlServer };
