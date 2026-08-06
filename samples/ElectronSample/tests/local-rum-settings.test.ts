import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";

import { afterEach, describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const temporaryDirectories: string[] = [];

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

describe("loadLocalRumSettings", () => {
  it("finds rum.local.json in an ancestor of the Electron sample directory", () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "guance-electron-config-"));
    temporaryDirectories.push(root);
    const sampleDirectory = path.join(root, "samples", "ElectronSample");
    fs.mkdirSync(sampleDirectory, { recursive: true });
    fs.writeFileSync(
      path.join(root, "samples", "rum.local.json"),
      JSON.stringify({
        rumAppId: "json-electron-app",
        serviceName: "json-electron-service",
        webViewUrl: "https://example.test/webview",
      }),
    );

    const { loadLocalRumSettings } = require("../src/main/local-rum-settings.cjs");
    const result = loadLocalRumSettings({
      currentDirectory: sampleDirectory,
      appPath: sampleDirectory,
      executablePath: path.join(root, "electron.exe"),
      isPackaged: false,
    });

    expect(result.settings).toMatchObject({
      rumAppId: "json-electron-app",
      serviceName: "json-electron-service",
      webViewUrl: "https://example.test/webview",
    });
    expect(result.filePath).toBe(path.join(root, "samples", "rum.local.json"));
  });

  it("applies environment values over JSON values and defaults", () => {
    const { createLocalRumSettingsReader } = require("../src/main/local-rum-settings.cjs");
    const reader = createLocalRumSettingsReader({
      environment: {
        GUANCE_RUM_APP_ID: "environment-app",
        GUANCE_RUM_DEBUG: "TRUE",
      },
      settings: {
        rumAppId: "json-app",
        serviceName: "json-service",
        debug: false,
        sessionSampleRate: 42,
      },
    });

    expect(reader.readString("GUANCE_RUM_APP_ID", "rumAppId", "default-app")).toBe(
      "environment-app",
    );
    expect(reader.readString("GUANCE_RUM_SERVICE_NAME", "serviceName", "default-service")).toBe(
      "json-service",
    );
    expect(reader.readBoolean("GUANCE_RUM_DEBUG", "debug", false)).toBe(true);
    expect(
      reader.readNumber("GUANCE_RUM_SAMPLE_RATE", "sessionSampleRate", 100),
    ).toBe(42);
  });

  it("falls back for empty numeric settings", () => {
    const { createLocalRumSettingsReader } = require("../src/main/local-rum-settings.cjs");
    const reader = createLocalRumSettingsReader({
      environment: {},
      settings: {
        sessionSampleRate: null,
      },
    });

    expect(
      reader.readNumber("GUANCE_RUM_SAMPLE_RATE", "sessionSampleRate", 100),
    ).toBe(100);
  });

  it("reads tracing URL arrays from JSON or a comma-separated environment value", () => {
    const {
      createLocalRumSettingsReader,
      resolveAllowedTraceUrls,
    } = require("../src/main/local-rum-settings.cjs");
    const jsonReader = createLocalRumSettingsReader({
      environment: {},
      settings: { allowedTracingUrls: ["https://json.example.test"] },
    });
    const environmentReader = createLocalRumSettingsReader({
      environment: {
        GUANCE_TRACE_ALLOWED_URLS:
          "https://one.example.test, https://two.example.test",
        GUANCE_RUM_ALLOWED_TRACING_URLS: "https://legacy.example.test",
      },
      settings: {},
    });
    const legacyEnvironmentReader = createLocalRumSettingsReader({
      environment: {
        GUANCE_RUM_ALLOWED_TRACING_URLS: "https://legacy.example.test",
      },
      settings: {},
    });

    expect(resolveAllowedTraceUrls(jsonReader)).toEqual(["https://json.example.test"]);
    expect(resolveAllowedTraceUrls(environmentReader)).toEqual([
      "https://one.example.test",
      "https://two.example.test",
    ]);
    expect(resolveAllowedTraceUrls(legacyEnvironmentReader)).toEqual([
      "https://legacy.example.test",
    ]);
  });

  it("lets an intake environment variable override the opposite JSON target", () => {
    const {
      createLocalRumSettingsReader,
      resolveRumIngestionConfiguration,
    } = require("../src/main/local-rum-settings.cjs");
    const reader = createLocalRumSettingsReader({
      environment: {
        GUANCE_RUM_DATAKIT_URL: "http://environment-datakit.test",
      },
      settings: {
        datawayUrl: "https://json-dataway.test",
      },
    });

    expect(
      resolveRumIngestionConfiguration(reader, "http://default-datakit.test"),
    ).toEqual({
      datawayUrl: "",
      datakitUrl: "http://environment-datakit.test",
    });
  });

  it("uses the shared web view URL for Electron mixed mode and allows HTTP", () => {
    const {
      createLocalRumSettingsReader,
      isSupportedWebViewUrl,
      resolveWebViewUrl,
    } = require("../src/main/local-rum-settings.cjs");
    const reader = createLocalRumSettingsReader({
      environment: {},
      settings: {
        webViewUrl: "http://webview.example.test:8000",
      },
    });

    expect(resolveWebViewUrl(reader)).toBe("http://webview.example.test:8000");
    expect(isSupportedWebViewUrl(resolveWebViewUrl(reader))).toBe(true);
  });

  it("prefers an environment web view URL over the shared JSON setting", () => {
    const {
      createLocalRumSettingsReader,
      resolveWebViewUrl,
    } = require("../src/main/local-rum-settings.cjs");
    const reader = createLocalRumSettingsReader({
      environment: {
        GUANCE_RUM_WEBVIEW_URL: "https://environment-webview.example.test",
      },
      settings: {
        webViewUrl: "https://json-webview.example.test",
      },
    });

    expect(resolveWebViewUrl(reader)).toBe("https://environment-webview.example.test");
  });

  it("only accepts an executable-sidecar JSON file in packaged mode", () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "guance-electron-packaged-config-"));
    temporaryDirectories.push(root);
    const executableDirectory = path.join(root, "application");
    const unrelatedWorkingDirectory = path.join(root, "working");
    fs.mkdirSync(executableDirectory, { recursive: true });
    fs.mkdirSync(unrelatedWorkingDirectory, { recursive: true });
    fs.writeFileSync(
      path.join(unrelatedWorkingDirectory, "rum.local.json"),
      JSON.stringify({ rumAppId: "untrusted-working-directory-app" }),
    );

    const { loadLocalRumSettings } = require("../src/main/local-rum-settings.cjs");
    const withoutSidecar = loadLocalRumSettings({
      currentDirectory: unrelatedWorkingDirectory,
      appPath: path.join(executableDirectory, "resources", "app.asar"),
      executablePath: path.join(executableDirectory, "sample.exe"),
      isPackaged: true,
    });

    expect(withoutSidecar.settings).toEqual({});

    fs.writeFileSync(
      path.join(executableDirectory, "rum.local.json"),
      JSON.stringify({ rumAppId: "trusted-sidecar-app" }),
    );
    const withSidecar = loadLocalRumSettings({
      currentDirectory: unrelatedWorkingDirectory,
      appPath: path.join(executableDirectory, "resources", "app.asar"),
      executablePath: path.join(executableDirectory, "sample.exe"),
      isPackaged: true,
    });

    expect(withSidecar.settings.rumAppId).toBe("trusted-sidecar-app");
  });
});
