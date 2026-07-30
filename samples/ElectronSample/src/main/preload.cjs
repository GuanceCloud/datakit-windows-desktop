"use strict";

const { contextBridge, ipcRenderer } = require("electron");

const RUM_BRIDGE_CHANNEL = "rum:browser-event";
const MAX_SERIALIZED_EVENT_BYTES = 1024 * 1024;

const desktopBridge = Object.freeze({
  getBootstrap: () => ipcRenderer.invoke("app:get-bootstrap"),
  minimize: () => ipcRenderer.send("window:minimize"),
  toggleMaximize: () => ipcRenderer.invoke("window:toggle-maximize"),
  close: () => ipcRenderer.send("window:close"),
  selectWorkspace: () => ipcRenderer.invoke("native:select-workspace"),
  openRemoteWorkspace: () => ipcRenderer.invoke("hybrid:open-remote"),
  showNotification: (message) => ipcRenderer.invoke("native:show-notification", message),
});

if (!process.argv.includes("--guance-rum-only-preload")) {
  contextBridge.exposeInMainWorld("guanceDesktop", desktopBridge);
}

contextBridge.exposeInMainWorld(
  "FTWebViewJavascriptBridge",
  Object.freeze({
    getCapabilities: () => JSON.stringify([]),
    getPrivacyLevel: () => "mask",
    getAllowedWebViewHosts: () => null,
    sendEvent: (serializedEvent) => {
      if (
        typeof serializedEvent === "string" &&
        Buffer.byteLength(serializedEvent, "utf8") <= MAX_SERIALIZED_EVENT_BYTES
      ) {
        ipcRenderer.send(RUM_BRIDGE_CHANNEL, serializedEvent);
      }
    },
  }),
);
