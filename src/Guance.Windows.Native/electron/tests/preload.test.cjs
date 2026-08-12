"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { installElectronRumPreload } = require("../preload/install.cjs");
const {
  BRIDGE_CHANNEL,
  BRIDGE_CONFIGURATION_CHANNEL,
  MAX_BRIDGE_PAYLOAD_BYTES,
} = require("../internal/constants.cjs");

function fakeElectron(configuration = { replayEnabled: false, replayPrivacy: "mask" }) {
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
      sendSync(channel) {
        assert.equal(channel, BRIDGE_CONFIGURATION_CHANNEL);
        return configuration;
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
  assert.equal(bridge.getCapabilities(), "[]");
  assert.equal(bridge.getPrivacyLevel(), "mask");
  assert.equal(Object.isFrozen(bridge), true);
  assert.throws(
    () => installElectronRumPreload(electron),
    /already occupied/,
  );
});

test("advertises Browser Replay only when Native configuration enables it", () => {
  const electron = fakeElectron({
    replayEnabled: true,
    replayPrivacy: "mask-user-input",
  });
  const bridge = installElectronRumPreload(electron);

  assert.equal(bridge.getCapabilities(), '["records"]');
  assert.equal(bridge.getPrivacyLevel(), "mask-user-input");
});

test("uses the safe Replay defaults when Native Replay is disabled or unavailable", () => {
  const disabledElectron = fakeElectron({ replayEnabled: false, replayPrivacy: "allow" });
  const disabledBridge = installElectronRumPreload(disabledElectron);
  assert.equal(disabledBridge.getCapabilities(), "[]");
  assert.equal(disabledBridge.getPrivacyLevel(), "mask");

  const unavailableElectron = fakeElectron();
  unavailableElectron.ipcRenderer.sendSync = () => { throw new Error("IPC unavailable"); };
  const unavailableBridge = installElectronRumPreload(unavailableElectron);
  assert.equal(unavailableBridge.getCapabilities(), "[]");
  assert.equal(unavailableBridge.getPrivacyLevel(), "mask");
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
  const electron = fakeElectron({ replayEnabled: true, replayPrivacy: "allow" });
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
  assert.equal(
    electron.mainWorld.FTWebViewJavascriptBridge.getCapabilities(),
    '["records"]',
  );
  assert.equal(electron.mainWorld.FTWebViewJavascriptBridge.getPrivacyLevel(), "allow");
});
