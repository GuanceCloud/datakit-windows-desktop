import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { packager } from "@electron/packager";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const sampleRoot = path.resolve(scriptDirectory, "..");
const packageMetadata = JSON.parse(
  fs.readFileSync(path.join(sampleRoot, "package.json"), "utf8"),
);
const applicationName = packageMetadata.config.windowsArtifactName;
const nativeSourceDirectory = path.resolve(
  sampleRoot,
  "..",
  "..",
  "src",
  "Guance.Rum.NativeCore",
  "bin",
  "win-x64",
);
const nativeFiles = [
  "guance_rum_native.dll",
  "guance_rum_electron_bridge.exe",
];
const expectedApplicationPath = path.join(
  sampleRoot,
  "release",
  `${applicationName}-win32-x64`,
);
const localSettingsPath = path.join(expectedApplicationPath, "rum.local.json");
const preservedLocalSettings = fs.existsSync(localSettingsPath)
  ? fs.readFileSync(localSettingsPath)
  : undefined;
const settingsExamplePath = path.resolve(
  sampleRoot,
  "..",
  "rum.local.json.example",
);

for (const nativeFile of nativeFiles) {
  const sourcePath = path.join(nativeSourceDirectory, nativeFile);
  if (!fs.existsSync(sourcePath)) {
    throw new Error(`Missing native RUM bridge artifact: ${sourcePath}`);
  }
}

const applicationPaths = await packager({
  dir: sampleRoot,
  out: path.join(sampleRoot, "release"),
  name: applicationName,
  executableName: applicationName,
  platform: "win32",
  arch: "x64",
  asar: true,
  overwrite: true,
  prune: true,
  appVersion: packageMetadata.version,
  buildVersion: packageMetadata.version,
  win32metadata: {
    CompanyName: "Guance",
    FileDescription: "Guance Windows Electron RUM acceptance application",
    ProductName: packageMetadata.productName,
    InternalName: applicationName,
    OriginalFilename: `${applicationName}.exe`,
    "requested-execution-level": "asInvoker",
  },
  ignore: [
    /[\\/]release([\\/]|$)/,
    /[\\/]tests([\\/]|$)/,
    /[\\/]scripts([\\/]|$)/,
    /[\\/]src[\\/]renderer([\\/]|$)/,
    /[\\/]tsconfig\.json$/,
    /[\\/]vite\.config\.ts$/,
  ],
});

for (const applicationPath of applicationPaths) {
  const nativeTargetDirectory = path.join(applicationPath, "resources", "native");
  fs.mkdirSync(nativeTargetDirectory, { recursive: true });
  for (const nativeFile of nativeFiles) {
    fs.copyFileSync(
      path.join(nativeSourceDirectory, nativeFile),
      path.join(nativeTargetDirectory, nativeFile),
    );
  }
  fs.copyFileSync(
    settingsExamplePath,
    path.join(applicationPath, "rum.local.json.example"),
  );
  if (preservedLocalSettings) {
    fs.writeFileSync(path.join(applicationPath, "rum.local.json"), preservedLocalSettings);
  }
  console.log(`[electron-package] ${applicationPath} (Electron + C++ RUM bridge)`);
}
