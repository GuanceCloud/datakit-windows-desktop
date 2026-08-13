"use strict";

const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const test = require("node:test");
const {
  ElectronApplicationLaunchTracker,
} = require("../internal/application-launch.cjs");
const {
  browserRumViewContext,
  electronLaunchToNativeInput,
} = require("../internal/rum-line-protocol.cjs");

const SECOND = 1_000_000_000n;

class FakeBrowserWindow extends EventEmitter {
  constructor() {
    super();
    this.focused = false;
    this.webContents = new EventEmitter();
    this.webContents.mainFrame = {};
    this.webContents.isDestroyed = () => false;
  }

  isFocused() {
    return this.focused;
  }

  isVisible() {
    return false;
  }

  focusForTest() {
    this.focused = true;
    this.emit("focus");
  }

  blurForTest() {
    this.focused = false;
    this.emit("blur");
  }
}

function viewEvent(id = "electron-view") {
  return JSON.stringify({
    name: "rum",
    data: {
      measurement: "view",
      time: 1_700_000_000_000,
      tags: {
        view_id: id,
        view_name: "Electron Main",
        view_referrer: "file:///splash.html",
      },
      fields: { time_spent: 1 },
    },
  });
}

test("Electron launch tracker reports cold phases, trusted View, and repeatable hot launches", async () => {
  let current = {
    unixNanoseconds: 100n * SECOND,
    monotonicNanoseconds: 20n * SECOND,
  };
  const launches = [];
  let doubleRafCount = 0;
  const tracker = new ElectronApplicationLaunchTracker({
    now: () => current,
    processUptimeNanoseconds: 2n * SECOND,
    sendLaunch: (launch, view) => launches.push({ launch, view }),
    confirmFirstFrame: async () => { doubleRafCount += 1; },
    scheduleImmediate: (callback) => {
      callback();
      return 1;
    },
    cancelImmediate: () => {},
  });

  current = {
    unixNanoseconds: 101n * SECOND,
    monotonicNanoseconds: 21n * SECOND,
  };
  const window = new FakeBrowserWindow();
  const detach = tracker.attachWindow(window);
  tracker.observeTrustedView(window.webContents, browserRumViewContext(viewEvent()));
  current = {
    unixNanoseconds: 103n * SECOND,
    monotonicNanoseconds: 23n * SECOND,
  };
  window.emit("ready-to-show");

  assert.equal(launches.length, 1);
  assert.equal(launches[0].launch.type, "cold");
  assert.equal(launches[0].launch.preApplicationDurationNanoseconds, 2n * SECOND);
  assert.equal(launches[0].launch.applicationDurationNanoseconds, 1n * SECOND);
  assert.equal(launches[0].launch.firstFrameDurationNanoseconds, 2n * SECOND);
  assert.equal(launches[0].launch.durationNanoseconds, 5n * SECOND);
  assert.equal(launches[0].view.id, "electron-view");

  current = {
    unixNanoseconds: 110n * SECOND,
    monotonicNanoseconds: 30n * SECOND,
  };
  window.blurForTest();
  current = {
    unixNanoseconds: 119n * SECOND,
    monotonicNanoseconds: 39n * SECOND,
  };
  window.focusForTest();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(launches.length, 1, "nine background seconds must not emit hot launch");

  current = {
    unixNanoseconds: 120n * SECOND,
    monotonicNanoseconds: 40n * SECOND,
  };
  window.blurForTest();
  current = {
    unixNanoseconds: 130n * SECOND,
    monotonicNanoseconds: 50n * SECOND,
  };
  window.focusForTest();
  current = {
    unixNanoseconds: 131n * SECOND,
    monotonicNanoseconds: 51n * SECOND,
  };
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(launches.length, 2);
  assert.equal(launches[1].launch.type, "hot");
  assert.equal(launches[1].launch.durationNanoseconds, SECOND);

  current = {
    unixNanoseconds: 140n * SECOND,
    monotonicNanoseconds: 60n * SECOND,
  };
  window.blurForTest();
  current = {
    unixNanoseconds: 151n * SECOND,
    monotonicNanoseconds: 71n * SECOND,
  };
  window.focusForTest();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(launches.length, 3);
  assert.equal(doubleRafCount, 2);

  detach();
  assert.equal(window.listenerCount("ready-to-show"), 0);
  assert.equal(window.listenerCount("focus"), 0);
  assert.equal(window.listenerCount("blur"), 0);
  assert.equal(window.webContents.listenerCount("did-finish-load"), 0);
  tracker.dispose();
});

test("Electron cold launch falls back to did-finish-load and waits up to the View timeout", () => {
  let current = {
    unixNanoseconds: 50n * SECOND,
    monotonicNanoseconds: 10n * SECOND,
  };
  let timeoutCallback;
  const launches = [];
  const tracker = new ElectronApplicationLaunchTracker({
    now: () => current,
    processUptimeNanoseconds: SECOND,
    sendLaunch: (launch, view) => launches.push({ launch, view }),
    scheduleTimeout: (callback, timeout) => {
      assert.equal(timeout, 1_000);
      timeoutCallback = callback;
      return 7;
    },
    cancelTimeout: () => {},
  });
  const webContents = new EventEmitter();
  webContents.mainFrame = {};
  webContents.isDestroyed = () => false;
  tracker.attachWindow(webContents);
  current = {
    unixNanoseconds: 51n * SECOND,
    monotonicNanoseconds: 11n * SECOND,
  };
  webContents.emit("did-finish-load");
  assert.equal(launches.length, 0);
  timeoutCallback();
  assert.equal(launches.length, 1);
  assert.equal(launches[0].view, undefined);
  tracker.dispose();
});

test("BrowserWindow uses did-finish-load when ready-to-show is unavailable", () => {
  let current = {
    unixNanoseconds: 70n * SECOND,
    monotonicNanoseconds: 10n * SECOND,
  };
  const timers = [];
  const launches = [];
  const tracker = new ElectronApplicationLaunchTracker({
    now: () => current,
    processUptimeNanoseconds: SECOND,
    sendLaunch: (launch) => launches.push(launch),
    coldViewWaitMs: 0,
    scheduleTimeout: (callback, timeout) => {
      timers.push({ callback, timeout });
      return timers.length;
    },
    cancelTimeout: () => {},
  });
  const window = new FakeBrowserWindow();
  tracker.attachWindow(window);
  current = {
    unixNanoseconds: 71n * SECOND,
    monotonicNanoseconds: 11n * SECOND,
  };
  window.webContents.emit("did-finish-load");
  assert.equal(timers[0].timeout, 1_000);
  timers[0].callback();
  const viewTimer = timers.find((timer) => timer.timeout === 0);
  viewTimer.callback();
  assert.equal(launches.length, 1);
  tracker.dispose();
});

test("Electron launch command preserves the existing Native launch protocol", () => {
  const view = browserRumViewContext(viewEvent("view-42"));
  const line = electronLaunchToNativeInput({
    type: "cold",
    startTimeNanoseconds: 1n,
    durationNanoseconds: 6n,
    preApplicationDurationNanoseconds: 1n,
    applicationDurationNanoseconds: 2n,
    firstFrameDurationNanoseconds: 3n,
  }, view);
  assert.match(line, /^@guance-launch\ttype=cold\t/);
  assert.match(line, /duration_ns=6/);
  assert.match(line, /view_id=view-42/);
  assert.match(line, /view_name=Electron%20Main/);
  assert.throws(
    () => browserRumViewContext(viewEvent("bad view id")),
    /View context is invalid/,
  );
});
