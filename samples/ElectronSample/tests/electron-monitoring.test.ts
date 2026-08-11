import { EventEmitter } from "node:events";
import { createRequire } from "node:module";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const {
  COLD_LAUNCH_VIEW_WAIT_MS,
  connectElectronMonitoring,
  initializeElectronMonitoring,
} = require("../src/main/electron-monitoring.cjs");

function nativePolicy() {
  return {
    rum: {
      enabled: true,
      sessionReplay: { enabled: true, privacyLevel: "mask" },
    },
    log: { enabled: true },
    trace: {
      enabled: true,
      sampleRate: 75,
      type: "w3c_traceparent",
      allowedUrls: ["https://api.example.test"],
    },
  };
}

function webContents() {
  const contents = new EventEmitter() as EventEmitter & { mainFrame: object };
  contents.mainFrame = {};
  return contents;
}

describe("Electron monitoring ownership modes", () => {
  it("initializes and owns the native host in full Electron mode", async () => {
    const nativeBridge = {
      start: vi.fn(),
      send: vi.fn(() => true),
      shutdown: vi.fn(() => Promise.resolve()),
    };
    const createNativeBridge = vi.fn(() => nativeBridge);
    const integration = initializeElectronMonitoring({
      configuration: nativePolicy(),
      electron: { defaultPage: { enabled: true } },
      createNativeBridge,
    });

    expect(integration.mode).toBe("electron-owned");
    expect(createNativeBridge).toHaveBeenCalledWith(nativePolicy());
    expect(nativeBridge.start).toHaveBeenCalledOnce();

    await integration.shutdown();
    expect(nativeBridge.shutdown).toHaveBeenCalledOnce();
  });

  it("connects to but does not shut down a C++ owned SDK", async () => {
    const nativeBridge = {
      send: vi.fn(() => true),
      disconnect: vi.fn(() => Promise.resolve()),
      shutdown: vi.fn(),
    };
    const integration = connectElectronMonitoring({
      nativeBridge,
      nativePolicy: nativePolicy(),
      electron: {
        defaultPage: { enabled: false },
        pages: {
          workspace: {
            enabled: true,
            log: { enabled: false },
            rum: { sessionReplay: { enabled: true, privacyLevel: "mask-user-input" } },
          },
        },
      },
    });

    expect(integration.mode).toBe("native-owned");
    expect(integration.pagePolicy("settings").enabled).toBe(false);
    expect(integration.pagePolicy("workspace")).toMatchObject({
      enabled: true,
      rum: {
        enabled: true,
        sessionReplay: { enabled: true, privacyLevel: "mask-user-input" },
      },
      log: { enabled: false },
      trace: { enabled: true, sampleRate: 75 },
    });
    expect(integration.createPreloadArguments("settings")).toContain(
      "--guance-monitoring-disabled",
    );
    expect(integration.createPreloadArguments("workspace")).toContain(
      "--guance-rum-replay=mask-user-input",
    );
    const page = integration.createPageIntegration("workspace", {
      preload: "C:\\app\\guance-preload.cjs",
      rendererLabel: "workspace-renderer",
    });
    expect(page.webPreferences).toMatchObject({
      preload: "C:\\app\\guance-preload.cjs",
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    });

    await integration.shutdown();
    expect(nativeBridge.disconnect).toHaveBeenCalledOnce();
    expect(nativeBridge.shutdown).not.toHaveBeenCalled();
  });

  it("registers only enabled pages and enforces their feature policy", () => {
    const nativeBridge = { send: vi.fn(() => true) };
    const integration = connectElectronMonitoring({
      nativeBridge,
      nativePolicy: nativePolicy(),
      electron: {
        defaultPage: { enabled: false },
        pages: {
          workspace: { enabled: true, log: { enabled: false } },
        },
      },
    });
    const workspace = webContents();
    const settings = webContents();
    integration.registerWebContents(workspace, "workspace", "workspace-renderer");
    integration.registerWebContents(settings, "settings", "settings-renderer");

    const workspaceEvent = { sender: workspace, senderFrame: workspace.mainFrame };
    const settingsEvent = { sender: settings, senderFrame: settings.mainFrame };
    expect(integration.send(workspaceEvent, JSON.stringify({ name: "rum" }))).toBe(true);
    expect(integration.send(workspaceEvent, JSON.stringify({ name: "log" }))).toBe(false);
    expect(integration.send(settingsEvent, JSON.stringify({ name: "rum" }))).toBe(false);
    expect(integration.send(
      { sender: workspace, senderFrame: {} },
      JSON.stringify({ name: "rum" }),
    )).toBe(false);
    expect(nativeBridge.send).toHaveBeenCalledOnce();
    expect(nativeBridge.send).toHaveBeenCalledWith(
      JSON.stringify({ name: "rum" }),
      "workspace-renderer",
    );
  });

  it("lets an all-pages default be narrowed by a page override", () => {
    const integration = connectElectronMonitoring({
      nativeBridge: { send: vi.fn(() => true) },
      nativePolicy: nativePolicy(),
      electron: {
        defaultPage: { enabled: true },
        pages: { diagnostics: { trace: { enabled: false } } },
      },
    });

    expect(integration.pagePolicy("main").trace.enabled).toBe(true);
    expect(integration.pagePolicy("diagnostics").trace.enabled).toBe(false);
  });

  it("associates launches with the accepted main Renderer View", () => {
    const nativeBridge = {
      send: vi.fn(() => true),
      sendLaunch: vi.fn(() => true),
    };
    const integration = connectElectronMonitoring({
      nativeBridge,
      nativePolicy: nativePolicy(),
      electron: { defaultPage: { enabled: true } },
    });
    const main = webContents();
    integration.registerWebContents(main, "main", "main-renderer");
    const view = JSON.stringify({
      name: "rum",
      data: {
        measurement: "view",
        tags: {
          view_id: "main-view-id",
          view_name: "Control Room",
          view_referrer: "file:///splash.html",
        },
        fields: { is_active: true },
        time: 1_722_300_000_000,
      },
    });

    expect(integration.sendLaunch({
      type: "cold",
      startTimeNanoseconds: 100n,
      durationNanoseconds: 60n,
    })).toBe(true);
    expect(nativeBridge.sendLaunch).not.toHaveBeenCalled();
    expect(integration.send(
      { sender: main, senderFrame: main.mainFrame },
      view,
    )).toBe(true);
    expect(nativeBridge.sendLaunch).toHaveBeenCalledWith({
      type: "cold",
      startTimeNanoseconds: 100n,
      durationNanoseconds: 60n,
      view: {
        id: "main-view-id",
        name: "Control Room",
        referrer: "file:///splash.html",
      },
    });
  });

  it("falls back to an unassociated cold launch when the main View does not arrive", () => {
    vi.useFakeTimers();
    try {
      const nativeBridge = {
        send: vi.fn(() => true),
        sendLaunch: vi.fn(() => true),
      };
      const integration = connectElectronMonitoring({
        nativeBridge,
        nativePolicy: nativePolicy(),
        electron: { defaultPage: { enabled: true } },
      });
      integration.registerWebContents(webContents(), "main", "main-renderer");

      expect(integration.sendLaunch({
        type: "cold",
        startTimeNanoseconds: 100n,
        durationNanoseconds: 60n,
      })).toBe(true);
      vi.advanceTimersByTime(COLD_LAUNCH_VIEW_WAIT_MS);

      expect(nativeBridge.sendLaunch).toHaveBeenCalledWith({
        type: "cold",
        startTimeNanoseconds: 100n,
        durationNanoseconds: 60n,
        view: undefined,
      });
    } finally {
      vi.useRealTimers();
    }
  });
});
