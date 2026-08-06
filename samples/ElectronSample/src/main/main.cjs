"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  Notification,
  session,
} = require("electron");
const {
  createLocalRumSettingsReader,
  isSupportedWebViewUrl,
  loadLocalRumSettings,
  resolveAllowedTraceUrls,
  resolveRumIngestionConfiguration,
  resolveWebViewUrl,
} = require("./local-rum-settings.cjs");
const RUM_BRIDGE_CHANNEL = "rum:browser-event";
const SUPPORTED_BROWSER_TRACE_TYPES = new Set([
  "ddtrace",
  "zipkin",
  "zipkin_single_header",
  "w3c_traceparent",
  "w3c_traceparent_64bit",
  "skywalking_v3",
  "jaeger",
]);
const {
  NativeRumHost,
  resolveNativeRumPaths,
} = require("./native-rum-host.cjs");
const {
  initializeElectronMonitoring,
} = require("./electron-monitoring.cjs");
const {
  ElectronApplicationLaunchTracker,
} = require("./electron-application-launch.cjs");
const { monitorElectronWindow } = require("./electron-process-monitor.cjs");

const DEV_RENDERER_URL = process.env.ELECTRON_RENDERER_URL;
const IS_SMOKE = process.env.ELECTRON_SMOKE === "1";
const SAMPLE_ROOT = path.resolve(__dirname, "..", "..");
const DIST_ROOT = path.join(SAMPLE_ROOT, "dist");
const ACCEPTANCE_USER_ID = `desktop-${crypto.randomUUID()}`;
const NATIVE_SESSION_ID = crypto.randomUUID().replaceAll("-", "");
const SMOKE_TIMEOUT_MS = 15_000;
const applicationLaunch = new ElectronApplicationLaunchTracker();

if (IS_SMOKE) {
  app.setPath("userData", path.join(os.tmpdir(), `guance-electron-smoke-${process.pid}`));
  app.disableHardwareAcceleration();
}

let mainWindow;
let remoteWindow;
let apiServer;
let apiBaseUrl;
let nativeRumHost;
let electronMonitoring;
let monitoringConfiguration;
let nativeRumShutdownPromise;
let launchLifecycleInstalled = false;
let browserTraceHeaderObserved = false;
let localRumSettingsReader = createLocalRumSettingsReader({
  environment: process.env,
  settings: {},
});

function initializeLocalRumSettings() {
  const local = IS_SMOKE
    ? { filePath: undefined, settings: {} }
    : loadLocalRumSettings({
        currentDirectory: process.cwd(),
        appPath: app.getAppPath(),
        executablePath: process.execPath,
        isPackaged: app.isPackaged,
      });
  localRumSettingsReader = createLocalRumSettingsReader({
    environment: process.env,
    settings: local.settings,
  });
  if (local.filePath) {
    console.log(`[electron-main] loaded local RUM settings from ${local.filePath}`);
  }
}

function readConfiguration(environmentName, jsonName, fallback = "") {
  return localRumSettingsReader.readString(environmentName, jsonName, fallback);
}

function readBooleanConfiguration(environmentName, jsonName, fallback = false) {
  return localRumSettingsReader.readBoolean(environmentName, jsonName, fallback);
}

function readNumberConfiguration(environmentName, jsonName, fallback) {
  return localRumSettingsReader.readNumber(environmentName, jsonName, fallback);
}

function readPercentageConfiguration(environmentName, jsonName, fallback) {
  return Math.min(
    1,
    Math.max(0, readNumberConfiguration(environmentName, jsonName, fallback) / 100),
  );
}

function readBrowserPercentageConfiguration(environmentName, jsonName, fallback) {
  return Math.min(
    100,
    Math.max(0, readNumberConfiguration(environmentName, jsonName, fallback)),
  );
}

function resolveBrowserTraceType() {
  const configured = readConfiguration(
    "GUANCE_TRACE_TYPE",
    "traceType",
    "w3c_traceparent",
  ).toLowerCase();
  return SUPPORTED_BROWSER_TRACE_TYPES.has(configured)
    ? configured
    : "w3c_traceparent";
}

function resolveReplayPrivacyLevel() {
  const configured = readConfiguration(
    "GUANCE_RUM_REPLAY_PRIVACY_LEVEL",
    "replayPrivacyLevel",
    readConfiguration(
      "GUANCE_RUM_REPLAY_TEXT_AND_INPUT_PRIVACY",
      "replayTextAndInputPrivacy",
      "MaskAll",
    ),
  ).toLowerCase();
  if (configured === "allow") {
    return "allow";
  }
  if (configured === "mask-user-input" || configured === "masksensitiveinputs" ||
      configured === "maskallinputs") {
    return "mask-user-input";
  }
  return "mask";
}

function createMonitoringConfiguration() {
  const { datawayUrl, datakitUrl } = resolveRumIngestionConfiguration(
    localRumSettingsReader,
    IS_SMOKE ? "http://127.0.0.1:9" : "http://127.0.0.1:9529",
  );
  const applicationId = readConfiguration(
    "GUANCE_RUM_APP_ID",
    "rumAppId",
    IS_SMOKE ? "electron-smoke" : "",
  );
  const service = readConfiguration(
    "GUANCE_RUM_SERVICE_NAME",
    "serviceName",
    "guance-rum-windows-electron",
  );
  const environment = readConfiguration("GUANCE_RUM_ENV", "env", "local");
  const version = readConfiguration("GUANCE_RUM_VERSION", "version", app.getVersion());
  const sampleRate = readPercentageConfiguration(
    "GUANCE_RUM_SAMPLE_RATE",
    "sessionSampleRate",
    100,
  );
  const rumEnabled = readBooleanConfiguration(
    "GUANCE_RUM_ENABLED",
    "rumEnabled",
    true,
  );
  const sessionReplayEnabled = readBooleanConfiguration(
    "GUANCE_RUM_SESSION_REPLAY_ENABLED",
    "sessionReplayEnabled",
    IS_SMOKE,
  );
  const loggingEnabled = readBooleanConfiguration(
    "GUANCE_LOG_ENABLED",
    "loggingEnabled",
    IS_SMOKE,
  );
  const traceEnabled = readBooleanConfiguration(
    "GUANCE_TRACE_ENABLED",
    "traceEnabled",
    IS_SMOKE,
  );
  const sessionReplaySampleRate = readPercentageConfiguration(
    "GUANCE_RUM_SESSION_REPLAY_SAMPLE_RATE",
    "sessionReplaySampleRate",
    100,
  );
  const sessionReplayOnErrorSampleRate = readPercentageConfiguration(
    "GUANCE_RUM_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE",
    "sessionReplayOnErrorSampleRate",
    0,
  );

  return {
    transport: {
      datawayUrl,
      datakitUrl,
      clientToken: readConfiguration("GUANCE_RUM_CLIENT_TOKEN", "clientToken"),
      proxyUrl: readConfiguration("GUANCE_RUM_PROXY_URL", "proxyUrl"),
      httpTimeoutMs: 10_000,
    },
    application: {
      id: applicationId,
      service,
      env: environment,
      version,
    },
    runtime: {
      cachePath: path.join(app.getPath("userData"), "native-rum-cache"),
      debug: readBooleanConfiguration("GUANCE_RUM_DEBUG", "debug", true),
    },
    cache: {
      maxBytes: readNumberConfiguration("GUANCE_RUM_MAX_CACHE_BYTES", "maxCacheBytes", 128 * 1024 * 1024),
      maxFiles: readNumberConfiguration("GUANCE_RUM_MAX_CACHE_FILES", "maxCacheFiles", 1024),
      maxAgeSeconds: readNumberConfiguration("GUANCE_RUM_MAX_CACHE_AGE_SECONDS", "maxCacheAgeSeconds", 7 * 24 * 60 * 60),
      maxBatchItems: readNumberConfiguration("GUANCE_RUM_MAX_BATCH_ITEMS", "maxBatchItems", 50),
      maxBatchBytes: readNumberConfiguration("GUANCE_RUM_MAX_BATCH_BYTES", "maxBatchBytes", 512 * 1024),
    },
    upload: {
      maxBytesPerSecond: readNumberConfiguration("GUANCE_RUM_MAX_UPLOAD_BYTES_PER_SECOND", "maxUploadBytesPerSecond", 256 * 1024),
      burstBytes: readNumberConfiguration("GUANCE_RUM_UPLOAD_BURST_BYTES", "uploadBurstBytes", 2 * 1024 * 1024),
      maxRequestsPerSecond: readNumberConfiguration("GUANCE_RUM_MAX_UPLOAD_REQUESTS_PER_SECOND", "maxUploadRequestsPerSecond", 2),
      maxBatchesPerCycle: readNumberConfiguration("GUANCE_RUM_MAX_UPLOAD_BATCHES_PER_CYCLE", "maxUploadBatchesPerCycle", 4),
    },
    rum: {
      enabled: rumEnabled,
      sampleRate,
      sessionReplay: {
        enabled: sessionReplayEnabled,
        sampleRate: sessionReplaySampleRate,
        onErrorSampleRate: sessionReplayOnErrorSampleRate,
        privacyLevel: resolveReplayPrivacyLevel(),
      },
    },
    log: {
      enabled: loggingEnabled,
      sampleRate: readPercentageConfiguration(
        "GUANCE_LOG_SAMPLE_RATE",
        "logSampleRate",
        100,
      ),
    },
    trace: {
      enabled: traceEnabled,
      sampleRate: readBrowserPercentageConfiguration(
        "GUANCE_TRACE_SAMPLE_RATE",
        "traceSampleRate",
        100,
      ),
      type: resolveBrowserTraceType(),
      allowedUrls: resolveAllowedTraceUrls(localRumSettingsReader),
    },
    electron: {
      defaultPage: { enabled: true },
      pages: {},
    },
    trustedContext: {
      tags: {
        app_id: applicationId,
        service,
        env: environment,
        version,
        sdk_name: "df_windows_rum_sdk",
        sdk_version: app.getVersion(),
        session_id: NATIVE_SESSION_ID,
        is_electron: "true",
        is_signin: "true",
        userid: ACCEPTANCE_USER_ID,
        user_name: "Electron acceptance operator",
      },
      fields: {
        session_has_replay: sessionReplayEnabled && sessionReplaySampleRate > 0,
        session_sample_rate: sampleRate,
        session_on_error_sample_rate: 0,
      },
    },
  };
}

function initializeNativeRumHost() {
  const configuration = createMonitoringConfiguration();
  monitoringConfiguration = configuration;
  if (
    !configuration.application.id ||
    (!configuration.transport.datawayUrl && !configuration.transport.datakitUrl)
  ) {
    console.warn("[electron-main][rum-bridge] disabled: missing ingestion URL or app id");
    return;
  }

  const paths = resolveNativeRumPaths({
    isPackaged: app.isPackaged,
    resourcesPath: process.resourcesPath,
    sampleRoot: SAMPLE_ROOT,
  });
  electronMonitoring = initializeElectronMonitoring({
    configuration,
    createNativeBridge: (nativeConfiguration) => new NativeRumHost({
      paths,
      configuration: nativeConfiguration,
      trustedContext: nativeConfiguration.trustedContext,
    }),
  });
  nativeRumHost = electronMonitoring.nativeBridge;
  applicationLaunch.markSdkInitialized();
  console.log(
    `[electron-main][rum-bridge] Browser RUM -> C++ Core enabled (${paths.executablePath})`,
  );
}

function installLaunchLifecycle() {
  if (launchLifecycleInstalled) {
    return;
  }
  launchLifecycleInstalled = true;

  app.on("browser-window-blur", () => {
    setImmediate(() => {
      if (!BrowserWindow.getFocusedWindow()) {
        applicationLaunch.enterBackground();
      }
    });
  });
  app.on("browser-window-focus", (_event, window) => {
    const foreground = applicationLaunch.beginForeground();
    if (!foreground) {
      return;
    }

    void applicationLaunch.reportHotAfterFrame(electronMonitoring, window, foreground);
  });
}

function shutdownNativeRumHost() {
  if (nativeRumShutdownPromise) {
    return nativeRumShutdownPromise;
  }
  const host = nativeRumHost;
  nativeRumHost = undefined;
  const integration = electronMonitoring;
  electronMonitoring = undefined;
  nativeRumShutdownPromise = integration?.shutdown() || host?.shutdown() || Promise.resolve();
  return nativeRumShutdownPromise;
}

function createBootstrap(pageId, isRemoteRenderer = false) {
  const pagePolicy = electronMonitoring?.pagePolicy(pageId);
  const configuredTracingUrls = pagePolicy?.trace.allowedUrls || [];
  const allowedTracingUrls = pagePolicy?.trace.enabled
    ? [...new Set([...configuredTracingUrls, apiBaseUrl].filter(Boolean))]
    : [];
  return {
    app: {
      name: app.getName(),
      version: app.getVersion(),
      platform: process.platform,
      arch: process.arch,
      osVersion: os.release(),
      electronVersion: process.versions.electron,
      chromiumVersion: process.versions.chrome,
      nodeVersion: process.versions.node,
      packaged: app.isPackaged,
      rendererMode: isRemoteRenderer
        ? "remote-http"
        : DEV_RENDERER_URL
          ? "vite-dev-server"
          : "packaged-file",
    },
    monitoring: {
      bridgeEnabled: Boolean(electronMonitoring && pagePolicy?.enabled),
      debug: monitoringConfiguration?.runtime.debug ?? false,
      userId: ACCEPTANCE_USER_ID,
      rum: {
        enabled: Boolean(pagePolicy?.rum.enabled),
        sessionReplay: {
          enabled: Boolean(pagePolicy?.rum.sessionReplay.enabled),
          privacyLevel: pagePolicy?.rum.sessionReplay.privacyLevel || "mask",
        },
      },
      log: {
        enabled: Boolean(pagePolicy?.log.enabled),
      },
      trace: {
        enabled: Boolean(pagePolicy?.trace.enabled),
        sampleRate: pagePolicy?.trace.sampleRate ?? 100,
        type: pagePolicy?.trace.type || "w3c_traceparent",
        allowedUrls: allowedTracingUrls,
      },
    },
    hybrid: {
      localOrigin: isRemoteRenderer ? apiBaseUrl : DEV_RENDERER_URL || "file://",
      remoteUrl: readConfiguration(
        "GUANCE_RUM_ELECTRON_REMOTE_URL",
        "electronRemoteUrl",
        resolveWebViewUrl(localRumSettingsReader),
      ),
      remoteAvailable: true,
      isRemoteRenderer,
      apiBaseUrl,
    },
  };
}

function createRumPreloadArguments(pageId, additionalArguments = []) {
  return electronMonitoring?.createPreloadArguments(pageId, additionalArguments) || [
    ...additionalArguments,
    "--guance-monitoring-disabled",
  ];
}

function sendJson(response, statusCode, value) {
  const body = JSON.stringify(value);
  response.writeHead(statusCode, {
    "Access-Control-Allow-Origin": "*",
    "Cache-Control": "no-store",
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
  });
  response.end(body);
}

function sendFile(response, filePath, contentType, cacheControl = "no-store") {
  fs.readFile(filePath, (error, content) => {
    if (error) {
      sendJson(response, error.code === "ENOENT" ? 404 : 500, {
        code: error.code === "ENOENT" ? "NOT_FOUND" : "READ_FAILED",
      });
      return;
    }
    response.writeHead(200, {
      "Cache-Control": cacheControl,
      "Content-Type": contentType,
      "Content-Length": content.length,
    });
    response.end(content);
  });
}

function tryServeRemoteRenderer(requestUrl, response) {
  if (requestUrl.pathname === "/remote" || requestUrl.pathname === "/remote/") {
    sendFile(response, path.join(DIST_ROOT, "index.html"), "text/html; charset=utf-8");
    return true;
  }

  if (requestUrl.pathname === "/remote/bootstrap") {
    sendJson(response, 200, createBootstrap("remote", true));
    return true;
  }

  if (!requestUrl.pathname.startsWith("/remote/assets/")) {
    return false;
  }

  const relativePath = requestUrl.pathname.slice("/remote/".length);
  const resolvedPath = path.resolve(DIST_ROOT, relativePath);
  const allowedRoot = `${path.resolve(DIST_ROOT)}${path.sep}`;
  if (!resolvedPath.startsWith(allowedRoot)) {
    sendJson(response, 403, { code: "FORBIDDEN" });
    return true;
  }

  const extension = path.extname(resolvedPath);
  const contentTypes = {
    ".css": "text/css; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".map": "application/json; charset=utf-8",
  };
  sendFile(
    response,
    resolvedPath,
    contentTypes[extension] || "application/octet-stream",
    "public, max-age=31536000, immutable",
  );
  return true;
}

function startLocalApi() {
  return new Promise((resolve, reject) => {
    apiServer = http.createServer((request, response) => {
      if (request.method === "OPTIONS") {
        response.writeHead(204, {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "GET, OPTIONS",
          "Access-Control-Allow-Headers": [
            "Content-Type",
            "traceparent",
            "tracestate",
            "x-datadog-origin",
            "x-datadog-parent-id",
            "x-datadog-sampling-priority",
            "x-datadog-trace-id",
            "X-B3-TraceId",
            "X-B3-SpanId",
            "X-B3-Sampled",
            "b3",
            "uber-trace-id",
            "sw8",
          ].join(", "),
        });
        response.end();
        return;
      }

      const requestUrl = new URL(request.url || "/", "http://127.0.0.1");
      if (
        requestUrl.pathname.startsWith("/api/") &&
        [
          "traceparent",
          "x-datadog-trace-id",
          "x-b3-traceid",
          "b3",
          "uber-trace-id",
          "sw8",
        ].some((header) => typeof request.headers[header] === "string")
      ) {
        browserTraceHeaderObserved = true;
      }
      if (tryServeRemoteRenderer(requestUrl, response)) {
        return;
      }

      if (requestUrl.pathname === "/health") {
        sendJson(response, 200, { status: "ready", source: "electron-main" });
        return;
      }

      if (requestUrl.pathname === "/api/orders") {
        setTimeout(() => {
          sendJson(response, 200, {
            batch: "CN-EAST-2407",
            processed: 1284,
            delayed: 17,
            generatedAt: new Date().toISOString(),
          });
        }, 180);
        return;
      }

      if (requestUrl.pathname === "/api/failure") {
        setTimeout(() => {
          sendJson(response, 503, {
            code: "UPSTREAM_DEGRADED",
            message: "Synthetic acceptance failure",
          });
        }, 260);
        return;
      }

      sendJson(response, 404, { code: "NOT_FOUND" });
    });

    apiServer.once("error", reject);
    apiServer.listen(0, "127.0.0.1", () => {
      const address = apiServer.address();
      if (!address || typeof address === "string") {
        reject(new Error("Unable to resolve the local acceptance API address."));
        return;
      }
      apiBaseUrl = `http://127.0.0.1:${address.port}`;
      resolve();
    });
  });
}

function isAllowedDevelopmentRendererUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" && ["127.0.0.1", "localhost"].includes(url.hostname);
  } catch {
    return false;
  }
}

function isAllowedRemoteUrl(value) {
  return isSupportedWebViewUrl(value);
}

function isAllowedNavigation(candidate, allowedEntry) {
  try {
    const destination = new URL(candidate);
    const allowed = new URL(allowedEntry);
    if (allowed.protocol === "file:") {
      return destination.protocol === "file:" && destination.pathname === allowed.pathname;
    }
    return destination.origin === allowed.origin;
  } catch {
    return false;
  }
}

function secureWebContents(webContents, allowedEntry) {
  webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  for (const eventName of ["will-navigate", "will-redirect"]) {
    webContents.on(eventName, (event, destination) => {
      if (!isAllowedNavigation(destination, allowedEntry)) {
        event.preventDefault();
      }
    });
  }
}

function isTrustedMainEvent(event) {
  return Boolean(
    mainWindow &&
    !mainWindow.isDestroyed() &&
    event.sender === mainWindow.webContents &&
    event.senderFrame === mainWindow.webContents.mainFrame,
  );
}

function handleTrusted(channel, handler) {
  ipcMain.handle(channel, (event, ...args) => {
    if (!isTrustedMainEvent(event)) {
      throw new Error(`Rejected untrusted IPC invocation: ${channel}`);
    }
    return handler(...args);
  });
}

function onTrusted(channel, handler) {
  ipcMain.on(channel, (event, ...args) => {
    if (isTrustedMainEvent(event)) {
      handler(...args);
    }
  });
}

async function createRemoteWindow({ forceBuiltIn = false } = {}) {
  const configuredUrl = resolveWebViewUrl(localRumSettingsReader);
  const remoteUrl = !forceBuiltIn && configuredUrl ? configuredUrl : `${apiBaseUrl}/remote/`;
  if (!isAllowedRemoteUrl(remoteUrl)) {
    return { opened: false, reason: "Only HTTP or HTTPS WebView URLs are allowed." };
  }

  if (remoteWindow && !remoteWindow.isDestroyed()) {
    remoteWindow.focus();
    return { opened: true, reused: true, instrumentation: configuredUrl ? "external" : "built-in" };
  }

  remoteWindow = new BrowserWindow({
    width: 1100,
    height: 760,
    minWidth: 760,
    minHeight: 520,
    show: false,
    title: "Hybrid Remote Workspace",
    backgroundColor: "#07110f",
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      additionalArguments: createRumPreloadArguments(
        "remote",
        ["--guance-rum-only-preload"],
      ),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  electronMonitoring?.registerWebContents(
    remoteWindow.webContents,
    "remote",
    "remote-renderer",
  );
  remoteWindow.removeMenu();
  secureWebContents(remoteWindow.webContents, remoteUrl);
  monitorElectronWindow(remoteWindow, {
    label: "remote-renderer",
    sendProcessFailure: (failure) => electronMonitoring?.sendProcessFailure(failure),
  });
  remoteWindow.once("ready-to-show", () => remoteWindow?.show());
  remoteWindow.on("closed", () => {
    remoteWindow = undefined;
  });

  try {
    await remoteWindow.loadURL(remoteUrl);
    return {
      opened: true,
      reused: false,
      instrumentation: !forceBuiltIn && configuredUrl ? "external" : "built-in",
    };
  } catch (error) {
    remoteWindow.destroy();
    remoteWindow = undefined;
    return {
      opened: false,
      reason: error instanceof Error ? error.message : "Remote renderer failed to load.",
    };
  }
}

function registerIpc() {
  ipcMain.on(RUM_BRIDGE_CHANNEL, (event, serializedEvent) => {
    electronMonitoring?.send(event, serializedEvent);
  });
  handleTrusted("app:get-bootstrap", () => createBootstrap("main", false));
  onTrusted("window:minimize", () => mainWindow?.minimize());
  handleTrusted("window:toggle-maximize", () => {
    if (!mainWindow) {
      return false;
    }
    if (mainWindow.isMaximized()) {
      mainWindow.unmaximize();
      return false;
    }
    mainWindow.maximize();
    return true;
  });
  onTrusted("window:close", () => mainWindow?.close());
  handleTrusted("native:select-workspace", async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
      title: "Select hybrid workspace",
      properties: ["openDirectory"],
    });
    if (result.canceled || result.filePaths.length === 0) {
      return null;
    }
    return { name: path.basename(result.filePaths[0]) };
  });
  handleTrusted("hybrid:open-remote", () => createRemoteWindow());
  handleTrusted("native:show-notification", (message) => {
    if (IS_SMOKE) {
      return false;
    }
    const safeMessage = typeof message === "string"
      ? message.slice(0, 160)
      : "RUM acceptance event emitted.";
    if (Notification.isSupported()) {
      new Notification({
        title: "Guance Desktop",
        body: safeMessage,
      }).show();
      return true;
    }
    return false;
  });
}

function configureSmoke(window) {
  let finished = false;
  const finish = (exitCode, message) => {
    if (finished) {
      return;
    }
    finished = true;
    clearTimeout(timeout);
    if (message) {
      const log = exitCode === 0 ? console.log : console.error;
      log(message);
    }
    process.exitCode = exitCode;
    void shutdownNativeRumHost().finally(() => app.exit(exitCode));
  };
  const timeout = setTimeout(() => {
    finish(1, `[electron-smoke] timed out after ${SMOKE_TIMEOUT_MS}ms`);
  }, SMOKE_TIMEOUT_MS);

  window.webContents.once("did-fail-load", (_event, code, description, _url, isMainFrame) => {
    if (isMainFrame) {
      finish(1, `[electron-smoke] load failed (${code}): ${description}`);
    }
  });
  window.webContents.once("render-process-gone", (_event, details) => {
    finish(1, `[electron-smoke] renderer terminated: ${details.reason}`);
  });
  window.webContents.once("did-finish-load", async () => {
    try {
      const localResult = await window.webContents.executeJavaScript(
        `(async () => {
          const waitFor = async (selector) => {
            for (let attempt = 0; attempt < 50; attempt += 1) {
              const element = document.querySelector(selector)
              if (element) return element
              await new Promise((resolve) => setTimeout(resolve, 100))
            }
            throw new Error('Timed out waiting for ' + selector)
          }
          const waitUntil = async (predicate, description) => {
            for (let attempt = 0; attempt < 50; attempt += 1) {
              if (predicate()) return
              await new Promise((resolve) => setTimeout(resolve, 100))
            }
            throw new Error('Timed out waiting for ' + description)
          }
          await waitFor('[data-testid="rum-status"]')
          await waitUntil(
            () => document.querySelector('#app')?.dataset.rumInitialized === 'true',
            'local Browser RUM initialization'
          )
          document.querySelector('[data-route="sessions"]').click()
          document.querySelector('#run-success').click()
          await waitUntil(
            () => document.querySelector('#resource-delta')?.textContent === 'HTTP 200',
            'successful Resource'
          )
          document.querySelector('#run-failure').click()
          await waitUntil(
            () => document.querySelector('#resource-delta')?.textContent === 'HTTP 503',
            'failed Resource'
          )
          document.querySelector('#emit-error').click()
          document.querySelector('#block-thread').click()
          await waitUntil(() => {
            const app = document.querySelector('#app')
            const types = (app?.dataset.rumSdkEventTypes || '').split(',')
            return (
              document.querySelectorAll('.coverage-row.verified').length >= 5 &&
              ['view', 'action', 'resource', 'error', 'long_task'].every((type) => types.includes(type)) &&
              Number(app?.dataset.rumSdkResourceCount || '0') >= 2
            )
          }, 'five SDK events and two Resource events')
          const replayPlayground = await waitFor('#replay-playground')
          const replayFixtures = Array.from(
            replayPlayground.querySelectorAll('[data-replay-fixture]')
          )
          const replayFixtureKinds = [
            ...new Set(replayFixtures.map((element) => element.dataset.replayFixture))
          ]
          const requiredReplayFixtureKinds = [
            'input',
            'privacy',
            'selection',
            'range',
            'toggle',
            'dynamic',
            'overlay',
            'drag',
            'scroll',
            'canvas'
          ]
          if (
            replayFixtures.length < 18 ||
            !requiredReplayFixtureKinds.every((kind) => replayFixtureKinds.includes(kind))
          ) {
            throw new Error('Replay fixture coverage is incomplete')
          }
          const nameInput = document.querySelector('#replay-name')
          nameInput.value = 'Replay operator'
          nameInput.dispatchEvent(new Event('input', { bubbles: true }))
          const rangeInput = document.querySelector('#replay-range')
          rangeInput.value = '74'
          rangeInput.dispatchEvent(new Event('input', { bubbles: true }))
          document.querySelector('#replay-toggle').click()
          document.querySelector('#replay-add-row').click()
          document.querySelector('#replay-dialog-open').click()
          const replayDialog = document.querySelector('#replay-dialog')
          const modalOpened = replayDialog.open === true
          document.querySelector('#replay-dialog-close').click()
          await waitUntil(
            () =>
              document.querySelectorAll('#replay-dynamic-list .replay-dynamic-row').length >= 4 &&
              document.querySelector('#replay-range-output')?.textContent === '74%' &&
              document.querySelector('#replay-toggle')?.getAttribute('aria-pressed') === 'true',
            'Replay fixture interactions'
          )
          const app = document.querySelector('#app')
          return {
            title: document.title,
            status: document.querySelector('[data-testid="rum-status"]')?.textContent,
            cards: document.querySelectorAll('.metric-card').length,
            resource: document.querySelector('#metric-resource')?.textContent,
            resourceStatus: document.querySelector('#resource-delta')?.textContent,
            rumInitialized: app?.dataset.rumInitialized === 'true',
            logInitialized: app?.dataset.logInitialized === 'true',
            traceEnabled: app?.dataset.traceEnabled === 'true',
            sdkEventTypes: (app?.dataset.rumSdkEventTypes || '').split(',').filter(Boolean),
            sdkResourceCount: Number(app?.dataset.rumSdkResourceCount || '0'),
            verifiedSignals: document.querySelectorAll('.coverage-row.verified').length,
            verifiedTypes: Array.from(document.querySelectorAll('.coverage-row.verified'))
              .map((element) => element.dataset.coverage),
            replayFixtures: replayFixtures.length,
            replayFixtureKinds,
            replayDynamicRows: document.querySelectorAll(
              '#replay-dynamic-list .replay-dynamic-row'
            ).length,
            replayModalOpened: modalOpened,
            replayRecording: app?.dataset.replayRecording === 'true',
            acceptanceUserId: app?.dataset.acceptanceUserId
          }
        })()`,
        true,
      );

      const remoteOpenResult = await createRemoteWindow({ forceBuiltIn: true });
      if (!remoteOpenResult.opened || !remoteWindow) {
        throw new Error(remoteOpenResult.reason || "Built-in remote renderer did not open.");
      }
      const remoteResult = await remoteWindow.webContents.executeJavaScript(
        `(async () => {
          for (let attempt = 0; attempt < 50; attempt += 1) {
            if (
              document.querySelector('[data-testid="rum-status"]') &&
              document.body.classList.contains('remote-renderer') &&
              document.querySelector('#app')?.dataset.rumInitialized === 'true' &&
              (document.querySelector('#app')?.dataset.rumSdkEventTypes || '').split(',').includes('view')
            ) {
              const app = document.querySelector('#app')
              return {
                title: document.title,
                status: document.querySelector('[data-testid="rum-status"]')?.textContent,
                remoteMode: true,
                rumInitialized: true,
                sdkEventTypes: (app?.dataset.rumSdkEventTypes || '').split(',').filter(Boolean),
                hasPreloadBridge: Boolean(window.guanceDesktop),
                acceptanceUserId: app?.dataset.acceptanceUserId
              }
            }
            await new Promise((resolve) => setTimeout(resolve, 100))
          }
          throw new Error('Timed out waiting for the remote renderer')
        })()`,
        true,
      );

      const userCorrelation =
        Boolean(localResult.acceptanceUserId) &&
        localResult.acceptanceUserId === remoteResult.acceptanceUserId;
      const result = {
        local: { ...localResult, acceptanceUserId: undefined },
        remote: { ...remoteResult, acceptanceUserId: undefined },
        traceHeaderObserved: browserTraceHeaderObserved,
        userCorrelation,
      };
      const passed =
        localResult.title === "Guance Desktop Control Room" &&
        localResult.cards >= 4 &&
        localResult.resource !== "\u2014" &&
        localResult.resourceStatus === "HTTP 503" &&
        localResult.rumInitialized === true &&
        localResult.logInitialized === true &&
        localResult.traceEnabled === true &&
        browserTraceHeaderObserved === true &&
        localResult.sdkResourceCount >= 2 &&
        ["view", "action", "resource", "error", "long_task"].every((type) =>
          localResult.sdkEventTypes.includes(type),
        ) &&
        localResult.verifiedSignals >= 5 &&
        localResult.replayFixtures >= 18 &&
        localResult.replayDynamicRows >= 4 &&
        localResult.replayModalOpened === true &&
        localResult.replayRecording === true &&
        remoteResult.title === "Guance Desktop Control Room" &&
        remoteResult.remoteMode === true &&
        remoteResult.rumInitialized === true &&
        remoteResult.sdkEventTypes.includes("view") &&
        remoteResult.hasPreloadBridge === false &&
        userCorrelation;
      finish(passed ? 0 : 1, `[electron-smoke] ${JSON.stringify(result)}`);
    } catch (error) {
      finish(1, `[electron-smoke] failed: ${error instanceof Error ? error.stack : error}`);
    }
  });
}

function createMainWindow() {
  const packagedEntry = pathToFileURL(path.join(DIST_ROOT, "index.html")).href;
  const rendererEntry = DEV_RENDERER_URL || packagedEntry;
  if (DEV_RENDERER_URL && !isAllowedDevelopmentRendererUrl(DEV_RENDERER_URL)) {
    throw new Error("ELECTRON_RENDERER_URL must use loopback HTTP.");
  }

  mainWindow = new BrowserWindow({
    width: 1480,
    height: 920,
    minWidth: 1080,
    minHeight: 700,
    show: false,
    frame: false,
    backgroundColor: "#07110f",
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      additionalArguments: createRumPreloadArguments("main"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  electronMonitoring?.registerWebContents(
    mainWindow.webContents,
    "main",
    "main-renderer",
  );
  const windowCreated = applicationLaunch.markWindowCreated();

  mainWindow.removeMenu();
  secureWebContents(mainWindow.webContents, rendererEntry);
  monitorElectronWindow(mainWindow, {
    label: "main-renderer",
    sendProcessFailure: (failure) => electronMonitoring?.sendProcessFailure(failure),
  });
  mainWindow.once("ready-to-show", () => {
    applicationLaunch.reportCold(electronMonitoring, windowCreated);
    mainWindow?.show();
  });
  mainWindow.on("closed", () => {
    mainWindow = undefined;
  });

  if (IS_SMOKE) {
    configureSmoke(mainWindow);
  }
  void mainWindow.loadURL(rendererEntry);

  if (readBooleanConfiguration(
    "GUANCE_RUM_ELECTRON_DEVTOOLS",
    "electronDevTools",
  )) {
    mainWindow.webContents.openDevTools({ mode: "detach" });
  }
}

app.whenReady().then(async () => {
  initializeLocalRumSettings();
  initializeNativeRumHost();
  installLaunchLifecycle();
  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => {
    callback(false);
  });
  await startLocalApi();
  registerIpc();
  createMainWindow();

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createMainWindow();
    }
  });
}).catch((error) => {
  console.error("[electron-main] startup failed", error);
  process.exitCode = 1;
  app.quit();
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});

app.on("before-quit", (event) => {
  if (nativeRumHost) {
    event.preventDefault();
    void shutdownNativeRumHost().finally(() => app.quit());
  }
  if (apiServer) {
    apiServer.close();
    apiServer = undefined;
  }
});
