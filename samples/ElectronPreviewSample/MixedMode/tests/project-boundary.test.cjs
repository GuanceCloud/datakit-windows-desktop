"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const sampleRoot = path.resolve(__dirname, "..");

test("Mixed Mode installs the adapter without a Bridge EXE and owns one SDK Handle", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(sampleRoot, "vcpkg.json"), "utf8"));
  const nativeDependency = manifest.dependencies.find(
    (dependency) => dependency.name === "guance-windows-native",
  );
  const mainProcess = fs.readFileSync(path.join(sampleRoot, "main.cjs"), "utf8");

  assert.deepEqual(nativeDependency.features, ["electron-adapter"]);
  assert.equal(fs.existsSync(path.join(sampleRoot, "native-owned-bridge.cjs")), false);
  assert.equal(fs.existsSync(path.join(sampleRoot, "rum-line-protocol.cjs")), false);
  assert.equal(fs.existsSync(path.join(sampleRoot, "native-host", "main.cpp")), true);
  assert.doesNotMatch(mainProcess, /guance_windows_electron_bridge/);
  assert.match(mainProcess, /electron["',\s\)]*[\s\S]*main["',\s\)]*[\s\S]*index\.cjs/);
  const preloadSource = fs.readFileSync(path.join(sampleRoot, "preload-source.cjs"), "utf8");
  assert.match(preloadSource, /preload\/install\.cjs/);
  const nativeHost = fs.readFileSync(path.join(sampleRoot, "native-host", "main.cpp"), "utf8");
  assert.match(nativeHost, /guance_electron_bridge_server_start\(sdk, &options\)/);
  assert.match(nativeHost, /guance_electron_bridge_server_stop\(bridge\)[\s\S]*guance_sdk_shutdown\(sdk\)/);
});
