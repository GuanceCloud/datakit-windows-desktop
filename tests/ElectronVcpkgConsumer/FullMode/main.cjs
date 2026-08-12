"use strict";

const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { app, BrowserWindow, dialog, ipcMain } = require("electron");

const NATIVE_DIRECTORY = path.join(
  __dirname,
  "vcpkg_installed",
  "x64-windows",
  "tools",
  "guance-windows-native",
);
const ELECTRON_ADAPTER_DIRECTORY = path.join(NATIVE_DIRECTORY, "electron");
const { startFullMode } = require(path.join(
  ELECTRON_ADAPTER_DIRECTORY,
  "main",
  "index.cjs",
));
const PRELOAD_PATH = path.join(
  ELECTRON_ADAPTER_DIRECTORY,
  "preload",
  "standalone.cjs",
);
const IS_SMOKE = process.argv.includes("--guance-smoke");

let mainWindow;
let rumBridge;

function readBooleanEnvironment(name) {
  return /^(1|true)$/i.test(process.env[name]?.trim() || "");
}

function readNativeSettings() {
  const applicationId = process.env.GUANCE_RUM_APP_ID?.trim();
  if (!applicationId) {
    throw new Error("Set GUANCE_RUM_APP_ID before starting the Full Mode sample.");
  }
  return {
    applicationId,
    datakitUrl: process.env.GUANCE_RUM_DATAKIT_URL?.trim() || "http://127.0.0.1:9529",
    service: process.env.GUANCE_RUM_SERVICE?.trim() || "electron-full-preview",
    environment: process.env.GUANCE_RUM_ENV?.trim() || "local",
    version: app.getVersion(),
    cachePath: path.join(app.getPath("userData"), "rum-cache"),
    sampleRate: 1,
    loggingEnabled: false,
    replayEnabled: readBooleanEnvironment("GUANCE_RUM_SESSION_REPLAY_ENABLED"),
    replayPrivacy: process.env.GUANCE_RUM_REPLAY_PRIVACY_LEVEL?.trim() || "mask",
    debug: true,
  };
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 720,
    height: 480,
    show: !IS_SMOKE,
    webPreferences: {
      preload: PRELOAD_PATH,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  rumBridge.attachWindow(mainWindow);

  const entryUrl = pathToFileURL(path.join(__dirname, "index.html"));
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  mainWindow.webContents.on("will-navigate", (event, destination) => {
    if (destination !== entryUrl.href) event.preventDefault();
  });
  if (IS_SMOKE) {
    mainWindow.webContents.once("did-finish-load", async () => {
      try {
        const state = await mainWindow.webContents.executeJavaScript(`({
          status: document.querySelector("#status")?.dataset.state,
          bridge: typeof window.FTWebViewJavascriptBridge?.sendEvent,
          rum: typeof window.DATAFLUX_RUM?.addAction,
          capabilities: JSON.parse(window.FTWebViewJavascriptBridge?.getCapabilities?.() || "[]"),
          replayPrivacy: window.FTWebViewJavascriptBridge?.getPrivacyLevel?.()
        })`);
        const replayVisible = state.capabilities.includes("records");
        if (state.status !== "ready" || state.bridge !== "function" || state.rum !== "function" ||
            replayVisible !== rumBridge.capabilities.replay ||
            state.replayPrivacy !== rumBridge.capabilities.replayPrivacy) {
          throw new Error(`Renderer smoke state is invalid: ${JSON.stringify(state)}`);
        }
        await mainWindow.webContents.executeJavaScript(
          'document.querySelector("#preview-action").click()',
        );
        console.log("[rum-bridge] renderer smoke passed path=full-mode");
      } catch (error) {
        process.exitCode = 1;
        console.error("[rum-bridge] renderer smoke failed", error);
      } finally {
        mainWindow.close();
      }
    });
  }
  void mainWindow.loadURL(entryUrl.href);
}

app.whenReady().then(async () => {
  rumBridge = await startFullMode({
    ipcMain,
    nativeDirectory: NATIVE_DIRECTORY,
    nativeSettings: readNativeSettings(),
    onNativeOutput(stream, chunk) {
      const output = String(chunk).trim();
      if (output) console[stream === "stderr" ? "error" : "log"](output);
    },
  });
  createWindow();
  console.log("[rum-bridge] initialized path=full-mode");
}).catch((error) => {
  dialog.showErrorBox("Electron Full Mode sample failed to start", String(error.message || error));
  app.exit(1);
});

app.on("window-all-closed", () => {
  const bridge = rumBridge;
  rumBridge = undefined;
  void (bridge?.stop() || Promise.resolve()).finally(() => app.quit());
});
