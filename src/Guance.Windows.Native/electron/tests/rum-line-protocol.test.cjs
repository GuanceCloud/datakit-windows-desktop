"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { browserRumEventToLine } = require("../internal/rum-line-protocol.cjs");
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

test("rejects invalid type, measurement, timestamp, and message size", () => {
  assert.throws(
    () => browserRumEventToLine(JSON.stringify({ name: "log", data: {} })),
    /Only Browser RUM events/,
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
