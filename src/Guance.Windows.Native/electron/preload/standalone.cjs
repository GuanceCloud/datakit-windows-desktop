"use strict";

const { contextBridge, ipcRenderer } = require("electron");

const BRIDGE_CHANNEL = "guance:electron-rum:browser-event:v1";
const MAX_BRIDGE_PAYLOAD_BYTES = 1024 * 1024;

if (typeof contextBridge.executeInMainWorld === "function") {
  const occupied = contextBridge.executeInMainWorld({
    func: () => Object.prototype.hasOwnProperty.call(
      window,
      "FTWebViewJavascriptBridge",
    ),
  });
  if (occupied) {
    throw new Error("window.FTWebViewJavascriptBridge is already occupied.");
  }
}

const bridge = Object.freeze({
  getCapabilities: () => JSON.stringify([]),
  getPrivacyLevel: () => "mask",
  getAllowedWebViewHosts: () => null,
  sendEvent: (serializedEvent) => {
    if (typeof serializedEvent === "string" &&
        Buffer.byteLength(serializedEvent, "utf8") <= MAX_BRIDGE_PAYLOAD_BYTES) {
      ipcRenderer.send(BRIDGE_CHANNEL, serializedEvent);
    }
  },
});

try {
  contextBridge.exposeInMainWorld("FTWebViewJavascriptBridge", bridge);
} catch (error) {
  throw new Error(
    `Failed to install window.FTWebViewJavascriptBridge; the global may already be occupied: ${error.message}`,
  );
}
