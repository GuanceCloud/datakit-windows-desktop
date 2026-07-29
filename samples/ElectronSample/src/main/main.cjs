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

const DEV_RENDERER_URL = process.env.ELECTRON_RENDERER_URL;
const IS_SMOKE = process.env.ELECTRON_SMOKE === "1";
const SAMPLE_ROOT = path.resolve(__dirname, "..", "..");
const DIST_ROOT = path.join(SAMPLE_ROOT, "dist");
const ACCEPTANCE_USER_ID = `desktop-${crypto.randomUUID()}`;
const SMOKE_TIMEOUT_MS = 15_000;

if (IS_SMOKE) {
  app.setPath("userData", path.join(os.tmpdir(), `guance-electron-smoke-${process.pid}`));
  app.disableHardwareAcceleration();
}

let mainWindow;
let remoteWindow;
let apiServer;
let apiBaseUrl;

function readEnvironment(name, fallback = "") {
  const value = process.env[name];
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : fallback;
}

function readBooleanEnvironment(name, fallback = false) {
  const value = readEnvironment(name).toLowerCase();
  if (value === "true" || value === "1") {
    return true;
  }
  if (value === "false" || value === "0") {
    return false;
  }
  return fallback;
}

function createBootstrap(isRemoteRenderer = false) {
  const datawayUrl = readEnvironment("GUANCE_RUM_DATAWAY_URL");
  const datakitUrl = readEnvironment(
    "GUANCE_RUM_DATAKIT_URL",
    IS_SMOKE ? "http://127.0.0.1:9" : "http://127.0.0.1:9529",
  );

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
    rum: {
      applicationId: readEnvironment("GUANCE_RUM_APP_ID", IS_SMOKE ? "electron-smoke" : ""),
      clientToken: readEnvironment("GUANCE_RUM_CLIENT_TOKEN"),
      site: datawayUrl,
      datakitOrigin: datawayUrl ? "" : datakitUrl,
      service: readEnvironment("GUANCE_RUM_SERVICE_NAME", "guance-rum-windows-electron"),
      env: readEnvironment("GUANCE_RUM_ENV", "local"),
      version: readEnvironment("GUANCE_RUM_VERSION", app.getVersion()),
      debug: readBooleanEnvironment("GUANCE_RUM_DEBUG", true),
      sessionSampleRate: Number(readEnvironment("GUANCE_RUM_SAMPLE_RATE", "100")),
      userId: ACCEPTANCE_USER_ID,
    },
    hybrid: {
      localOrigin: isRemoteRenderer ? apiBaseUrl : DEV_RENDERER_URL || "file://",
      remoteUrl: readEnvironment("GUANCE_RUM_ELECTRON_REMOTE_URL"),
      remoteAvailable: true,
      isRemoteRenderer,
      apiBaseUrl,
    },
  };
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
    sendJson(response, 200, createBootstrap(true));
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
          "Access-Control-Allow-Headers": "Content-Type",
        });
        response.end();
        return;
      }

      const requestUrl = new URL(request.url || "/", "http://127.0.0.1");
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
  try {
    const url = new URL(value);
    return url.protocol === "https:" ||
      (url.protocol === "http:" && ["127.0.0.1", "localhost"].includes(url.hostname));
  } catch {
    return false;
  }
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
  const configuredUrl = readEnvironment("GUANCE_RUM_ELECTRON_REMOTE_URL");
  const remoteUrl = !forceBuiltIn && configuredUrl ? configuredUrl : `${apiBaseUrl}/remote/`;
  if (!isAllowedRemoteUrl(remoteUrl)) {
    return { opened: false, reason: "Only HTTPS or loopback HTTP remote URLs are allowed." };
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
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  remoteWindow.removeMenu();
  secureWebContents(remoteWindow.webContents, remoteUrl);
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
  handleTrusted("app:get-bootstrap", () => createBootstrap(false));
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
    app.exit(exitCode);
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
          const app = document.querySelector('#app')
          return {
            title: document.title,
            status: document.querySelector('[data-testid="rum-status"]')?.textContent,
            cards: document.querySelectorAll('.metric-card').length,
            resource: document.querySelector('#metric-resource')?.textContent,
            resourceStatus: document.querySelector('#resource-delta')?.textContent,
            rumInitialized: app?.dataset.rumInitialized === 'true',
            sdkEventTypes: (app?.dataset.rumSdkEventTypes || '').split(',').filter(Boolean),
            sdkResourceCount: Number(app?.dataset.rumSdkResourceCount || '0'),
            verifiedSignals: document.querySelectorAll('.coverage-row.verified').length,
            verifiedTypes: Array.from(document.querySelectorAll('.coverage-row.verified'))
              .map((element) => element.dataset.coverage),
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
        userCorrelation,
      };
      const passed =
        localResult.title === "Guance Desktop Control Room" &&
        localResult.cards >= 4 &&
        localResult.resource !== "\u2014" &&
        localResult.resourceStatus === "HTTP 503" &&
        localResult.rumInitialized === true &&
        localResult.sdkResourceCount >= 2 &&
        ["view", "action", "resource", "error", "long_task"].every((type) =>
          localResult.sdkEventTypes.includes(type),
        ) &&
        localResult.verifiedSignals >= 5 &&
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
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  mainWindow.removeMenu();
  secureWebContents(mainWindow.webContents, rendererEntry);
  mainWindow.once("ready-to-show", () => mainWindow?.show());
  mainWindow.on("closed", () => {
    mainWindow = undefined;
  });

  if (IS_SMOKE) {
    configureSmoke(mainWindow);
  }
  void mainWindow.loadURL(rendererEntry);

  if (readBooleanEnvironment("GUANCE_RUM_ELECTRON_DEVTOOLS")) {
    mainWindow.webContents.openDevTools({ mode: "detach" });
  }
}

app.whenReady().then(async () => {
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

app.on("before-quit", () => {
  if (apiServer) {
    apiServer.close();
    apiServer = undefined;
  }
});
