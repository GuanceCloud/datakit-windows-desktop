"use strict";

const net = require("node:net");
const {
  browserBridgeEventToNativeInput,
} = require("./rum-line-protocol.cjs");
const {
  DEFAULT_PIPE_NAME,
  MAX_CAPABILITIES_BYTES,
} = require("./constants.cjs");

const RETRYABLE_PIPE_ERRORS = new Set(["ENOENT", "ECONNREFUSED", "EBUSY"]);

function resolvePipePath(pipeName = process.env.GUANCE_RUM_NATIVE_OWNED_PIPE_NAME) {
  const normalized = pipeName || DEFAULT_PIPE_NAME;
  if (!/^[A-Za-z0-9_.-]{1,96}$/.test(normalized)) {
    throw new Error("Native-owned pipe name must be a safe identifier.");
  }
  return `\\\\.\\pipe\\${normalized}`;
}

function parseBooleanCapability(fields, name) {
  const value = fields.get(name);
  if (value !== "0" && value !== "1") {
    throw new Error(`Native Bridge capability ${name} must be 0 or 1.`);
  }
  return value === "1";
}

function parseCapabilities(line) {
  const parts = line.split("\t");
  if (parts.shift() !== "@guance-capabilities") {
    throw new Error("Native Bridge Server did not provide a capabilities handshake.");
  }
  const fields = new Map();
  for (const part of parts) {
    const separator = part.indexOf("=");
    const name = part.slice(0, separator);
    if (separator <= 0 || fields.has(name)) {
      throw new Error("Native Bridge Server capabilities handshake is malformed.");
    }
    fields.set(name, part.slice(separator + 1));
  }
  if (fields.get("protocol") !== "1" || fields.get("rum") !== "1") {
    throw new Error("Native Bridge Server does not support RUM bridge protocol 1.");
  }
  return Object.freeze({
    rum: true,
    log: parseBooleanCapability(fields, "log"),
    replay: parseBooleanCapability(fields, "replay"),
    trace: parseBooleanCapability(fields, "trace"),
    replayPrivacy: fields.get("replay_privacy") || "mask",
    traceSampleRate: Number(fields.get("trace_sample_rate") || 0),
    traceType: fields.get("trace_type") || "w3c_traceparent",
  });
}

function connectOnce(pipePath, timeoutMs) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ path: pipePath });
    let pending = "";
    let settled = false;
    const timeout = setTimeout(() => {
      fail(new Error("Timed out waiting for the Native Bridge capabilities handshake."));
    }, timeoutMs);
    const cleanup = () => {
      clearTimeout(timeout);
      socket.off("data", onData);
      socket.off("error", onError);
      socket.off("close", onClose);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy();
      reject(error);
    };
    const onError = (error) => fail(error);
    const onClose = () => fail(
      new Error("Native Bridge Server closed before the capabilities handshake."),
    );
    const onData = (chunk) => {
      pending += String(chunk);
      if (Buffer.byteLength(pending, "utf8") > MAX_CAPABILITIES_BYTES) {
        fail(new Error("Native Bridge Server capabilities handshake is too large."));
        return;
      }
      const newline = pending.indexOf("\n");
      if (newline < 0) return;
      try {
        const capabilities = parseCapabilities(
          pending.slice(0, newline).replace(/\r$/, ""),
        );
        settled = true;
        cleanup();
        resolve({ socket, capabilities });
      } catch (error) {
        fail(error);
      }
    };
    socket.setEncoding("utf8");
    socket.on("data", onData);
    socket.once("error", onError);
    socket.once("close", onClose);
  });
}

function validateRetryOptions(timeoutMs, retryDelayMs) {
  if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 60_000) {
    throw new Error("Mixed Mode timeoutMs must be an integer from 1 through 60000.");
  }
  if (!Number.isInteger(retryDelayMs) || retryDelayMs < 10 || retryDelayMs > 5_000) {
    throw new Error("Mixed Mode retryDelayMs must be an integer from 10 through 5000.");
  }
}

async function connectNativeOwnedBridge({
  pipeName,
  pipePath = resolvePipePath(pipeName),
  timeoutMs = 10_000,
  retryDelayMs = 100,
  onError,
} = {}) {
  validateRetryOptions(timeoutMs, retryDelayMs);
  const deadline = Date.now() + timeoutMs;
  let lastError;
  do {
    const remainingMs = Math.max(1, deadline - Date.now());
    try {
      const { socket, capabilities } = await connectOnce(pipePath, remainingMs);
      let disconnected;
      let disconnecting = false;
      let transportFailure;
      let backpressured = false;
      const failTransport = (error) => {
        if (disconnecting || transportFailure) return;
        transportFailure = error;
        if (typeof onError === "function") onError(error);
      };
      socket.on("error", failTransport);
      socket.on("close", () => failTransport(new Error(
        "The application-owned Native Bridge Server disconnected unexpectedly.",
      )));
      socket.on("drain", () => { backpressured = false; });
      return {
        capabilities,
        writable: () => Boolean(
          !transportFailure && !backpressured && socket.writable && !socket.destroyed,
        ),
        send: (serializedEvent) => {
          if (transportFailure || backpressured || !socket.writable || socket.destroyed) {
            return false;
          }
          const payload = browserBridgeEventToNativeInput(
            serializedEvent,
            { is_electron: "true" },
          );
          if (payload.measurement === "log" && !capabilities.log) {
            throw new Error("Native Bridge Server did not enable Browser Log collection.");
          }
          try {
            if (!socket.write(payload.line, "utf8")) backpressured = true;
            return true;
          } catch (error) {
            failTransport(error);
            return false;
          }
        },
        disconnect: () => {
          if (disconnected) return disconnected;
          disconnecting = true;
          disconnected = new Promise((resolve) => {
            if (socket.destroyed) {
              resolve();
              return;
            }
            let settled = false;
            const finish = () => {
              if (settled) return;
              settled = true;
              clearTimeout(timeout);
              resolve();
            };
            const timeout = setTimeout(() => {
              socket.destroy();
              finish();
            }, 3_000);
            socket.once("close", finish);
            socket.end();
          });
          return disconnected;
        },
      };
    } catch (error) {
      lastError = error;
      if (!RETRYABLE_PIPE_ERRORS.has(error.code)) throw error;
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      await new Promise((resolve) => setTimeout(resolve, Math.min(retryDelayMs, remaining)));
    }
  } while (Date.now() < deadline);
  throw new Error(
    `Timed out connecting to the Native Bridge Server: ${lastError?.message || "unknown error"}`,
  );
}

module.exports = {
  connectNativeOwnedBridge,
  parseCapabilities,
  resolvePipePath,
};
