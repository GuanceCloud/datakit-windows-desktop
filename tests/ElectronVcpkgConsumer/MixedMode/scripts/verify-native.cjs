"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { EventEmitter, once } = require("node:events");
const { spawn } = require("node:child_process");

const sampleRoot = path.resolve(__dirname, "..");
const hostPath = path.join(
  sampleRoot,
  "native-host",
  "build",
  "Release",
  "guance_windows_electron_mixed_host.exe",
);
const adapterPath = path.join(
  sampleRoot,
  "vcpkg_installed",
  "x64-windows",
  "tools",
  "guance-windows-native",
  "electron",
  "main",
  "index.cjs",
);

async function main() {
  assert.ok(fs.existsSync(hostPath), `Missing Mixed Mode C++ host: ${hostPath}`);
  assert.ok(fs.existsSync(adapterPath), `Missing installed Electron adapter: ${adapterPath}`);
  const { connectMixedMode } = require(adapterPath);
  const pipeName = `guance-electron-mixed-native-${process.pid}`;
  const child = spawn(hostPath, [], {
    cwd: path.dirname(hostPath),
    env: {
      ...process.env,
      GUANCE_RUM_NATIVE_DATAKIT_URL: "http://127.0.0.1:9",
      GUANCE_RUM_NATIVE_APP_ID: "electron-mixed-native-smoke",
      GUANCE_RUM_NATIVE_SERVICE: "electron-mixed-native-smoke",
      GUANCE_RUM_NATIVE_ENV: "test",
      GUANCE_RUM_NATIVE_VERSION: "0.1.0",
      GUANCE_RUM_NATIVE_CACHE_PATH: path.join(
        sampleRoot,
        "vcpkg_installed",
        "verify-native-cache",
      ),
      GUANCE_RUM_NATIVE_SAMPLE_RATE: "1",
      GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED: "1",
      GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE: "1",
      GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL: "mask-user-input",
      GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS: "100",
      GUANCE_RUM_NATIVE_DEBUG: "1",
      GUANCE_RUM_NATIVE_OWNED_PIPE_NAME: pipeName,
    },
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { stdout += chunk; });
  child.stderr.on("data", (chunk) => { stderr += chunk; });

  const ipcMain = new EventEmitter();
  const bridge = await connectMixedMode({ ipcMain, pipeName });
  const webContents = { mainFrame: {}, isDestroyed: () => false };
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
        tags: { view_name: "electron.mixed.native-smoke" },
        fields: { view_loading_time: 1 },
      },
    }),
  );
  ipcMain.emit(
    "guance:electron-rum:browser-event:v1",
    { sender: webContents, senderFrame: webContents.mainFrame },
    JSON.stringify({
      name: "session_replay",
      view: { id: "electron-mixed-replay" },
      data: { type: 2, timestamp: Date.now(), data: { source: 0, nodes: [] } },
    }),
  );
  await bridge.stop();
  child.stdin.end();
  const [code, signal] = await once(child, "exit");
  assert.equal(signal, null, stderr);
  assert.equal(code, 0, stderr);
  assert.doesNotMatch(stderr, /rejected invalid bridge input/);
  assert.match(stdout, /rum_events_enqueued=1/);
  console.log("PASS Mixed Mode native path: public adapter -> existing SDK Handle.");
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
