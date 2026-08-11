"use strict";

const net = require("node:net");
const { browserBridgeEventToNativeInput } = require("./browser-rum-line-protocol.cjs");

const DEFAULT_PIPE_NAME = "guance-rum-electron-native-owned";
const MAX_CAPABILITIES_BYTES = 16 * 1024;
const PROCESS_FAILURE_TYPES = new Set([
  "ElectronRendererProcessGone",
  "ElectronRendererUnresponsive",
]);
const MAX_INT64 = 9_223_372_036_854_775_807n;

function launchInteger(value, name) {
  const text = String(value);
  if (!/^\d+$/.test(text) || BigInt(text) > MAX_INT64) {
    throw new Error(`${name} must be a non-negative int64.`);
  }
  return text;
}

function resolveNativeOwnedPipePath(pipeName = process.env.GUANCE_RUM_NATIVE_OWNED_PIPE_NAME) {
  const normalized = pipeName || DEFAULT_PIPE_NAME;
  if (!/^[A-Za-z0-9_.-]{1,96}$/.test(normalized)) {
    throw new Error("Native-owned pipe name must be a safe identifier.");
  }
  return `\\\\.\\pipe\\${normalized}`;
}

function parseCapabilities(line) {
  const parts = line.split("\t");
  if (parts.shift() !== "@guance-capabilities") {
    throw new Error("C++ host did not provide the Native-owned capabilities handshake.");
  }
  const fields = new Map();
  for (const part of parts) {
    const separator = part.indexOf("=");
    if (separator <= 0 || fields.has(part.slice(0, separator))) {
      throw new Error("C++ host capabilities handshake is malformed.");
    }
    fields.set(part.slice(0, separator), part.slice(separator + 1));
  }
  if (fields.get("protocol") !== "1") {
    throw new Error("C++ host uses an unsupported Native-owned protocol version.");
  }
  const booleanField = (name) => {
    const value = fields.get(name);
    if (value !== "0" && value !== "1") {
      throw new Error(`C++ host capability ${name} is invalid.`);
    }
    return value === "1";
  };
  const traceSampleRate = Number(fields.get("trace_sample_rate"));
  if (!Number.isFinite(traceSampleRate) || traceSampleRate < 0 || traceSampleRate > 100) {
    throw new Error("C++ host trace sample rate is invalid.");
  }
  const privacyLevel = fields.get("replay_privacy");
  if (!new Set(["allow", "mask-user-input", "mask"]).has(privacyLevel)) {
    throw new Error("C++ host Replay privacy capability is invalid.");
  }
  const allowedUrls = decodeURIComponent(fields.get("trace_allowed_urls") || "")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);
  return {
    rum: {
      enabled: booleanField("rum"),
      sessionReplay: {
        enabled: booleanField("replay"),
        privacyLevel,
      },
    },
    log: { enabled: booleanField("log") },
    trace: {
      enabled: booleanField("trace"),
      sampleRate: traceSampleRate,
      type: fields.get("trace_type") || "w3c_traceparent",
      allowedUrls,
    },
    runtime: { debug: booleanField("debug") },
  };
}

class NativeOwnedRumBridge {
  constructor({ socket, nativePolicy, logger = console }) {
    this.socket = socket;
    this.nativePolicy = nativePolicy;
    this.logger = logger;
    this.accepted = 0;
    this.rejected = 0;
    socket.on?.("error", (error) => {
      this.logger.error("[electron-main][native-owned] pipe failed", error);
    });
  }

  writable() {
    return Boolean(this.socket?.writable && !this.socket?.destroyed);
  }

  write(line, label) {
    if (!this.writable()) {
      this.rejected += 1;
      this.logger.error(`[electron-main][native-owned] ${label} rejected: pipe unavailable`);
      return false;
    }
    this.socket.write(line, "utf8");
    this.accepted += 1;
    return true;
  }

  send(serializedEvent, rendererLabel) {
    try {
      const payload = browserBridgeEventToNativeInput(serializedEvent, {
        tags: { is_electron: "true" },
      });
      if (payload.measurement === "log" && !this.nativePolicy.log.enabled) {
        throw new Error("C++ host did not enable Browser Log collection.");
      }
      if (payload.measurement === "session_replay" &&
          !this.nativePolicy.rum.sessionReplay.enabled) {
        throw new Error("C++ host did not enable Session Replay.");
      }
      if (payload.measurement !== "log" &&
          payload.measurement !== "session_replay" &&
          !this.nativePolicy.rum.enabled) {
        throw new Error("C++ host did not enable Browser RUM collection.");
      }
      return this.write(payload.line, `${rendererLabel} ${payload.measurement}`);
    } catch (error) {
      this.rejected += 1;
      this.logger.error(
        `[electron-main][native-owned] rejected renderer payload: ${
          error instanceof Error ? error.message : "unknown error"
        }`,
      );
      return false;
    }
  }

  sendLaunch({
    type,
    startTimeNanoseconds,
    durationNanoseconds,
    preApplicationDurationNanoseconds = 0,
    applicationDurationNanoseconds = 0,
    firstFrameDurationNanoseconds = 0,
  }) {
    try {
      if (!this.nativePolicy.rum.enabled || (type !== "cold" && type !== "hot")) {
        throw new Error("C++ host RUM capability or launch type is invalid.");
      }
      const values = [
        launchInteger(startTimeNanoseconds, "startTimeNanoseconds"),
        launchInteger(durationNanoseconds, "durationNanoseconds"),
        launchInteger(preApplicationDurationNanoseconds, "preApplicationDurationNanoseconds"),
        launchInteger(applicationDurationNanoseconds, "applicationDurationNanoseconds"),
        launchInteger(firstFrameDurationNanoseconds, "firstFrameDurationNanoseconds"),
      ];
      return this.write([
        "@guance-launch",
        `type=${type}`,
        `start_time_ns=${values[0]}`,
        `duration_ns=${values[1]}`,
        `pre_application_duration_ns=${values[2]}`,
        `application_duration_ns=${values[3]}`,
        `first_frame_duration_ns=${values[4]}`,
      ].join("\t") + "\n", `launch_${type}`);
    } catch (error) {
      this.rejected += 1;
      this.logger.error("[electron-main][native-owned] rejected launch payload", error);
      return false;
    }
  }

  sendProcessFailure({ type, message }) {
    if (!this.nativePolicy.rum.enabled || !PROCESS_FAILURE_TYPES.has(type) ||
        typeof message !== "string" || message.length === 0 || message.length > 512) {
      this.rejected += 1;
      return false;
    }
    return this.write(
      `@guance-error\ttype=${type}\tmessage=${encodeURIComponent(message)}\n`,
      type,
    );
  }

  sendNativeAcceptanceScenario() {
    return false;
  }

  crashForAcceptance() {
    return false;
  }

  disconnect() {
    const socket = this.socket;
    this.socket = undefined;
    if (!socket || socket.destroyed) return Promise.resolve();
    return new Promise((resolve) => {
      let complete = false;
      const finish = () => {
        if (complete) return;
        complete = true;
        clearTimeout(timeout);
        resolve();
      };
      const timeout = setTimeout(() => {
        socket.destroy();
        finish();
      }, 2_000);
      socket.once?.("close", finish);
      socket.end(finish);
    });
  }
}

function connectNativeOwnedRumBridge({
  pipePath = resolveNativeOwnedPipePath(),
  connect = (options) => net.createConnection(options),
  logger = console,
  timeoutMs = 10_000,
} = {}) {
  return new Promise((resolve, reject) => {
    const socket = connect({ path: pipePath });
    let settled = false;
    let pending = "";
    const timeout = setTimeout(() => fail(new Error("Timed out waiting for C++ host capabilities.")), timeoutMs);
    const cleanup = () => {
      clearTimeout(timeout);
      socket.off?.("data", onData);
      socket.off?.("error", fail);
      socket.off?.("end", onClosed);
      socket.off?.("close", onClosed);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy?.();
      reject(error);
    };
    const onClosed = () => {
      fail(new Error("C++ host closed the pipe before sending capabilities."));
    };
    const onData = (chunk) => {
      pending += String(chunk);
      if (Buffer.byteLength(pending, "utf8") > MAX_CAPABILITIES_BYTES) {
        fail(new Error("C++ host capabilities handshake is too large."));
        return;
      }
      const newline = pending.indexOf("\n");
      if (newline < 0) return;
      try {
        const nativePolicy = parseCapabilities(pending.slice(0, newline).replace(/\r$/, ""));
        settled = true;
        cleanup();
        resolve(new NativeOwnedRumBridge({ socket, nativePolicy, logger }));
      } catch (error) {
        fail(error);
      }
    };
    socket.setEncoding?.("utf8");
    socket.on?.("data", onData);
    socket.once?.("error", fail);
    socket.once?.("end", onClosed);
    socket.once?.("close", onClosed);
  });
}

module.exports = {
  DEFAULT_PIPE_NAME,
  NativeOwnedRumBridge,
  connectNativeOwnedRumBridge,
  parseCapabilities,
  resolveNativeOwnedPipePath,
};
