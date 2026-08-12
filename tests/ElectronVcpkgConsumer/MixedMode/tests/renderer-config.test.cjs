"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

test("uses only the Browser RUM bridge placeholder origin", () => {
  let initConfiguration;
  const status = { dataset: {} };
  const actionButton = { addEventListener() {} };

  vm.runInNewContext(
    fs.readFileSync(path.join(__dirname, "..", "renderer.js"), "utf8"),
    {
      document: {
        querySelector(selector) {
          return selector === "#status" ? status : actionButton;
        },
      },
      window: {
        DATAFLUX_RUM: {
          init(configuration) {
            initConfiguration = configuration;
          },
        },
        FTWebViewJavascriptBridge: {},
      },
    },
  );

  assert.equal(initConfiguration.datakitOrigin, "http://127.0.0.1");
  assert.equal(initConfiguration.applicationId, undefined);
  assert.deepEqual(Object.keys(initConfiguration), ["datakitOrigin"]);
});

test("starts Browser Replay only when the Native bridge advertises records", () => {
  let replayStarts = 0;
  const status = { dataset: {} };
  const actionButton = { addEventListener() {} };

  vm.runInNewContext(
    fs.readFileSync(path.join(__dirname, "..", "renderer.js"), "utf8"),
    {
      document: {
        querySelector(selector) {
          return selector === "#status" ? status : actionButton;
        },
      },
      window: {
        DATAFLUX_RUM: {
          init() {},
          startSessionReplayRecording() { replayStarts += 1; },
        },
        FTWebViewJavascriptBridge: {
          getCapabilities: () => '["records"]',
        },
      },
    },
  );

  assert.equal(replayStarts, 1);
  assert.match(status.textContent, /Session Replay/);
});
