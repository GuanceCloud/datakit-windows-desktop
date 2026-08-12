"use strict";

const { contextBridge } = require("electron");
const { installElectronRumPreload } = require(
  "./vcpkg_installed/x64-windows/tools/guance-windows-native/electron/preload/install.cjs"
);

installElectronRumPreload();
contextBridge.exposeInMainWorld(
  "guanceMixedModeSample",
  Object.freeze({ bundled: true }),
);
