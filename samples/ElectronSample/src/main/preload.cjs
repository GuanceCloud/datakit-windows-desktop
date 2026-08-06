"use strict";

const { contextBridge, ipcRenderer } = require("electron");

const RUM_BRIDGE_CHANNEL = "rum:browser-event";
const MAX_SERIALIZED_EVENT_BYTES = 1024 * 1024;
const REPLAY_ARGUMENT_PREFIX = "--guance-rum-replay=";
const MONITORING_DISABLED_ARGUMENT = "--guance-monitoring-disabled";
const SUPPORTED_PRIVACY_LEVELS = new Set(["allow", "mask-user-input", "mask"]);

const replayArgument = process.argv.find((argument) =>
  argument.startsWith(REPLAY_ARGUMENT_PREFIX),
);
const requestedPrivacyLevel = replayArgument
  ? replayArgument.slice(REPLAY_ARGUMENT_PREFIX.length)
  : "mask";
const sessionReplayEnabled = Boolean(replayArgument);
const monitoringEnabled = !process.argv.includes(MONITORING_DISABLED_ARGUMENT);
const sessionReplayPrivacyLevel = SUPPORTED_PRIVACY_LEVELS.has(requestedPrivacyLevel)
  ? requestedPrivacyLevel
  : "mask";

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

if (monitoringEnabled) {
  contextBridge.exposeInMainWorld(
    "FTWebViewJavascriptBridge",
    Object.freeze({
      getCapabilities: () => JSON.stringify(sessionReplayEnabled ? ["records"] : []),
      getPrivacyLevel: () => sessionReplayPrivacyLevel,
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
}
