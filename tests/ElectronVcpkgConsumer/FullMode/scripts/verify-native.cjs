"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { EventEmitter } = require("node:events");

const sampleRoot = path.resolve(__dirname, "..");
const nativeDirectory = path.join(
  sampleRoot,
  "vcpkg_installed",
  "x64-windows",
  "tools",
  "guance-windows-native",
);
const adapterPath = path.join(nativeDirectory, "electron", "main", "index.cjs");

async function main() {
  assert.ok(fs.existsSync(adapterPath), `Missing installed Electron adapter: ${adapterPath}`);
  const { startFullMode } = require(adapterPath);
  const ipcMain = new EventEmitter();
  let nativeOutput = "";
  const bridge = await startFullMode({
    ipcMain,
    nativeDirectory,
    nativeSettings: {
      applicationId: "electron-full-native-smoke",
      datakitUrl: "http://127.0.0.1:9",
      service: "electron-full-native-smoke",
      environment: "test",
      version: "0.1.0",
      cachePath: path.join(sampleRoot, "vcpkg_installed", "verify-native-cache"),
      sampleRate: 1,
      replayEnabled: true,
      replaySampleRate: 1,
      replayPrivacy: "mask-user-input",
      debug: true,
      httpTimeoutMs: 100,
    },
    onNativeOutput(_stream, chunk) {
      nativeOutput += chunk;
    },
  });
  const webContents = new EventEmitter();
  webContents.mainFrame = {};
  webContents.isDestroyed = () => false;
  bridge.attachWindow(webContents);
  assert.equal(bridge.capabilities.replay, true);
  assert.equal(bridge.capabilities.replayPrivacy, "mask-user-input");
  const configurationEvent = {
    sender: webContents,
    senderFrame: webContents.mainFrame,
  };
  ipcMain.emit("guance:electron-rum:configuration:v1", configurationEvent);
  assert.deepEqual(configurationEvent.returnValue, {
    replayEnabled: true,
    replayPrivacy: "mask-user-input",
  });
  ipcMain.emit(
    "guance:electron-rum:browser-event:v1",
    { sender: webContents, senderFrame: webContents.mainFrame },
    JSON.stringify({
      name: "rum",
      data: {
        measurement: "view",
        time: Date.now(),
        tags: {
          view_id: "electron-full-launch",
          view_name: "electron.full.native-smoke",
          view_referrer: "file:///full-splash.html",
        },
        fields: { view_loading_time: 1 },
      },
    }),
  );
  webContents.emit("did-finish-load");
  ipcMain.emit(
    "guance:electron-rum:browser-event:v1",
    { sender: webContents, senderFrame: webContents.mainFrame },
    JSON.stringify({
      name: "session_replay",
      view: { id: "electron-full-replay" },
      data: { type: 2, timestamp: Date.now(), data: { source: 0, nodes: [] } },
    }),
  );
  await bridge.stop();
  assert.doesNotMatch(nativeOutput, /rejected invalid bridge input/);
  assert.match(nativeOutput, /@guance-capabilities\tprotocol=1\trum=1\tlog=0\treplay=1\treplay_privacy=mask-user-input/);
  assert.match(nativeOutput, /\[Guance\.RUM\.NativeBridge\] ready/);
  assert.match(nativeOutput, /\benqueued=2\b/);
  assert.match(nativeOutput, /launch type=launch_cold/);
  console.log("PASS Full Mode native path: public adapter -> Bridge EXE -> owned SDK.");
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
