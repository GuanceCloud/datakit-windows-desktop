"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { browserRumEventToLine } = require("./browser-rum-line-protocol.cjs");

const NATIVE_HOST_FILE = "guance_rum_electron_bridge.exe";
const NATIVE_CORE_FILE = "guance_rum_native.dll";
const MAX_INT64 = 9_223_372_036_854_775_807n;

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
    : path.resolve(sampleRoot, "..", "..", "src", "Guance.Rum.NativeCore", "bin", "win-x64");
  return {
    directory: nativeDirectory,
    executablePath: path.join(nativeDirectory, NATIVE_HOST_FILE),
    libraryPath: path.join(nativeDirectory, NATIVE_CORE_FILE),
  };
}

function createNativeEnvironment(configuration, baseEnvironment = process.env) {
  return {
    ...baseEnvironment,
    GUANCE_RUM_NATIVE_DATAWAY_URL: configuration.datawayUrl || "",
    GUANCE_RUM_NATIVE_DATAKIT_URL: configuration.datakitUrl || "",
    GUANCE_RUM_NATIVE_CLIENT_TOKEN: configuration.clientToken || "",
    GUANCE_RUM_NATIVE_APP_ID: configuration.applicationId || "",
    GUANCE_RUM_NATIVE_SERVICE: configuration.service || "",
    GUANCE_RUM_NATIVE_ENV: configuration.env || "",
    GUANCE_RUM_NATIVE_VERSION: configuration.version || "",
    GUANCE_RUM_NATIVE_CACHE_PATH: configuration.cachePath || "",
    GUANCE_RUM_NATIVE_PROXY_URL: configuration.proxyUrl || "",
    GUANCE_RUM_NATIVE_SAMPLE_RATE: String(configuration.sampleRate ?? 1),
    GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS: String(configuration.httpTimeoutMs ?? 10_000),
    GUANCE_RUM_NATIVE_DEBUG: configuration.debug ? "1" : "0",
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
      this.logger.log(
        `[electron-main][rum-bridge] native host exited code=${code} signal=${signal || "none"}`,
      );
      if (this.child === child) {
        this.child = undefined;
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
      const payload = browserRumEventToLine(serializedEvent, this.trustedContext);
      this.child.stdin.write(payload.line, "utf8");
      this.accepted += 1;
      if (this.configuration.debug) {
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
      if (this.configuration.debug) {
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

  shutdown() {
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
      }, Math.max(2_000, Number(this.configuration.httpTimeoutMs || 10_000) + 2_000));

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
