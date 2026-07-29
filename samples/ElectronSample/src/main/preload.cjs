"use strict";

const { contextBridge, ipcRenderer } = require("electron");

const desktopBridge = Object.freeze({
  getBootstrap: () => ipcRenderer.invoke("app:get-bootstrap"),
  minimize: () => ipcRenderer.send("window:minimize"),
  toggleMaximize: () => ipcRenderer.invoke("window:toggle-maximize"),
  close: () => ipcRenderer.send("window:close"),
  selectWorkspace: () => ipcRenderer.invoke("native:select-workspace"),
  openRemoteWorkspace: () => ipcRenderer.invoke("hybrid:open-remote"),
  showNotification: (message) => ipcRenderer.invoke("native:show-notification", message),
});

contextBridge.exposeInMainWorld("guanceDesktop", desktopBridge);
