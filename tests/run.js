'use strict';
// Headless engine test runner (no Electron needed).
const { spawnSync } = require('child_process');
const path = require('path');

const r = spawnSync(process.execPath, ['--test', path.join(__dirname, 'engine.test.js')], {
  stdio: 'inherit',
});
process.exit(r.status || 0);
