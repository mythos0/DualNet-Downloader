'use strict';
// DownloadController: routes browser-initiated downloads + manual URL adds
// into the DownloadManager, applies NIC selection, broadcasts progress.
const path = require('path');
const { DownloadManager } = require('./downloader/manager');
const nics = require('./nics');

class DownloadController {
  constructor({ store, sentry }) {
    this.store = store;
    this.sentry = sentry;
    this.manager = new DownloadManager({
      store,
      resolveBindIps: (names) => {
        const ips = nics.resolveBindIps(names);
        if (!ips.length) return nics.listNics().map(n => n.ip);
        return ips;
      },
    });
    this._lastSent = new Map(); // job id -> ts (progress throttle)
    this.manager.on('job-state', (snap) => this._emit('downloads-changed', snap));
    this.manager.on('job-progress', (snap) => {
      const now = Date.now();
      const last = this._lastSent.get(snap.id) || 0;
      if (now - last < 500) return;
      this._lastSent.set(snap.id, now);
      this._emit('download-progress', snap);
    });
  }

  _emit(channel, payload) {
    if (this._win && !this._win.isDestroyed()) {
      try { this._win.webContents.send(channel, payload); } catch {}
    }
  }
  attachWindow(win) { this._win = win; }

  // Called from will-download (info captured before preventDefault)
  async addFromBrowser(info) {
    let directory = this.store.get('downloadDir') || this.manager.defaultDir();
    let fileName = null;
    if (this.store.get('askWhereToSave')) {
      try {
        const { dialog } = require('electron');
        const r = await dialog.showSaveDialog(this._win, {
          defaultPath: path.join(directory, info.filename || 'download'),
        });
        if (r.canceled || !r.filePath) return null;
        directory = path.dirname(r.filePath);
        fileName = path.basename(r.filePath);
      } catch {}
    }
    const nicNames = this.store.get('selectedNics') || [];
    const snap = this.manager.add({
      url: info.url,
      headers: info.headers || {},
      fileName,
      nicNames,
      proxy: null,    // downloads go direct (multi-NIC); browser tab proxy is for pages
    });
    this.sentry.addBreadcrumb(`browser download queued: ${info.filename}`, 'download', { url: info.url });
    return snap;
  }

  addManual({ url, fileName, nicMode, connections }) {
    const nicNames = nicMode && nicMode !== 'combined' ? [nicMode] : (this.store.get('selectedNics') || []);
    const snap = this.manager.add({
      url,
      fileName: fileName || null,
      nicNames,
      connections: connections || undefined,
    });
    return snap;
  }

  list() { return this.manager.snapshotAll(); }
  pause(id) { this.manager.pause(id); }
  resume(id) { this.manager.resume(id); }
  cancel(id) { this.manager.cancel(id); }
  remove(id) { this.manager.remove(id); }
  retry(id) { return this.manager.retry(id); }

  resumables() { return this.manager.findResumables(); }
  resumeFromDisk(meta) { return this.manager.resumeFromDisk(meta); }

  async chooseDir(win, current) {
    const { dialog } = require('electron');
    const r = await dialog.showOpenDialog(win, { properties: ['openDirectory'], defaultPath: current });
    if (!r.canceled && r.filePaths[0]) {
      this.store.set('downloadDir', r.filePaths[0]);
      return r.filePaths[0];
    }
    return null;
  }

  openFolder(dir) {
    const { shell } = require('electron');
    shell.openPath(dir || this.store.get('downloadDir') || this.manager.defaultDir());
  }
  openFile(p) {
    const { shell } = require('electron');
    shell.openPath(p);
  }
  showInFolder(p) {
    const { shell } = require('electron');
    shell.showItemInFolder(p);
  }
}

module.exports = { DownloadController };
