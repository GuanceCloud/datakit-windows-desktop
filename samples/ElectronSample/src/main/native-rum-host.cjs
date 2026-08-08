"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { browserBridgeEventToNativeInput } = require("./browser-rum-line-protocol.cjs");

const NATIVE_HOST_FILE = "guance_windows_electron_bridge.exe";
const NATIVE_CORE_FILE = "guance_windows_native.dll";
const MAX_INT64 = 9_223_372_036_854_775_807n;
const PROCESS_FAILURE_TYPES = new Set([
  "ElectronRendererProcessGone",
  "ElectronRendererUnresponsive",
]);

function launchInteger(value, name) {
  const text = String(value);
  if (!/^\d+$/.test(text)) {
    throw new Error(`${name} must be a non-negative integer.`);
  }
  const parsed = BigInt(text);
  if (parsed > MAX_INT64) {
    throw new Error(`${name} exceeds int64 range.`);
  }
  return text;
}

function resolveNativeRumPaths({
  isPackaged,
  resourcesPath,
  sampleRoot,
  architecture = process.arch,
}) {
  if (architecture !== "x64") {
    throw new Error(`Electron Sample native RUM bridge does not include ${architecture} binaries.`);
  }
  const nativeDirectory = isPackaged
    ? path.join(resourcesPath, "native")
    : path.resolve(sampleRoot, "..", "..", "src", "Guance.Windows.Native", "bin", "win-x64");
  return {
    directory: nativeDirectory,
    executablePath: path.join(nativeDirectory, NATIVE_HOST_FILE),
    libraryPath: path.join(nativeDirectory, NATIVE_CORE_FILE),
  };
}

function createNativeEnvironment(configuration, baseEnvironment = process.env) {
  return {
    ...baseEnvironment,
    GUANCE_RUM_NATIVE_DATAWAY_URL: configuration.transport.datawayUrl || "",
    GUANCE_RUM_NATIVE_DATAKIT_URL: configuration.transport.datakitUrl || "",
    GUANCE_RUM_NATIVE_CLIENT_TOKEN: configuration.transport.clientToken || "",
    GUANCE_RUM_NATIVE_APP_ID: configuration.application.id || "",
    GUANCE_RUM_NATIVE_SERVICE: configuration.application.service || "",
    GUANCE_RUM_NATIVE_ENV: configuration.application.env || "",
    GUANCE_RUM_NATIVE_VERSION: configuration.application.version || "",
    GUANCE_RUM_NATIVE_CACHE_PATH: configuration.runtime.cachePath || "",
    GUANCE_RUM_NATIVE_MAX_CACHE_BYTES: launchInteger(configuration.cache.maxBytes, "cache.maxBytes"),
    GUANCE_RUM_NATIVE_MAX_CACHE_FILES: launchInteger(configuration.cache.maxFiles, "cache.maxFiles"),
    GUANCE_RUM_NATIVE_MAX_CACHE_AGE_SECONDS: launchInteger(configuration.cache.maxAgeSeconds, "cache.maxAgeSeconds"),
    GUANCE_RUM_NATIVE_MAX_BATCH_ITEMS: launchInteger(configuration.cache.maxBatchItems, "cache.maxBatchItems"),
    GUANCE_RUM_NATIVE_MAX_BATCH_BYTES: launchInteger(configuration.cache.maxBatchBytes, "cache.maxBatchBytes"),
    GUANCE_RUM_NATIVE_MAX_UPLOAD_BYTES_PER_SECOND: launchInteger(configuration.upload.maxBytesPerSecond, "upload.maxBytesPerSecond"),
    GUANCE_RUM_NATIVE_UPLOAD_BURST_BYTES: launchInteger(configuration.upload.burstBytes, "upload.burstBytes"),
    GUANCE_RUM_NATIVE_MAX_UPLOAD_REQUESTS_PER_SECOND: String(configuration.upload.maxRequestsPerSecond),
    GUANCE_RUM_NATIVE_MAX_UPLOAD_BATCHES_PER_CYCLE: launchInteger(configuration.upload.maxBatchesPerCycle, "upload.maxBatchesPerCycle"),
    GUANCE_RUM_NATIVE_PROXY_URL: configuration.transport.proxyUrl || "",
    GUANCE_RUM_NATIVE_SAMPLE_RATE: String(
      configuration.rum.enabled ? configuration.rum.sampleRate ?? 1 : 0,
    ),
    GUANCE_RUM_NATIVE_LOG_ENABLED: configuration.log.enabled ? "1" : "0",
    GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE: String(configuration.log.sampleRate ?? 1),
    GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED:
      configuration.rum.enabled && configuration.rum.sessionReplay.enabled ? "1" : "0",
    GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE: String(
      configuration.rum.sessionReplay.sampleRate ?? 1,
    ),
    GUANCE_RUM_NATIVE_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE: String(
      configuration.rum.sessionReplay.onErrorSampleRate ?? 0,
    ),
    GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS: String(configuration.transport.httpTimeoutMs ?? 10_000),
    GUANCE_RUM_NATIVE_DEBUG: configuration.runtime.debug ? "1" : "0",
  };
}

class NativeRumHost {
  constructor({
    paths,
    configuration,
    trustedContext,
    spawnProcess = spawn,
    fileExists = fs.existsSync,
    logger = console,
  }) {
    this.paths = paths;
    this.configuration = configuration;
    this.trustedContext = trustedContext;
    this.spawnProcess = spawnProcess;
    this.fileExists = fileExists;
    this.logger = logger;
    this.child = undefined;
    this.accepted = 0;
    this.rejected = 0;
    this.restartAfterCrash = false;
    this.restartTimer = undefined;
  }

  start() {
    if (this.child) {
      return;
    }
    for (const requiredPath of [this.paths.executablePath, this.paths.libraryPath]) {
      if (!this.fileExists(requiredPath)) {
        throw new Error(`Missing Electron native RUM bridge binary: ${requiredPath}`);
      }
    }

    const child = this.spawnProcess(this.paths.executablePath, [], {
      cwd: this.paths.directory,
      env: createNativeEnvironment(this.configuration),
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.child = child;
    child.stdout?.setEncoding?.("utf8");
    child.stderr?.setEncoding?.("utf8");
    child.stdout?.on?.("data", (chunk) => this.writeNativeLog("info", chunk));
    child.stderr?.on?.("data", (chunk) => this.writeNativeLog("error", chunk));
    child.stdin?.on?.("error", (error) => {
      this.logger.error("[electron-main][rum-bridge] native stdin failed", error);
    });
    child.once?.("error", (error) => {
      this.logger.error("[electron-main][rum-bridge] native host failed", error);
    });
    child.once?.("exit", (code, signal) => {
      const shouldRestart = this.restartAfterCrash;
      this.restartAfterCrash = false;
      this.logger.log(
        `[electron-main][rum-bridge] native host exited code=${code} signal=${signal || "none"}`,
      );
      if (this.child === child) {
        this.child = undefined;
      }
      if (shouldRestart) {
        this.logger.log("[electron-main][rum-bridge] restarting native host after acceptance crash");
        this.restartTimer = setTimeout(() => {
          this.restartTimer = undefined;
          if (this.child) {
            return;
          }
          try {
            this.start();
          } catch (error) {
            this.logger.error("[electron-main][rum-bridge] native host restart failed", error);
          }
        }, 250);
      }
    });
  }

  writeNativeLog(level, chunk) {
    const message = String(chunk).trim();
    if (!message) {
      return;
    }
    const output = level === "error" ? this.logger.error : this.logger.log;
    output.call(this.logger, message);
  }

  send(serializedEvent, rendererLabel) {
    if (!this.child?.stdin?.writable) {
      this.rejected += 1;
      this.logger.error("[electron-main][rum-bridge] native host is unavailable");
      return false;
    }

    try {
      const payload = browserBridgeEventToNativeInput(serializedEvent, this.trustedContext);
      if (payload.measurement === "log" && !this.configuration.log.enabled) {
        throw new Error("Browser Log collection is not enabled in the native configuration.");
      }
      if (
        payload.measurement === "session_replay" &&
        (!this.configuration.rum.enabled ||
          !this.configuration.rum.sessionReplay.enabled)
      ) {
        throw new Error("Session Replay is not enabled in the native configuration.");
      }
      if (
        payload.measurement !== "log" &&
        payload.measurement !== "session_replay" &&
        !this.configuration.rum.enabled
      ) {
        throw new Error("Browser RUM collection is not enabled in the native configuration.");
      }
      this.child.stdin.write(payload.line, "utf8");
      this.accepted += 1;
      if (this.configuration.runtime.debug) {
        this.logger.log(
          `[electron-main][rum-bridge] ${rendererLabel} -> C++ ${payload.measurement} ` +
          `bytes=${Buffer.byteLength(payload.line, "utf8")} accepted=${this.accepted}`,
        );
      }
      return true;
    } catch (error) {
      this.rejected += 1;
      this.logger.error(
        `[electron-main][rum-bridge] rejected renderer payload: ${
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
    if (!this.child?.stdin?.writable) {
      this.rejected += 1;
      this.logger.error("[electron-main][rum-bridge] native host is unavailable");
      return false;
    }

    try {
      if (!this.configuration.rum.enabled) {
        throw new Error("RUM lifecycle collection is not enabled in the native configuration.");
      }
      if (type !== "cold" && type !== "hot") {
        throw new Error("launch type must be cold or hot.");
      }
      const values = [
        launchInteger(startTimeNanoseconds, "startTimeNanoseconds"),
        launchInteger(durationNanoseconds, "durationNanoseconds"),
        launchInteger(preApplicationDurationNanoseconds, "preApplicationDurationNanoseconds"),
        launchInteger(applicationDurationNanoseconds, "applicationDurationNanoseconds"),
        launchInteger(firstFrameDurationNanoseconds, "firstFrameDurationNanoseconds"),
      ];
      const fields = [
        `type=${type}`,
        `start_time_ns=${values[0]}`,
        `duration_ns=${values[1]}`,
        `pre_application_duration_ns=${values[2]}`,
        `application_duration_ns=${values[3]}`,
        `first_frame_duration_ns=${values[4]}`,
      ];
      this.child.stdin.write(`@guance-launch\t${fields.join("\t")}\n`, "utf8");
      this.accepted += 1;
      if (this.configuration.runtime.debug) {
        this.logger.log(
          `[electron-main][rum-bridge] Electron lifecycle -> C++ launch_${type} ` +
          `duration_ns=${values[1]} accepted=${this.accepted}`,
        );
      }
      return true;
    } catch (error) {
      this.rejected += 1;
      this.logger.error(
        `[electron-main][rum-bridge] rejected launch payload: ${
          error instanceof Error ? error.message : "unknown error"
        }`,
      );
      return false;
    }
  }

  sendProcessFailure({ type, message }) {
    if (!this.child?.stdin?.writable) {
      this.rejected += 1;
      this.logger.error("[electron-main][rum-bridge] native host is unavailable");
      return false;
    }

    try {
      if (!this.configuration.rum.enabled) {
        throw new Error("RUM error collection is not enabled in the native configuration.");
      }
      if (!PROCESS_FAILURE_TYPES.has(type)) {
        throw new Error("process failure type is not trusted.");
      }
      if (typeof message !== "string" || message.length === 0 || message.length > 512) {
        throw new Error("process failure message must contain 1 to 512 characters.");
      }
      const command =
        `@guance-error\ttype=${type}\tmessage=${encodeURIComponent(message)}\n`;
      this.child.stdin.write(command, "utf8");
      this.accepted += 1;
      if (this.configuration.runtime.debug) {
        this.logger.log(
          `[electron-main][rum-bridge] Electron process failure -> C++ ${type} ` +
          `accepted=${this.accepted}`,
        );
      }
      return true;
    } catch (error) {
      this.rejected += 1;
      this.logger.error(
        `[electron-main][rum-bridge] rejected process failure: ${
          error instanceof Error ? error.message : "unknown error"
        }`,
      );
      return false;
    }
  }

  sendNativeAcceptanceScenario({ scenarioId, windowHandle, width, height }) {
    if (!this.child?.stdin?.writable) {
      this.rejected += 1;
      this.logger.error("[electron-main][rum-bridge] native host is unavailable");
      return false;
    }

    try {
      if (!this.configuration.rum.enabled) {
        throw new Error("RUM collection is not enabled in the native configuration.");
      }
      if (typeof scenarioId !== "string" || !/^[A-Za-z0-9_.-]{1,96}$/.test(scenarioId)) {
        throw new Error("scenarioId must be a safe identifier.");
      }
      const handle = launchInteger(windowHandle, "windowHandle");
      const safeWidth = launchInteger(width, "width");
      const safeHeight = launchInteger(height, "height");
      if (handle === "0" || Number(safeWidth) < 1 || Number(safeWidth) > 10_000 ||
          Number(safeHeight) < 1 || Number(safeHeight) > 10_000) {
        throw new Error("Native scenario window information is invalid.");
      }

      const command = [
        "@guance-native-scenario",
        `scenario_id=${encodeURIComponent(scenarioId)}`,
        `window_handle=${handle}`,
        `width=${safeWidth}`,
        `height=${safeHeight}`,
      ].join("\t") + "\n";
      this.child.stdin.write(command, "utf8");
      this.accepted += 1;
      if (this.configuration.runtime.debug) {
        this.logger.log(
          `[electron-main][rum-bridge] Electron -> C++ native acceptance ` +
          `scenario_id=${scenarioId} accepted=${this.accepted}`,
        );
      }
      return true;
    } catch (error) {
      this.rejected += 1;
      this.logger.error(
        `[electron-main][rum-bridge] rejected native acceptance scenario: ${
          error instanceof Error ? error.message : "unknown error"
        }`,
      );
      return false;
    }
  }

  crashForAcceptance() {
    if (!this.child?.stdin?.writable) {
      this.rejected += 1;
      this.logger.error("[electron-main][rum-bridge] native host is unavailable");
      return false;
    }

    try {
      if (!this.configuration.runtime.debug) {
        throw new Error("Native crash acceptance is available only when debug is enabled.");
      }
      this.restartAfterCrash = true;
      this.child.stdin.write("@guance-native-crash\n", "utf8");
      this.accepted += 1;
      this.logger.log(
        `[electron-main][rum-bridge] controlled native crash accepted=${this.accepted}`,
      );
      return true;
    } catch (error) {
      this.restartAfterCrash = false;
      this.rejected += 1;
      this.logger.error(
        `[electron-main][rum-bridge] rejected controlled native crash: ${
          error instanceof Error ? error.message : "unknown error"
        }`,
      );
      return false;
    }
  }

  shutdown() {
    this.restartAfterCrash = false;
    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = undefined;
    }
    const child = this.child;
    this.child = undefined;
    if (!child) {
      return Promise.resolve();
    }

    return new Promise((resolve) => {
      let finished = false;
      const finish = () => {
        if (finished) {
          return;
        }
        finished = true;
        clearTimeout(timeout);
        resolve();
      };
      const timeout = setTimeout(() => {
        this.logger.error("[electron-main][rum-bridge] native host shutdown timed out");
        child.kill?.();
        finish();
      }, Math.max(
        2_000,
        Number(this.configuration.transport.httpTimeoutMs || 10_000) + 2_000,
      ));

      child.once?.("exit", finish);
      if (child.stdin?.writable) {
        child.stdin.end();
      } else {
        finish();
      }
    });
  }
}

module.exports = {
  NATIVE_CORE_FILE,
  NATIVE_HOST_FILE,
  NativeRumHost,
  createNativeEnvironment,
  resolveNativeRumPaths,
};
