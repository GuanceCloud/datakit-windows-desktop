import { EventEmitter } from "node:events";
import { createRequire } from "node:module";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const {
  NativeOwnedRumBridge,
  connectNativeOwnedRumBridge,
  parseCapabilities,
  resolveNativeOwnedPipePath,
} = require("../src/main/native-owned-rum-bridge.cjs");

const handshake = [
  "@guance-capabilities",
  "protocol=1",
  "rum=1",
  "log=1",
  "replay=1",
  "replay_privacy=mask-user-input",
  "trace=1",
  "trace_sample_rate=75",
  "trace_type=w3c_traceparent",
  `trace_allowed_urls=${encodeURIComponent("https://api.example.test")}`,
  "debug=0",
].join("\t");

class FakeSocket extends EventEmitter {
  writable = true;
  destroyed = false;
  writes: string[] = [];

  setEncoding() {}

  write(value: string) {
    this.writes.push(value);
    return true;
  }

  end(callback?: () => void) {
    this.writable = false;
    callback?.();
    this.emit("close");
  }

  destroy() {
    this.destroyed = true;
    this.writable = false;
    this.emit("close");
  }
}

function rumEvent() {
  return JSON.stringify({
    name: "rum",
    data: {
      measurement: "view",
      tags: {
        app_id: "renderer-placeholder",
        session_id: "renderer-session",
        view_id: "browser-view",
      },
      fields: { is_active: true },
      time: 1_722_300_000_000,
    },
  });
}

describe("C++ native-owned Electron bridge", () => {
  it("parses only non-sensitive host capabilities", () => {
    expect(parseCapabilities(handshake)).toEqual({
      rum: {
        enabled: true,
        sessionReplay: { enabled: true, privacyLevel: "mask-user-input" },
      },
      log: { enabled: true },
      trace: {
        enabled: true,
        sampleRate: 75,
        type: "w3c_traceparent",
        allowedUrls: ["https://api.example.test"],
      },
      runtime: { debug: false },
    });
    expect(handshake).not.toContain("app_id");
    expect(handshake).not.toContain("client_token");
    expect(handshake).not.toContain("datakit");
  });

  it("connects to the host pipe and writes Browser data without owning its lifecycle", async () => {
    const socket = new FakeSocket();
    const bridgePromise = connectNativeOwnedRumBridge({
      pipePath: "\\\\.\\pipe\\guance-test",
      connect: vi.fn(() => socket),
      timeoutMs: 1_000,
      logger: { log: vi.fn(), error: vi.fn() },
    });
    queueMicrotask(() => socket.emit("data", `${handshake}\n`));
    const bridge = await bridgePromise;

    expect(bridge).toBeInstanceOf(NativeOwnedRumBridge);
    expect(bridge.send(rumEvent(), "workspace-renderer")).toBe(true);
    expect(socket.writes[0]).toContain("app_id=renderer-placeholder");
    expect(socket.writes[0]).toContain("session_id=renderer-session");
    expect(socket.writes[0]).toContain("is_electron=true");
    expect(bridge.sendLaunch({
      type: "cold",
      startTimeNanoseconds: 100n,
      durationNanoseconds: 60n,
      view: {
        id: "browser-view",
        name: "Control Room",
        referrer: "file:///splash.html",
      },
    })).toBe(true);
    expect(socket.writes[1]).toContain("@guance-launch\ttype=cold");
    expect(socket.writes[1]).toContain(
      "\tview_id=browser-view\tview_name=Control%20Room\t" +
      "view_referrer=file%3A%2F%2F%2Fsplash.html\n",
    );

    await bridge.disconnect();
    expect(socket.writable).toBe(false);
  });

  it("rejects when the host closes before sending capabilities", async () => {
    const socket = new FakeSocket();
    const bridgePromise = connectNativeOwnedRumBridge({
      pipePath: "\\\\.\\pipe\\guance-test",
      connect: vi.fn(() => socket),
      timeoutMs: 1_000,
      logger: { log: vi.fn(), error: vi.fn() },
    });
    queueMicrotask(() => socket.emit("end"));

    await expect(bridgePromise).rejects.toThrow(
      "closed the pipe before sending capabilities",
    );
    expect(socket.destroyed).toBe(true);
  });

  it("validates the configured named-pipe identifier", () => {
    expect(resolveNativeOwnedPipePath("guance-host")).toBe(
      "\\\\.\\pipe\\guance-host",
    );
    expect(() => resolveNativeOwnedPipePath("..\\unsafe")).toThrow("safe identifier");
  });
});
