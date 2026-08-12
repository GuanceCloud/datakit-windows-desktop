"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { installElectronRumPreload } = require("../preload/install.cjs");
const {
  BRIDGE_CHANNEL,
  MAX_BRIDGE_PAYLOAD_BYTES,
} = require("../internal/constants.cjs");

function fakeElectron() {
  const sent = [];
  const mainWorld = {};
  return {
    sent,
    mainWorld,
    contextBridge: {
      executeInMainWorld({ func }) {
        return vm.runInNewContext(`(${func.toString()})()`, { window: mainWorld });
      },
      exposeInMainWorld(name, value) {
        if (Object.prototype.hasOwnProperty.call(mainWorld, name)) {
          throw new Error(`${name} already exists`);
        }
        mainWorld[name] = value;
      },
    },
    ipcRenderer: {
      send(...args) {
        sent.push(args);
      },
    },
  };
}

test("installs one versioned Renderer bridge and enforces the message limit", () => {
  const electron = fakeElectron();
  const bridge = installElectronRumPreload(electron);
  bridge.sendEvent("valid-event");
  bridge.sendEvent("x".repeat(MAX_BRIDGE_PAYLOAD_BYTES + 1));

  assert.deepEqual(electron.sent, [[BRIDGE_CHANNEL, "valid-event"]]);
  assert.equal(Object.isFrozen(bridge), true);
  assert.throws(
    () => installElectronRumPreload(electron),
    /already occupied/,
  );
});

test("fails explicitly when another SDK owns FTWebViewJavascriptBridge", () => {
  const electron = fakeElectron();
  electron.mainWorld.FTWebViewJavascriptBridge = { owner: "another-sdk" };
  assert.throws(
    () => installElectronRumPreload(electron),
    /already occupied/,
  );
});

test("standalone preload is self-contained under the sandboxed require surface", () => {
  const electron = fakeElectron();
  const source = fs.readFileSync(
    path.join(__dirname, "..", "preload", "standalone.cjs"),
    "utf8",
  );
  vm.runInNewContext(source, {
    Buffer,
    Error,
    Object,
    require(name) {
      assert.equal(name, "electron");
      return electron;
    },
  });

  electron.mainWorld.FTWebViewJavascriptBridge.sendEvent("standalone-event");
  assert.deepEqual(electron.sent, [[BRIDGE_CHANNEL, "standalone-event"]]);
});
