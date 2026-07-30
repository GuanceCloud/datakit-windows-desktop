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
const settingsExamplePath = path.resolve(
  sampleRoot,
  "..",
  "rum.local.json.example",
);

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
  fs.copyFileSync(
    settingsExamplePath,
    path.join(applicationPath, "rum.local.json.example"),
  );
  console.log(`[electron-package] ${applicationPath}`);
}
