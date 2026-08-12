"use strict";

const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { app, BrowserWindow, dialog, ipcMain } = require("electron");

const ELECTRON_ADAPTER_DIRECTORY = path.join(
  __dirname,
  "vcpkg_installed",
  "x64-windows",
  "tools",
  "guance-windows-native",
  "electron",
);
const { connectMixedMode } = require(path.join(
  ELECTRON_ADAPTER_DIRECTORY,
  "main",
  "index.cjs",
));
const PRELOAD_PATH = path.join(__dirname, ".build", "preload.cjs");
const IS_SMOKE = process.argv.includes("--guance-smoke");

let mainWindow;
let rumBridge;

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
          samplePreload: window.guanceMixedModeSample?.bundled,
          rum: typeof window.DATAFLUX_RUM?.addAction
        })`);
        if (state.status !== "ready" || state.bridge !== "function" ||
            state.samplePreload !== true || state.rum !== "function") {
          throw new Error(`Renderer smoke state is invalid: ${JSON.stringify(state)}`);
        }
        await mainWindow.webContents.executeJavaScript(
          'document.querySelector("#preview-action").click()',
        );
        console.log("[rum-bridge] renderer smoke passed path=mixed-mode");
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
  rumBridge = await connectMixedMode({
    ipcMain,
    pipeName: process.env.GUANCE_RUM_NATIVE_OWNED_PIPE_NAME,
  });
  createWindow();
  console.log("[rum-bridge] initialized path=mixed-mode");
}).catch((error) => {
  dialog.showErrorBox("Electron Mixed Mode sample failed to start", String(error.message || error));
  app.exit(1);
});

app.on("window-all-closed", () => {
  const bridge = rumBridge;
  rumBridge = undefined;
  void (bridge?.stop() || Promise.resolve()).finally(() => app.quit());
});
