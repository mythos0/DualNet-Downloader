// Rasterize build/icon.svg -> build/icon.png (512x512) using Electron itself
const { app, BrowserWindow } = require('electron');
const fs = require('fs');
const path = require('path');

app.whenReady().then(async () => {
  const svg = fs.readFileSync(path.join(__dirname, '..', 'build', 'icon.svg'), 'utf8');
  const win = new BrowserWindow({ width: 512, height: 512, show: false, webPreferences: { offscreen: true } });
  await win.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(
    `<body style="margin:0;width:512px;height:512px;overflow:hidden"><img src="data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}" width="512" height="512"></body>`
  ));
  await new Promise(r => setTimeout(r, 800));
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(__dirname, '..', 'build', 'icon.png'), img.toPNG());
  console.log('icon.png written:', img.getSize());
  app.quit();
});
