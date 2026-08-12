"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { once } = require("node:events");
const { spawn } = require("node:child_process");

const sampleRoot = path.resolve(__dirname, "..");
const electronPath = path.join(sampleRoot, "node_modules", "electron", "dist", "electron.exe");
const bridgePath = path.join(
  sampleRoot,
  "vcpkg_installed",
  "x64-windows",
  "tools",
  "guance-windows-native",
  "guance_windows_electron_bridge.exe",
);

async function main() {
  assert.ok(fs.existsSync(electronPath), `Missing Electron runtime: ${electronPath}`);
  assert.ok(fs.existsSync(bridgePath), `Missing full-mode Bridge EXE: ${bridgePath}`);
  const profilePath = path.join(
    sampleRoot,
    "vcpkg_installed",
    `verify-electron-profile-${process.pid}`,
  );
  const child = spawn(electronPath, [
    "--disable-gpu",
    `--user-data-dir=${profilePath}`,
    sampleRoot,
    "--guance-smoke",
  ], {
    cwd: sampleRoot,
    env: {
      ...process.env,
      GUANCE_RUM_APP_ID: "electron-full-electron-smoke",
      GUANCE_RUM_DATAKIT_URL: "http://127.0.0.1:9",
      GUANCE_RUM_SERVICE: "electron-full-electron-smoke",
      GUANCE_RUM_ENV: "test",
    },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { stdout += chunk; });
  child.stderr.on("data", (chunk) => { stderr += chunk; });
  try {
    const [code, signal] = await once(child, "exit");
    assert.equal(signal, null, stderr);
    assert.equal(code, 0, `${stdout}\n${stderr}`);
    assert.match(stdout, /renderer smoke passed path=full-mode/);
    assert.match(stdout, /\benqueued=[1-9][0-9]*\b/);
    console.log("PASS full Electron path: Renderer -> Preload -> Main -> Bridge EXE.");
  } finally {
    if (child.exitCode === null) child.kill();
    fs.rmSync(profilePath, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
