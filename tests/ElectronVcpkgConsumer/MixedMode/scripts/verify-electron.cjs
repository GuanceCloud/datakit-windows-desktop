"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { once } = require("node:events");
const { spawn } = require("node:child_process");

const sampleRoot = path.resolve(__dirname, "..");
const electronPath = path.join(sampleRoot, "node_modules", "electron", "dist", "electron.exe");
const hostPath = path.join(
  sampleRoot,
  "native-host",
  "build",
  "Release",
  "guance_windows_electron_mixed_host.exe",
);

function observe(child) {
  const output = { stdout: "", stderr: "" };
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { output.stdout += chunk; });
  child.stderr.on("data", (chunk) => { output.stderr += chunk; });
  return output;
}

async function successfulExit(child, output) {
  const [code, signal] = child.exitCode === null
    ? await once(child, "exit")
    : [child.exitCode, child.signalCode];
  assert.equal(signal, null, output.stderr);
  assert.equal(code, 0, `${output.stdout}\n${output.stderr}`);
}

async function main() {
  assert.ok(fs.existsSync(electronPath), `Missing Electron runtime: ${electronPath}`);
  assert.ok(fs.existsSync(hostPath), `Missing mixed-mode C++ host: ${hostPath}`);
  const pipeName = `guance-electron-mixed-renderer-${process.pid}`;
  const profilePath = path.join(
    sampleRoot,
    "vcpkg_installed",
    `verify-electron-profile-${process.pid}`,
  );
  const host = spawn(hostPath, [], {
    cwd: path.dirname(hostPath),
    env: {
      ...process.env,
      GUANCE_RUM_NATIVE_DATAKIT_URL: "http://127.0.0.1:9",
      GUANCE_RUM_NATIVE_APP_ID: "electron-mixed-electron-smoke",
      GUANCE_RUM_NATIVE_SERVICE: "electron-mixed-electron-smoke",
      GUANCE_RUM_NATIVE_ENV: "test",
      GUANCE_RUM_NATIVE_VERSION: "0.1.0",
      GUANCE_RUM_NATIVE_CACHE_PATH: path.join(
        sampleRoot,
        "vcpkg_installed",
        "verify-electron-cache",
      ),
      GUANCE_RUM_NATIVE_SAMPLE_RATE: "1",
      GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED: "1",
      GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE: "1",
      GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL: "mask-user-input",
      GUANCE_RUM_NATIVE_DEBUG: "1",
      GUANCE_RUM_NATIVE_OWNED_PIPE_NAME: pipeName,
    },
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  const hostOutput = observe(host);
  const electron = spawn(electronPath, [
    "--disable-gpu",
    `--user-data-dir=${profilePath}`,
    sampleRoot,
    "--guance-smoke",
  ], {
    cwd: sampleRoot,
    env: {
      ...process.env,
      GUANCE_RUM_NATIVE_OWNED_PIPE_NAME: pipeName,
    },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });
  const electronOutput = observe(electron);
  let electronFailure;
  try {
    await successfulExit(electron, electronOutput);
  } catch (error) {
    electronFailure = error;
  } finally {
    if (electron.exitCode === null) electron.kill();
    if (host.stdin.writable) host.stdin.end();
  }
  try {
    await successfulExit(host, hostOutput);
    if (electronFailure) throw electronFailure;
    assert.match(electronOutput.stdout, /renderer smoke passed path=mixed-mode/);
    assert.doesNotMatch(hostOutput.stderr, /rejected invalid bridge input/);
    assert.match(hostOutput.stdout, /rum_events_enqueued=[1-9][0-9]*/);
    console.log("PASS mixed Electron path: Renderer -> Preload -> Main -> named pipe -> C++ SDK owner.");
  } finally {
    if (host.exitCode === null) host.kill();
    fs.rmSync(profilePath, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
