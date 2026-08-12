"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const {
  browserBridgeEventToNativeInput,
  browserRumEventToLine,
} = require("../internal/rum-line-protocol.cjs");
const { MAX_BRIDGE_PAYLOAD_BYTES } = require("../internal/constants.cjs");

function rumEvent(overrides = {}) {
  return JSON.stringify({
    name: "rum",
    data: {
      measurement: "view",
      time: 1_700_000_000_000,
      tags: { app_id: "renderer-value", view_name: "adapter" },
      fields: { view_loading_time: 42 },
      ...overrides,
    },
  });
}

function logEvent(overrides = {}) {
  return JSON.stringify({
    name: "log",
    data: {
      message: "Renderer request failed\nwith context",
      status: "warn",
      service: "browser",
      view: { id: "browser-view", url: "file:///index.html" },
      request_id: "request-1",
      ...overrides,
    },
  });
}

test("converts Browser RUM JSON with trusted Native field overrides", () => {
  const line = browserRumEventToLine(rumEvent(), {
    app_id: "trusted-app-id",
    service: "electron-adapter",
    is_electron: "true",
  });

  assert.match(line, /^view,/);
  assert.match(line, /app_id=trusted-app-id/);
  assert.doesNotMatch(line, /renderer-value/);
  assert.match(line, /is_electron=true/);
  assert.match(line, /view_loading_time=42i/);
  assert.match(line, /1700000000000000000\n$/);
});

test("converts Browser Logs to the private Native log command", () => {
  const result = browserBridgeEventToNativeInput(logEvent());

  assert.equal(result.measurement, "log");
  assert.match(result.line, /^@guance-log\tstatus=warning\tmessage=/);
  assert.match(
    result.line,
    new RegExp(`message=${Buffer.from("Renderer request failed\nwith context").toString("base64")}`),
  );
  assert.match(
    result.line,
    new RegExp(`property-key=${Buffer.from("request_id").toString("base64")}`),
  );
  assert.match(
    result.line,
    new RegExp(`property-value=${Buffer.from("request-1").toString("base64")}`),
  );
  assert.match(result.line, /\n$/);
});

test("rejects invalid event types, records, timestamps, and message sizes", () => {
  assert.throws(
    () => browserRumEventToLine(logEvent()),
    /not a RUM line-protocol record/,
  );
  assert.throws(
    () => browserBridgeEventToNativeInput(JSON.stringify({ name: "unknown", data: {} })),
    /type is not supported/,
  );
  assert.throws(
    () => browserBridgeEventToNativeInput(logEvent({ message: "" })),
    /Log message is invalid/,
  );
  assert.throws(
    () => browserBridgeEventToNativeInput(logEvent({ status: "not valid" })),
    /Log status is invalid/,
  );
  assert.throws(
    () => browserRumEventToLine(rumEvent({ measurement: "custom" })),
    /measurement is not supported/,
  );
  assert.throws(
    () => browserRumEventToLine(rumEvent({ time: -1 })),
    /timestamp/,
  );
  assert.throws(
    () => browserRumEventToLine("x".repeat(MAX_BRIDGE_PAYLOAD_BYTES + 1)),
    /too large/,
  );
});
