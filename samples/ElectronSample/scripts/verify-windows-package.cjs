"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const sampleRoot = path.resolve(__dirname, "..");
const packageMetadata = require(path.join(sampleRoot, "package.json"));
const applicationName = packageMetadata.config.windowsArtifactName;
const packageDirectory = path.join(
  sampleRoot,
  "release",
  `${applicationName}-win32-x64`,
);
const executablePath = path.join(packageDirectory, `${applicationName}.exe`);
const appArchivePath = path.join(packageDirectory, "resources", "app.asar");
const settingsExamplePath = path.join(packageDirectory, "rum.local.json.example");
const electronVersionPath = path.join(packageDirectory, "version");
const skipRuntime = process.argv.includes("--skip-runtime");

function fail(message) {
  console.error(`[electron-package-verify] ${message}`);
  process.exit(1);
}

if (!fs.existsSync(executablePath)) {
  fail(`missing executable: ${executablePath}`);
}

if (!fs.existsSync(appArchivePath)) {
  fail(`missing application archive: ${appArchivePath}`);
}

if (!fs.existsSync(settingsExamplePath)) {
  fail(`missing local settings example: ${settingsExamplePath}`);
}

if (!fs.existsSync(electronVersionPath)) {
  fail(`missing Electron version marker: ${electronVersionPath}`);
}

const expectedElectronVersion = packageMetadata.devDependencies.electron;
const packagedElectronVersion = fs.readFileSync(electronVersionPath, "utf8").trim();
if (expectedElectronVersion !== "22.3.27") {
  fail(`Electron compatibility baseline must be 22.3.27, got ${expectedElectronVersion}`);
}
if (packagedElectronVersion !== expectedElectronVersion) {
  fail(
    `packaged Electron version ${packagedElectronVersion} does not match ${expectedElectronVersion}`,
  );
}

if (skipRuntime) {
  console.log(
    `[electron-package-verify] files passed with Electron ${packagedElectronVersion}, runtime skipped: ${path.relative(sampleRoot, executablePath)}`,
  );
  process.exit(0);
}

const smoke = spawnSync(executablePath, [], {
  cwd: packageDirectory,
  encoding: "utf8",
  env: {
    ...process.env,
    ELECTRON_SMOKE: "1",
  },
  timeout: 30_000,
  windowsHide: true,
});

if (smoke.error) {
  fail(`failed to launch packaged application: ${smoke.error.message}`);
}

const output = `${smoke.stdout || ""}\n${smoke.stderr || ""}`;
if (smoke.status !== 0) {
  fail(`packaged smoke exited with ${smoke.status}\n${output}`);
}

for (const marker of [
  "[electron-smoke]",
  '"rumInitialized":true',
  '"resourceStatus":"HTTP 503"',
  '"userCorrelation":true',
]) {
  if (!output.includes(marker)) {
    fail(`packaged smoke did not emit ${marker}\n${output}`);
  }
}

console.log(
  `[electron-package-verify] passed: ${path.relative(sampleRoot, executablePath)}`,
);
