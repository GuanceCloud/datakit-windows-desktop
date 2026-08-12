"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const sampleRoot = path.resolve(__dirname, "..");

test("Full Mode consumes only installed public adapter entries and the Bridge EXE feature", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(sampleRoot, "vcpkg.json"), "utf8"));
  const configuration = JSON.parse(
    fs.readFileSync(path.join(sampleRoot, "vcpkg-configuration.json"), "utf8"),
  );
  const nativeDependency = manifest.dependencies.find(
    (dependency) => dependency.name === "guance-windows-native",
  );

  assert.equal(manifest["version-string"], "0");
  assert.equal(nativeDependency["version>="], undefined);
  assert.equal(configuration.registries, undefined);
  assert.deepEqual(nativeDependency.features, ["electron-bridge"]);
  assert.equal(fs.existsSync(path.join(sampleRoot, "native-owned-bridge.cjs")), false);
  assert.equal(fs.existsSync(path.join(sampleRoot, "rum-line-protocol.cjs")), false);
  assert.equal(fs.existsSync(path.join(sampleRoot, "preload.cjs")), false);
  assert.equal(fs.existsSync(path.join(sampleRoot, "native-host")), false);
  const mainProcess = fs.readFileSync(path.join(sampleRoot, "main.cjs"), "utf8");
  assert.match(mainProcess, /electron["',\s\)]*[\s\S]*main["',\s\)]*[\s\S]*index\.cjs/);
  assert.match(mainProcess, /preload["',\s\)]*[\s\S]*standalone\.cjs/);
  const installScript = fs.readFileSync(
    path.join(sampleRoot, "scripts", "install-native.ps1"),
    "utf8",
  );
  assert.match(installScript, /prepare-vcpkg-local-overlay-port\.ps1/);
  assert.doesNotMatch(installScript, /ReleaseTag|0\.1\.0-alpha/);
});
