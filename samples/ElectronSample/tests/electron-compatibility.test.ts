import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import viteConfig from "../vite.config";

const sampleRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const packageMetadata = JSON.parse(
  fs.readFileSync(path.join(sampleRoot, "package.json"), "utf8"),
) as {
  scripts: Record<string, string>;
  devDependencies: Record<string, string>;
  engines: Record<string, string>;
};

describe("Electron compatibility baseline", () => {
  it("pins the final Electron 22 runtime and a compatible packager", () => {
    expect(packageMetadata.devDependencies.electron).toBe("22.3.27");
    expect(packageMetadata.devDependencies["@electron/packager"]).toBe(
      "20.0.4",
    );
    expect(packageMetadata.engines.node).toBe(">=22.12");
    expect(packageMetadata.scripts["electron:install"]).toBe(
      "node node_modules/electron/install.js",
    );
  });

  it("transpiles the renderer for Electron 22 Chromium 108", () => {
    expect(viteConfig.build?.target).toBe("chrome108");
  });
});
