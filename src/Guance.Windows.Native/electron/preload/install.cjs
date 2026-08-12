"use strict";

const {
  BRIDGE_CHANNEL,
  BRIDGE_CONFIGURATION_CHANNEL,
  MAX_BRIDGE_PAYLOAD_BYTES,
} = require("../internal/constants.cjs");

const REPLAY_PRIVACY_LEVELS = new Set(["allow", "mask-user-input", "mask"]);

function readBridgeConfiguration(ipcRenderer) {
  let configuration;
  try {
    configuration = ipcRenderer.sendSync(BRIDGE_CONFIGURATION_CHANNEL);
  } catch {
    return { replayEnabled: false, replayPrivacy: "mask" };
  }
  const replayEnabled = configuration?.replayEnabled === true;
  return {
    replayEnabled,
    replayPrivacy: replayEnabled && REPLAY_PRIVACY_LEVELS.has(configuration?.replayPrivacy)
      ? configuration.replayPrivacy
      : "mask",
  };
}

function installElectronRumPreload(electron = require("electron")) {
  const { contextBridge, ipcRenderer } = electron || {};
  if (typeof contextBridge?.exposeInMainWorld !== "function" ||
      typeof ipcRenderer?.send !== "function" ||
      typeof ipcRenderer?.sendSync !== "function") {
    throw new Error("Electron contextBridge and ipcRenderer are required.");
  }

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
    getCapabilities: () => JSON.stringify(
      readBridgeConfiguration(ipcRenderer).replayEnabled ? ["records"] : [],
    ),
    getPrivacyLevel: () => readBridgeConfiguration(ipcRenderer).replayPrivacy,
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
  return bridge;
}

module.exports = {
  installElectronRumPreload,
};
