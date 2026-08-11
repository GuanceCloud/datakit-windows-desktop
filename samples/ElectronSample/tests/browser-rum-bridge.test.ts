import { createRequire } from "node:module";
import { EventEmitter } from "node:events";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const {
  browserBridgeEventToNativeInput,
  browserRumEventToLine,
  parseBridgeEvent,
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

function replayEvent(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    name: "session_replay",
    data: {
      type: 2,
      timestamp: 1_722_300_000_123,
      data: { node: { id: 1, type: 0 } },
      ...overrides,
    },
    view: { id: "browser-view" },
  });
}

function logEvent(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    name: "log",
    data: {
      message: "Renderer request failed\nwith context",
      status: "warn",
      service: "browser",
      view: { id: "browser-view", url: "file:///index.html" },
      request_id: "request-1",
      ...overrides,
    },
  });
}

function nativeConfiguration() {
  return {
    transport: {
      datawayUrl: "",
      datakitUrl: "http://datakit.example.test:9529",
      clientToken: "",
      proxyUrl: "",
      httpTimeoutMs: 10_000,
    },
    application: {
      id: "win_sample",
      service: "windows-sample",
      env: "local",
      version: "0.1.0",
    },
    runtime: { cachePath: "C:\\temp\\rum-cache", debug: true },
    cache: {
      maxBytes: 128 * 1024 * 1024,
      maxFiles: 1024,
      maxAgeSeconds: 7 * 24 * 60 * 60,
      maxBatchItems: 50,
      maxBatchBytes: 512 * 1024,
    },
    upload: {
      maxBytesPerSecond: 256 * 1024,
      burstBytes: 2 * 1024 * 1024,
      maxRequestsPerSecond: 2,
      maxBatchesPerCycle: 4,
    },
    rum: {
      enabled: true,
      sampleRate: 1,
      sessionReplay: {
        enabled: true,
        sampleRate: 0.75,
        onErrorSampleRate: 0.25,
        privacyLevel: "mask" as const,
      },
    },
    log: { enabled: true, sampleRate: 0.5 },
    trace: {
      enabled: true,
      sampleRate: 100,
      type: "w3c_traceparent" as const,
      allowedUrls: [],
    },
  };
}

describe("Browser RUM WebView-compatible bridge", () => {
  it("executes the production preload and forwards the official bridge contract over IPC", () => {
    const { exposed, invoke, send } = executePreload();
    const bridge = exposed.FTWebViewJavascriptBridge;
    const payload = rumEvent();

    expect(exposed.guanceDesktop).toBeDefined();
    expect(bridge.getCapabilities()).toBe("[]");
    expect(bridge.getPrivacyLevel()).toBe("mask");
    expect(bridge.getAllowedWebViewHosts()).toBeNull();
    bridge.sendEvent(payload);
    exposed.guanceDesktop.runNativeAcceptanceScenario();
    exposed.guanceDesktop.crashNativeBridge();

    expect(send).toHaveBeenCalledWith("rum:browser-event", payload);
    expect(invoke).toHaveBeenCalledWith("native:run-acceptance-scenario");
    expect(invoke).toHaveBeenCalledWith("native:crash-bridge");
  });

  it("exposes only the RUM bridge to the remote workspace", () => {
    const { exposed } = executePreload(["--guance-rum-only-preload"]);

    expect(exposed.FTWebViewJavascriptBridge).toBeDefined();
    expect(exposed.guanceDesktop).toBeUndefined();
  });

  it("keeps the desktop preload but omits monitoring for a disabled page", () => {
    const { exposed } = executePreload(["--guance-monitoring-disabled"]);

    expect(exposed.guanceDesktop).toBeDefined();
    expect(exposed.FTWebViewJavascriptBridge).toBeUndefined();
  });

  it("advertises experimental replay only when native configuration enables it", () => {
    const { exposed } = executePreload(["--guance-rum-replay=mask-user-input"]);
    const bridge = exposed.FTWebViewJavascriptBridge;

    expect(bridge.getCapabilities()).toBe('["records"]');
    expect(bridge.getPrivacyLevel()).toBe("mask-user-input");
  });

  it("converts the SDK record to one line and applies trusted Windows context", () => {
    const result = browserRumEventToLine(rumEvent(), {
      tags: {
        app_id: "win_sample",
        service: "windows-sample",
        env: "local",
        sdk_name: "df_windows_rum_sdk",
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

  it("converts an experimental replay record to the private native command", () => {
    const parsed = parseBridgeEvent(replayEvent());
    const result = browserBridgeEventToNativeInput(replayEvent());

    expect(parsed).toMatchObject({
      name: "session_replay",
      viewId: "browser-view",
      timestamp: 1_722_300_000_123,
      fullSnapshot: true,
    });
    expect(result.measurement).toBe("session_replay");
    expect(result.line).toContain(
      "@guance-replay\tview_id=browser-view",
    );
    expect(result.line).not.toContain("session_id=");
    expect(result.line).toContain("\tfull_snapshot=1\t");
    expect(result.line.endsWith("\n")).toBe(true);
  });

  it("converts Browser Logs to the private native log command", () => {
    const parsed = parseBridgeEvent(logEvent());
    const result = browserBridgeEventToNativeInput(logEvent());

    expect(parsed).toMatchObject({
      name: "log",
      record: { status: "warn", request_id: "request-1" },
    });
    expect(result.measurement).toBe("log");
    expect(result.line).toContain("@guance-log\tstatus=warning\tmessage=");
    expect(result.line).toContain("\tproperty-key=");
    expect(result.line.endsWith("\n")).toBe(true);
  });

  it("rejects malformed replay, unsupported measurements, and malformed payloads", () => {
    expect(() => parseBridgeEvent(replayEvent({ timestamp: 0 }))).toThrow(
      "positive millisecond",
    );
    expect(() => parseBridgePayload(rumEvent({ measurement: "log" }))).toThrow(
      "not supported",
    );
    expect(() => parseBridgePayload("<!doctype html>")).toThrow("not valid JSON");
  });

  it.each(["view", "action", "resource", "error", "long_task"])(
    "accepts the core %s measurement across the JS boundary",
    (measurement) => {
      expect(parseBridgePayload(rumEvent({ measurement })).measurement).toBe(measurement);
    },
  );

  it("preserves Browser trace identifiers on native RUM resources", () => {
    const event = rumEvent({
      measurement: "resource",
      tags: {
        resource_url: "https://api.example.test/orders",
        trace_id: "0123456789abcdef0123456789abcdef",
        span_id: "0123456789abcdef",
      },
    });

    const result = browserRumEventToLine(event, {
      tags: { app_id: "win_sample" },
    });

    expect(result.line).toContain("trace_id=0123456789abcdef0123456789abcdef");
    expect(result.line).toContain("span_id=0123456789abcdef");
    expect(result.line).not.toContain("session_id=");
  });
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
      nativeConfiguration(),
      { PATH: "test-path" },
    );

    expect(environment.GUANCE_RUM_NATIVE_DATAKIT_URL).toBe("http://datakit.example.test:9529");
    expect(environment.GUANCE_RUM_NATIVE_APP_ID).toBe("win_sample");
    expect(environment.GUANCE_RUM_NATIVE_LOG_ENABLED).toBe("1");
    expect(environment.GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE).toBe("0.5");
    expect(environment.GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED).toBe("1");
    expect(environment.GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE).toBe("0.75");
    expect(environment.GUANCE_RUM_NATIVE_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE).toBe("0.25");
    expect(environment.GUANCE_RUM_NATIVE_MAX_CACHE_BYTES).toBe(String(128 * 1024 * 1024));
    expect(environment.GUANCE_RUM_NATIVE_MAX_UPLOAD_BYTES_PER_SECOND).toBe(String(256 * 1024));
    expect(environment.GUANCE_RUM_NATIVE_MAX_UPLOAD_REQUESTS_PER_SECOND).toBe("2");
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
        executablePath: "C:\\native\\guance_windows_electron_bridge.exe",
        libraryPath: "C:\\native\\guance_windows_native.dll",
      },
      configuration: nativeConfiguration(),
      trustedContext: {
        tags: {
          app_id: "win_sample",
          sdk_name: "df_windows_rum_sdk",
        },
        fields: { session_has_replay: false },
      },
      spawnProcess: vi.fn(() => child),
      fileExists: () => true,
      logger: { log: vi.fn(), error: vi.fn() },
    });

    nativeHost.start();
    expect(nativeHost.send(rumEvent(), "main-renderer")).toBe(true);
    expect(nativeHost.send(logEvent(), "main-renderer")).toBe(true);
    expect(nativeHost.send(replayEvent(), "main-renderer")).toBe(true);
    expect(nativeHost.sendLaunch({
      type: "cold",
      startTimeNanoseconds: 100n,
      durationNanoseconds: 60n,
      preApplicationDurationNanoseconds: 20n,
      applicationDurationNanoseconds: 20n,
      firstFrameDurationNanoseconds: 20n,
      view: {
        id: "browser-view",
        name: "Control Room",
        referrer: "file:///splash.html",
      },
    })).toBe(true);
    expect(nativeHost.sendProcessFailure({
      type: "ElectronRendererProcessGone",
      message: "renderer crashed\t(exit 11)",
    })).toBe(true);
    expect(nativeHost.sendNativeAcceptanceScenario({
      scenarioId: "native-scenario-1",
      windowHandle: 12345n,
      width: 1440,
      height: 900,
    })).toBe(true);
    expect(nativeHost.crashForAcceptance()).toBe(true);
    expect(nativeHost.sendProcessFailure({
      type: "NotTrusted",
      message: "must be rejected",
    })).toBe(false);
    expect(nativeHost.sendNativeAcceptanceScenario({
      scenarioId: "invalid scenario",
      windowHandle: 12345n,
      width: 1440,
      height: 900,
    })).toBe(false);
    nativeHost.configuration.rum.enabled = false;
    expect(nativeHost.send(logEvent(), "main-renderer")).toBe(true);
    expect(nativeHost.send(rumEvent(), "main-renderer")).toBe(false);

    expect(write).toHaveBeenCalledTimes(8);
    expect(write.mock.calls[0][0]).toContain("app_id=win_sample");
    expect(write.mock.calls[0][1]).toBe("utf8");
    expect(write.mock.calls[1][0]).toContain("@guance-log\tstatus=warning\tmessage=");
    expect(write.mock.calls[1][1]).toBe("utf8");
    expect(write.mock.calls[2][0]).toContain(
      "@guance-replay\tview_id=browser-view",
    );
    expect(write.mock.calls[2][0]).not.toContain("session_id=");
    expect(write.mock.calls[2][1]).toBe("utf8");
    expect(write.mock.calls[3]).toEqual([
      "@guance-launch\ttype=cold\tstart_time_ns=100\tduration_ns=60\t" +
        "pre_application_duration_ns=20\tapplication_duration_ns=20\t" +
        "first_frame_duration_ns=20\tview_id=browser-view\t" +
        "view_name=Control%20Room\tview_referrer=file%3A%2F%2F%2Fsplash.html\n",
      "utf8",
    ]);
    expect(write.mock.calls[4]).toEqual([
      "@guance-error\ttype=ElectronRendererProcessGone\t" +
        "message=renderer%20crashed%09(exit%2011)\n",
      "utf8",
    ]);
    expect(write.mock.calls[5]).toEqual([
      "@guance-native-scenario\tscenario_id=native-scenario-1\t" +
        "window_handle=12345\twidth=1440\theight=900\n",
      "utf8",
    ]);
    expect(write.mock.calls[6]).toEqual(["@guance-native-crash\n", "utf8"]);
    expect(write.mock.calls[7][0]).toContain("@guance-log\tstatus=warning\tmessage=");
  });

  it("restarts the native host once after a controlled acceptance crash", async () => {
    vi.useFakeTimers();
    try {
      const createChild = () => Object.assign(new EventEmitter(), {
        stdin: Object.assign(new EventEmitter(), {
          writable: true,
          write: vi.fn(),
          end: vi.fn(),
        }),
        stdout: Object.assign(new EventEmitter(), { setEncoding: vi.fn() }),
        stderr: Object.assign(new EventEmitter(), { setEncoding: vi.fn() }),
      });
      const firstChild = createChild();
      const secondChild = createChild();
      const spawnProcess = vi.fn()
        .mockReturnValueOnce(firstChild)
        .mockReturnValueOnce(secondChild);
      const nativeHost = new NativeRumHost({
        paths: {
          directory: "C:\\native",
          executablePath: "C:\\native\\guance_windows_electron_bridge.exe",
          libraryPath: "C:\\native\\guance_windows_native.dll",
        },
        configuration: nativeConfiguration(),
        trustedContext: { tags: {}, fields: {} },
        spawnProcess,
        fileExists: () => true,
        logger: { log: vi.fn(), error: vi.fn() },
      });

      nativeHost.start();
      expect(nativeHost.crashForAcceptance()).toBe(true);
      firstChild.emit("exit", 3, null);
      expect(nativeHost.child).toBeUndefined();

      await vi.advanceTimersByTimeAsync(250);

      expect(spawnProcess).toHaveBeenCalledTimes(2);
      expect(nativeHost.child).toBe(secondChild);
    } finally {
      vi.useRealTimers();
    }
  });
});
