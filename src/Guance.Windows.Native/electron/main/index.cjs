"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const {
  browserBridgeEventToNativeInput,
} = require("../internal/rum-line-protocol.cjs");
const {
  connectNativeOwnedBridge,
  parseCapabilities,
} = require("../internal/native-owned-bridge.cjs");
const {
  BRIDGE_CHANNEL,
  BRIDGE_CONFIGURATION_CHANNEL,
} = require("../internal/constants.cjs");

const ipcOwners = new WeakMap();

function requireObject(value, label) {
  if (!value || typeof value !== "object") {
    throw new Error(`${label} is required.`);
  }
  return value;
}

function optionalString(value, label) {
  if (value === undefined || value === null || value === "") return "";
  if (typeof value !== "string") throw new Error(`${label} must be a string.`);
  return value;
}

function requiredString(value, label) {
  const result = optionalString(value, label).trim();
  if (!result) throw new Error(`${label} is required.`);
  return result;
}

function rate(value, fallback, label) {
  const normalized = value ?? fallback;
  if (typeof normalized !== "number" || !Number.isFinite(normalized) ||
      normalized < 0 || normalized > 1) {
    throw new Error(`${label} must be a finite number from 0 through 1.`);
  }
  return normalized;
}

function integer(value, fallback, minimum, maximum, label) {
  const normalized = value ?? fallback;
  if (!Number.isInteger(normalized) || normalized < minimum || normalized > maximum) {
    throw new Error(`${label} must be an integer from ${minimum} through ${maximum}.`);
  }
  return normalized;
}

function reportError(onError, error) {
  if (typeof onError === "function") {
    onError(error);
  } else {
    console.error("[Guance.ElectronRumAdapter]", error);
  }
}

function registerTrustedIpc(ipcMain, {
  send,
  replayEnabled,
  replayPrivacy,
  onError,
}) {
  requireObject(ipcMain, "ipcMain");
  if (typeof ipcMain.on !== "function" || typeof ipcMain.removeListener !== "function") {
    throw new Error("ipcMain must provide on() and removeListener().");
  }
  if (ipcOwners.has(ipcMain)) {
    throw new Error("The Guance Electron RUM IPC channel is already registered.");
  }

  const allowedWebContents = new Set();
  const isTrustedMainFrame = (event) => {
    const sender = event?.sender;
    return allowedWebContents.has(sender) &&
      !(typeof sender?.isDestroyed === "function" && sender.isDestroyed()) &&
      event.senderFrame === sender?.mainFrame;
  };
  const handler = (event, serializedEvent) => {
    if (!isTrustedMainFrame(event)) return;
    try {
      send(serializedEvent);
    } catch (error) {
      reportError(onError, error);
    }
  };
  const configurationHandler = (event) => {
    event.returnValue = isTrustedMainFrame(event)
      ? {
          replayEnabled,
          replayPrivacy: replayEnabled ? replayPrivacy : "mask",
        }
      : { replayEnabled: false, replayPrivacy: "mask" };
  };
  const owner = { configurationHandler, handler };
  ipcOwners.set(ipcMain, owner);
  ipcMain.on(BRIDGE_CHANNEL, handler);
  ipcMain.on(BRIDGE_CONFIGURATION_CHANNEL, configurationHandler);

  return {
    attachWindow(windowOrWebContents) {
      const webContents = windowOrWebContents?.webContents || windowOrWebContents;
      if (!webContents || typeof webContents !== "object" || !webContents.mainFrame) {
        throw new Error("attachWindow requires an Electron BrowserWindow or WebContents.");
      }
      allowedWebContents.add(webContents);
      return () => allowedWebContents.delete(webContents);
    },
    dispose() {
      allowedWebContents.clear();
      ipcMain.removeListener(BRIDGE_CHANNEL, handler);
      ipcMain.removeListener(BRIDGE_CONFIGURATION_CHANNEL, configurationHandler);
      if (ipcOwners.get(ipcMain) === owner) ipcOwners.delete(ipcMain);
    },
  };
}

function mapNativeSettings(settings) {
  requireObject(settings, "nativeSettings");
  const applicationId = requiredString(settings.applicationId, "nativeSettings.applicationId");
  const datakitUrl = optionalString(settings.datakitUrl, "nativeSettings.datakitUrl").trim();
  const datawayUrl = optionalString(settings.datawayUrl, "nativeSettings.datawayUrl").trim();
  if (!datakitUrl && !datawayUrl) {
    throw new Error("nativeSettings.datakitUrl or nativeSettings.datawayUrl is required.");
  }
  const service = requiredString(settings.service, "nativeSettings.service");
  const environment = requiredString(settings.environment, "nativeSettings.environment");
  const version = requiredString(settings.version, "nativeSettings.version");
  const replayPrivacy = optionalString(
    settings.replayPrivacy,
    "nativeSettings.replayPrivacy",
  ) || "mask";
  if (!new Set(["allow", "mask-user-input", "mask"]).has(replayPrivacy)) {
    throw new Error("nativeSettings.replayPrivacy is invalid.");
  }
  const traceType = optionalString(settings.traceType, "nativeSettings.traceType") ||
    "w3c_traceparent";

  const normalized = {
    applicationId,
    datakitUrl,
    datawayUrl,
    clientToken: optionalString(settings.clientToken, "nativeSettings.clientToken"),
    service,
    environment,
    version,
    cachePath: optionalString(settings.cachePath, "nativeSettings.cachePath"),
    sampleRate: rate(settings.sampleRate, 1, "nativeSettings.sampleRate"),
    loggingEnabled: Boolean(settings.loggingEnabled),
    loggingSampleRate: rate(
      settings.loggingSampleRate,
      1,
      "nativeSettings.loggingSampleRate",
    ),
    replayEnabled: Boolean(settings.replayEnabled),
    replaySampleRate: rate(
      settings.replaySampleRate,
      1,
      "nativeSettings.replaySampleRate",
    ),
    replayPrivacy,
    traceEnabled: Boolean(settings.traceEnabled),
    traceSampleRate: rate(
      settings.traceSampleRate,
      1,
      "nativeSettings.traceSampleRate",
    ),
    traceType,
    traceAllowedUrls: optionalString(
      settings.traceAllowedUrls,
      "nativeSettings.traceAllowedUrls",
    ),
    debug: Boolean(settings.debug),
    httpTimeoutMs: integer(
      settings.httpTimeoutMs,
      10_000,
      1,
      300_000,
      "nativeSettings.httpTimeoutMs",
    ),
  };

  const nativeEnvironment = {
    GUANCE_RUM_NATIVE_DATAKIT_URL: normalized.datakitUrl,
    GUANCE_RUM_NATIVE_DATAWAY_URL: normalized.datawayUrl,
    GUANCE_RUM_NATIVE_CLIENT_TOKEN: normalized.clientToken,
    GUANCE_RUM_NATIVE_APP_ID: normalized.applicationId,
    GUANCE_RUM_NATIVE_SERVICE: normalized.service,
    GUANCE_RUM_NATIVE_ENV: normalized.environment,
    GUANCE_RUM_NATIVE_VERSION: normalized.version,
    GUANCE_RUM_NATIVE_CACHE_PATH: normalized.cachePath,
    GUANCE_RUM_NATIVE_SAMPLE_RATE: String(normalized.sampleRate),
    GUANCE_RUM_NATIVE_LOG_ENABLED: normalized.loggingEnabled ? "1" : "0",
    GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE: String(normalized.loggingSampleRate),
    GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED: normalized.replayEnabled ? "1" : "0",
    GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE: String(normalized.replaySampleRate),
    GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL: normalized.replayPrivacy,
    GUANCE_RUM_NATIVE_TRACE_ENABLED: normalized.traceEnabled ? "1" : "0",
    GUANCE_RUM_NATIVE_TRACE_SAMPLE_RATE: String(normalized.traceSampleRate),
    GUANCE_RUM_NATIVE_TRACE_TYPE: normalized.traceType,
    GUANCE_RUM_NATIVE_TRACE_ALLOWED_URLS: normalized.traceAllowedUrls,
    GUANCE_RUM_NATIVE_DEBUG: normalized.debug ? "1" : "0",
    GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS: String(normalized.httpTimeoutMs),
  };
  return { normalized, nativeEnvironment };
}

function waitForNativeReady(child, timeoutMs, onNativeOutput) {
  return new Promise((resolve, reject) => {
    let settled = false;
    let pendingStdout = "";
    let stderr = "";
    const timeout = setTimeout(() => {
      fail(new Error("Timed out waiting for the Guance Electron Bridge EXE handshake."));
    }, timeoutMs);
    const cleanup = () => {
      clearTimeout(timeout);
      child.off("error", onError);
      child.off("exit", onExit);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    };
    const onError = (error) => fail(error);
    const onExit = (code, signal) => fail(new Error(
      `Guance Electron Bridge EXE exited before ready (code=${code}, signal=${signal}). ${stderr}`,
    ));
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      if (typeof onNativeOutput === "function") onNativeOutput("stdout", String(chunk));
      if (settled) return;
      pendingStdout += chunk;
      if (Buffer.byteLength(pendingStdout, "utf8") > 64 * 1024) {
        fail(new Error("Guance Electron Bridge EXE startup output is too large."));
        return;
      }
      let newline;
      while (!settled && (newline = pendingStdout.indexOf("\n")) >= 0) {
        const line = pendingStdout.slice(0, newline).replace(/\r$/, "");
        pendingStdout = pendingStdout.slice(newline + 1);
        if (!line.startsWith("@guance-capabilities")) continue;
        try {
          const capabilities = parseCapabilities(line);
          settled = true;
          cleanup();
          resolve(capabilities);
        } catch (error) {
          fail(new Error(`Guance Electron Bridge EXE handshake failed: ${error.message}`));
        }
      }
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
      if (typeof onNativeOutput === "function") onNativeOutput("stderr", String(chunk));
      if (stderr.length > 64 * 1024) stderr = stderr.slice(-64 * 1024);
    });
    child.once("error", onError);
    child.once("exit", onExit);
  });
}

function stopChild(child, timeoutMs) {
  if (child.exitCode !== null || child.signalCode !== null) return Promise.resolve();
  return new Promise((resolve) => {
    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      resolve();
    };
    const timeout = setTimeout(() => {
      child.kill();
      finish();
    }, timeoutMs);
    child.once("exit", finish);
    if (child.stdin?.writable) child.stdin.end();
    else finish();
  });
}

async function startFullMode({
  ipcMain,
  nativeDirectory,
  nativeSettings,
  readyTimeoutMs = 10_000,
  stopTimeoutMs = 3_000,
  onError,
  onNativeOutput,
} = {}) {
  const directory = path.resolve(requiredString(nativeDirectory, "nativeDirectory"));
  const bridgePath = path.join(directory, "guance_windows_electron_bridge.exe");
  const runtimePath = path.join(directory, "guance_windows_native.dll");
  for (const requiredPath of [bridgePath, runtimePath]) {
    if (!fs.existsSync(requiredPath)) {
      throw new Error(`The installed native runtime is missing: ${requiredPath}`);
    }
  }
  const { normalized, nativeEnvironment } = mapNativeSettings(nativeSettings);
  const readyLimit = integer(readyTimeoutMs, 10_000, 1, 60_000, "readyTimeoutMs");
  const stopLimit = integer(stopTimeoutMs, 3_000, 1, 30_000, "stopTimeoutMs");
  const child = spawn(bridgePath, [], {
    cwd: directory,
    windowsHide: true,
    stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, ...nativeEnvironment },
  });

  let nativeCapabilities;
  try {
    nativeCapabilities = await waitForNativeReady(child, readyLimit, onNativeOutput);
  } catch (error) {
    await stopChild(child, stopLimit);
    throw error;
  }

  let stopping = false;
  let transportFailure;
  let backpressured = false;
  const failTransport = (error) => {
    if (stopping || transportFailure) return;
    transportFailure = error;
    reportError(onError, error);
  };
  child.on("error", failTransport);
  child.on("exit", (code, signal) => failTransport(new Error(
    `Guance Electron Bridge EXE exited unexpectedly (code=${code}, signal=${signal}).`,
  )));
  child.stdin.on("error", failTransport);
  child.stdin.on("drain", () => { backpressured = false; });
  const trustedTags = {
    app_id: normalized.applicationId,
    service: normalized.service,
    env: normalized.environment,
    version: normalized.version,
    sdk_name: "df_windows_rum_sdk",
    is_electron: "true",
  };
  let ipc;
  try {
    ipc = registerTrustedIpc(ipcMain, {
      replayEnabled: nativeCapabilities.replay,
      replayPrivacy: nativeCapabilities.replayPrivacy,
      onError,
      send(serializedEvent) {
        if (transportFailure || !child.stdin?.writable) {
          throw new Error("The Guance Electron Bridge EXE is not writable.");
        }
        if (backpressured) {
          throw new Error("The Guance Electron Bridge EXE is applying backpressure.");
        }
        const payload = browserBridgeEventToNativeInput(serializedEvent, trustedTags);
        if (payload.measurement === "log" && !normalized.loggingEnabled) {
          throw new Error("Browser Log collection is not enabled in the native settings.");
        }
        if (payload.measurement === "session_replay" && !nativeCapabilities.replay) {
          throw new Error("Browser Session Replay is not enabled in the native settings.");
        }
        const accepted = child.stdin.write(payload.line, "utf8");
        if (!accepted) backpressured = true;
      },
    });
  } catch (error) {
    await stopChild(child, stopLimit);
    throw error;
  }

  let stopped;
  return Object.freeze({
    mode: "full",
    capabilities: nativeCapabilities,
    attachWindow: ipc.attachWindow,
    stop() {
      if (!stopped) {
        stopping = true;
        ipc.dispose();
        stopped = stopChild(child, stopLimit);
      }
      return stopped;
    },
  });
}

async function connectMixedMode({
  ipcMain,
  pipeName,
  timeoutMs = 10_000,
  retryDelayMs = 100,
  onError,
} = {}) {
  const nativeBridge = await connectNativeOwnedBridge({
    pipeName,
    timeoutMs,
    retryDelayMs,
    onError: (error) => reportError(onError, error),
  });
  let ipc;
  try {
    ipc = registerTrustedIpc(ipcMain, {
      replayEnabled: nativeBridge.capabilities.replay,
      replayPrivacy: nativeBridge.capabilities.replayPrivacy,
      onError,
      send(serializedEvent) {
        if (!nativeBridge.send(serializedEvent)) {
          throw new Error("The application-owned Native Bridge Server is not writable.");
        }
      },
    });
  } catch (error) {
    await nativeBridge.disconnect();
    throw error;
  }

  let stopped;
  return Object.freeze({
    mode: "mixed",
    capabilities: nativeBridge.capabilities,
    attachWindow: ipc.attachWindow,
    stop() {
      if (!stopped) {
        ipc.dispose();
        stopped = nativeBridge.disconnect();
      }
      return stopped;
    },
  });
}

module.exports = {
  connectMixedMode,
  startFullMode,
};
