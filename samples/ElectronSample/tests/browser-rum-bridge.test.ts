import { createRequire } from "node:module";
import { EventEmitter } from "node:events";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const {
  browserRumEventToLine,
  parseBridgePayload,
} = require("../src/main/browser-rum-line-protocol.cjs");
const {
  NativeRumHost,
  createNativeEnvironment,
} = require("../src/main/native-rum-host.cjs");
const { monitorElectronWindow } = require("../src/main/electron-process-monitor.cjs");
const preloadSource = readFileSync(
  new URL("../src/main/preload.cjs", import.meta.url),
  "utf8",
);

function executePreload(additionalArguments: string[] = []) {
  const exposed: Record<string, Record<string, (...args: unknown[]) => unknown>> = {};
  const send = vi.fn();
  const invoke = vi.fn();

  runInNewContext(preloadSource, {
    Buffer,
    process: { argv: ["electron", "sample", ...additionalArguments] },
    require: (moduleName: string) => {
      if (moduleName !== "electron") {
        throw new Error(`Unexpected preload module: ${moduleName}`);
      }

      return {
        contextBridge: {
          exposeInMainWorld: (name: string, api: Record<string, (...args: unknown[]) => unknown>) => {
            exposed[name] = api;
          },
        },
        ipcRenderer: { invoke, send },
      };
    },
  });

  return { exposed, invoke, send };
}

function rumEvent(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    name: "rum",
    data: {
      measurement: "view",
      tags: {
        app_id: "00000000-aaaa-0000-aaaa-000000000000",
        view_id: "browser-view",
        view_name: "Control, Room",
      },
      fields: {
        is_active: false,
        time_spent: 42,
        message: "line one\nline two",
      },
      time: 1_722_300_000_000,
      ...overrides,
    },
  });
}

describe("Browser RUM WebView-compatible bridge", () => {
  it("executes the production preload and forwards the official bridge contract over IPC", () => {
    const { exposed, send } = executePreload();
    const bridge = exposed.FTWebViewJavascriptBridge;
    const payload = rumEvent();

    expect(exposed.guanceDesktop).toBeDefined();
    expect(bridge.getCapabilities()).toBe("[]");
    expect(bridge.getPrivacyLevel()).toBe("mask");
    expect(bridge.getAllowedWebViewHosts()).toBeNull();
    bridge.sendEvent(payload);

    expect(send).toHaveBeenCalledWith("rum:browser-event", payload);
  });

  it("exposes only the RUM bridge to the remote workspace", () => {
    const { exposed } = executePreload(["--guance-rum-only-preload"]);

    expect(exposed.FTWebViewJavascriptBridge).toBeDefined();
    expect(exposed.guanceDesktop).toBeUndefined();
  });

  it("converts the SDK record to one line and applies trusted Windows context", () => {
    const result = browserRumEventToLine(rumEvent(), {
      tags: {
        app_id: "win_sample",
        service: "windows-sample",
        env: "local",
        sdk_name: "df_windows_rum_sdk",
        session_id: "native-session",
        is_electron: "true",
      },
      fields: {
        session_has_replay: false,
        session_sample_rate: 1,
      },
    });

    expect(result.measurement).toBe("view");
    expect(result.line).toContain("app_id=win_sample");
    expect(result.line).not.toContain("00000000-aaaa");
    expect(result.line).toContain("sdk_name=df_windows_rum_sdk");
    expect(result.line).toContain("view_name=Control\\,\\ Room");
    expect(result.line).toContain('message="line one\\nline two"');
    expect(result.line).toContain("session_sample_rate=1i");
    expect(result.line.endsWith(" 1722300000000000000\n")).toBe(true);
    expect(result.line.slice(0, -1)).not.toContain("\n");
  });

  it("rejects replay, unsupported measurements, and malformed payloads", () => {
    expect(() => parseBridgePayload('{"name":"session_replay","data":{}}')).toThrow(
      "Only Phase 1 RUM",
    );
    expect(() => parseBridgePayload(rumEvent({ measurement: "log" }))).toThrow(
      "not supported",
    );
    expect(() => parseBridgePayload("<!doctype html>")).toThrow("not valid JSON");
  });

  it.each(["view", "action", "resource", "error", "long_task"])(
    "accepts the Phase 1 %s measurement across the JS boundary",
    (measurement) => {
      expect(parseBridgePayload(rumEvent({ measurement })).measurement).toBe(measurement);
    },
  );
});

describe("Electron native host adapter", () => {
  it("reports renderer termination and one recovered unresponsive incident", () => {
    const window = new EventEmitter() as EventEmitter & { webContents: EventEmitter };
    window.webContents = new EventEmitter();
    const sendProcessFailure = vi.fn();
    let now = 1_000;

    monitorElectronWindow(window, {
      label: "main-renderer",
      sendProcessFailure,
      now: () => now,
    });

    window.emit("unresponsive");
    now = 1_750;
    window.emit("unresponsive");
    window.emit("responsive");
    window.emit("responsive");
    window.webContents.emit("render-process-gone", {}, { reason: "crashed", exitCode: 11 });
    window.emit("responsive");

    expect(sendProcessFailure).toHaveBeenCalledTimes(2);
    expect(sendProcessFailure.mock.calls[0][0]).toEqual({
      type: "ElectronRendererUnresponsive",
      message: "main-renderer was unresponsive for 750 ms",
    });
    expect(sendProcessFailure.mock.calls[1][0]).toEqual({
      type: "ElectronRendererProcessGone",
      message: "main-renderer process ended: reason=crashed exit_code=11",
    });
  });

  it("passes configuration through a private child-process environment", () => {
    const environment = createNativeEnvironment(
      {
        datakitUrl: "http://private.example.test:9529",
        applicationId: "win_sample",
        service: "windows-sample",
        env: "local",
        version: "0.1.0",
        cachePath: "C:\\temp\\rum.db",
        sampleRate: 1,
        debug: true,
      },
      { PATH: "test-path" },
    );

    expect(environment.GUANCE_RUM_NATIVE_DATAKIT_URL).toBe("http://private.example.test:9529");
    expect(environment.GUANCE_RUM_NATIVE_APP_ID).toBe("win_sample");
    expect(environment.GUANCE_RUM_NATIVE_DEBUG).toBe("1");
    expect(environment.PATH).toBe("test-path");
  });

  it("writes validated line protocol to the native host", () => {
    const write = vi.fn();
    const child = {
      stdin: { writable: true, write, on: vi.fn(), end: vi.fn() },
      stdout: { setEncoding: vi.fn(), on: vi.fn() },
      stderr: { setEncoding: vi.fn(), on: vi.fn() },
      once: vi.fn(),
    };
    const nativeHost = new NativeRumHost({
      paths: {
        directory: "C:\\native",
        executablePath: "C:\\native\\guance_rum_electron_bridge.exe",
        libraryPath: "C:\\native\\guance_rum_native.dll",
      },
      configuration: {
        datakitUrl: "http://127.0.0.1:9529",
        applicationId: "win_sample",
        debug: true,
      },
      trustedContext: {
        tags: { app_id: "win_sample", sdk_name: "df_windows_rum_sdk" },
        fields: { session_has_replay: false },
      },
      spawnProcess: vi.fn(() => child),
      fileExists: () => true,
      logger: { log: vi.fn(), error: vi.fn() },
    });

    nativeHost.start();
    expect(nativeHost.send(rumEvent(), "main-renderer")).toBe(true);
    expect(nativeHost.sendLaunch({
      type: "cold",
      startTimeNanoseconds: 100n,
      durationNanoseconds: 60n,
      preApplicationDurationNanoseconds: 20n,
      applicationDurationNanoseconds: 20n,
      firstFrameDurationNanoseconds: 20n,
    })).toBe(true);
    expect(nativeHost.sendProcessFailure({
      type: "ElectronRendererProcessGone",
      message: "renderer crashed\t(exit 11)",
    })).toBe(true);
    expect(nativeHost.sendProcessFailure({
      type: "NotTrusted",
      message: "must be rejected",
    })).toBe(false);
    expect(write).toHaveBeenCalledTimes(3);
    expect(write.mock.calls[0][0]).toContain("app_id=win_sample");
    expect(write.mock.calls[0][1]).toBe("utf8");
    expect(write.mock.calls[1]).toEqual([
      "@guance-launch\ttype=cold\tstart_time_ns=100\tduration_ns=60\t" +
        "pre_application_duration_ns=20\tapplication_duration_ns=20\t" +
        "first_frame_duration_ns=20\n",
      "utf8",
    ]);
    expect(write.mock.calls[2]).toEqual([
      "@guance-error\ttype=ElectronRendererProcessGone\t" +
        "message=renderer%20crashed%09(exit%2011)\n",
      "utf8",
    ]);
  });
});
