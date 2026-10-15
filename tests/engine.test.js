'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const http = require('http');
const { makeServer } = require('./server');
const { DownloadJob } = require('../src/main/downloader/job');
const { DownloadManager } = require('../src/main/downloader/manager');

const MB = 1024 * 1024;

function tmpdir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'dualnet-test-'));
}

function verifyFile(file, size, hash) {
  const buf = fs.readFileSync(file);
  assert.strictEqual(buf.length, size, 'file size mismatch');
  const h = crypto.createHash('sha256').update(buf).digest('hex');
  assert.strictEqual(h, hash, 'sha256 mismatch');
}

function listen(server) {
  return new Promise(res => server.listen(0, '127.0.0.1', () => res(server.address().port)));
}

async function waitDone(job, timeoutMs = 60000) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('timeout waiting job: ' + job.state)), timeoutMs);
    job.on('state', s => {
      if (['completed', 'failed', 'cancelled'].includes(s.state)) {
        clearTimeout(t); resolve(s);
      }
    });
  });
}

test('multi-connection ranged download + sha256 (12 conns, 32MB)', async () => {
  const { server, hash, content } = makeServer({ size: 32 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({
    url: `http://127.0.0.1:${port}/file`, directory: dir, connections: 12,
  });
  job.start().catch(() => {});
  const snap = await waitDone(job);
  assert.strictEqual(snap.state, 'completed');
  assert.strictEqual(snap.segmentsTotal > 12, true, 'should split into more segments than connections');
  verifyFile(path.join(dir, snap.file), content.length, hash);
  server.close();
});

test('WORK STEALING: fast NIC claims majority of segments (no permanent division)', async () => {
  const { server, hash, content } = makeServer({ size: 48 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({
    url: `http://127.0.0.1:${port}/file`, directory: dir, connections: 4,
    bindIps: ['fast', 'slow'],
    fakeBind: true,
    nicLimits: { fast: 6 * MB, slow: 1.5 * MB },   // fast NIC 4x rate
  });
  job.start().catch(() => {});
  const snap = await waitDone(job, 90000);
  assert.strictEqual(snap.state, 'completed', 'state=' + snap.state + ' err=' + snap.error);
  verifyFile(path.join(dir, snap.file), content.length, hash);
  const fast = snap.perNic.fast.bytes, slow = snap.perNic.slow.bytes;
  assert.ok(fast > slow * 2, `fast NIC (${fast}) should carry >2x slow NIC (${slow}) bytes`);
  // fast NIC should claim clearly more than half the segments
  assert.ok(snap.perNic.fast.bytes > content.length * 0.55,
    `fast NIC should carry >55% (got ${(100 * fast / content.length).toFixed(1)}%)`);
  server.close();
});

test('server without range support falls back to single stream', async () => {
  const { server, hash, content } = makeServer({ size: 3 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({ url: `http://127.0.0.1:${port}/noregion`, directory: dir });
  job.start().catch(() => {});
  const snap = await waitDone(job);
  assert.strictEqual(snap.state, 'completed');
  assert.strictEqual(snap.segmentsTotal, 1);
  verifyFile(path.join(dir, snap.file), content.length, hash);
  server.close();
});

test('redirects are followed before hashing', async () => {
  const { server, hash, content } = makeServer({ size: 2 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({ url: `http://127.0.0.1:${port}/redir`, directory: dir });
  job.start().catch(() => {});
  const snap = await waitDone(job);
  assert.strictEqual(snap.state, 'completed');
  verifyFile(path.join(dir, snap.file), content.length, hash);
  server.close();
});

test('pause then resume completes with exact bytes', async () => {
  const { server, hash, content } = makeServer({ size: 24 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({
    url: `http://127.0.0.1:${port}/file`, directory: dir, connections: 4,
    bindIps: ['fast'], fakeBind: true, nicLimits: { fast: 2 * MB },
  });
  // pause shortly after start
  job.start().catch(() => {});
  await new Promise(r => setTimeout(r, 700));
  job.pause();
  await new Promise(r => setTimeout(r, 400));
  assert.strictEqual(job.state, 'paused', 'state=' + job.state);
  assert.ok(job.received > 0, 'should have received some bytes');
  const receivedAtPause = job.received;
  assert.ok(fs.existsSync(job.metaPath), 'meta file persisted');
  await job.resume();
  const snap = await waitDone(job, 90000);
  assert.strictEqual(snap.state, 'completed', 'err=' + snap.error);
  assert.ok(job.received > receivedAtPause, 'resume made progress beyond pause point');
  verifyFile(path.join(dir, snap.file), content.length, hash);
  server.close();
});

test('error server -> failed state with message', async () => {
  const { server } = makeServer({ size: MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({ url: `http://127.0.0.1:${port}/err`, directory: dir });
  job.start().catch(() => {});
  const snap = await waitDone(job, 15000);
  assert.strictEqual(snap.state, 'failed');
  assert.ok(snap.error);
  server.close();
});

test('real loopback binding: localAddress=127.0.0.1 works end to end', async () => {
  const { server, hash, content } = makeServer({ size: 8 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({
    url: `http://127.0.0.1:${port}/file`, directory: dir, connections: 4,
    bindIps: ['127.0.0.1'], nicLimits: { '127.0.0.1': 100 * MB },
  });
  job.start().catch(() => {});
  const snap = await waitDone(job);
  assert.strictEqual(snap.state, 'completed', 'err=' + snap.error);
  verifyFile(path.join(dir, snap.file), content.length, hash);
  server.close();
});

test('manager: add -> snapshot history, retry path', async () => {
  const { server, hash, content } = makeServer({ size: 2 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const store = { get: (k) => ({ downloadDir: dir, maxConnections: 6, selectedNics: [], insecureTLS: false })[k] };
  const mgr = new DownloadManager({ store, resolveBindIps: () => [] });
  let doneSnap = null;
  mgr.on('job-state', s => { if (s.state === 'completed') doneSnap = s; });
  const first = mgr.add({ url: `http://127.0.0.1:${port}/file` });
  assert.ok(first.id);
  await new Promise(r => {
    const iv = setInterval(() => { if (doneSnap) { clearInterval(iv); r(); } }, 50);
    setTimeout(() => { clearInterval(iv); r(); }, 15000);
  });
  assert.ok(doneSnap, 'completed snapshot seen');
  verifyFile(path.join(dir, doneSnap.file), content.length, hash);
  assert.strictEqual(mgr.snapshotAll().length >= 1, true);
  server.close();
});

test('cancel removes partial files and meta', async () => {
  const { server } = makeServer({ size: 64 * MB });
  const port = await listen(server);
  const dir = tmpdir();
  const job = new DownloadJob({
    url: `http://127.0.0.1:${port}/file`, directory: dir, connections: 2,
    bindIps: ['x'], fakeBind: true, nicLimits: { x: 2 * MB },
  });
  job.start().catch(() => {});
  await new Promise(r => setTimeout(r, 600));
  job.cancel();
  await waitDone(job, 15000);
  await new Promise(r => setTimeout(r, 200));
  assert.strictEqual(fs.existsSync(job.metaPath), false, 'meta cleaned');
  server.close();
});
