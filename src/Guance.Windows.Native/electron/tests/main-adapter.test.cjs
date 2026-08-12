"use strict";

const assert = require("node:assert/strict");
const { EventEmitter, once } = require("node:events");
const net = require("node:net");
const test = require("node:test");
const { connectMixedMode, startFullMode } = require("../main/index.cjs");
const { BRIDGE_CHANNEL } = require("../internal/constants.cjs");

const VALID_HANDSHAKE = [
  "@guance-capabilities",
  "protocol=1",
  "rum=1",
  "log=0",
  "replay=0",
  "replay_privacy=mask",
  "trace=0",
  "trace_sample_rate=100",
  "trace_type=w3c_traceparent",
  "trace_allowed_urls=",
  "debug=0",
].join("\t") + "\n";

function pipeName(label) {
  return `guance-adapter-${label}-${process.pid}-${Date.now()}-${Math.random()
    .toString(16).slice(2)}`;
}

function pipePath(name) {
  return `\\\\.\\pipe\\${name}`;
}

function listen(server, target) {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(target, () => {
      server.off("error", reject);
      resolve();
    });
  });
}

function close(server) {
  return new Promise((resolve) => server.close(resolve));
}

function rumEvent() {
  return JSON.stringify({
    name: "rum",
    data: {
      measurement: "action",
      time: 1_700_000_000_000,
      tags: { action_name: "public-main-api" },
      fields: { duration: 42 },
    },
  });
}

test("Mixed Mode retries, validates the handshake, and uses the versioned IPC channel", async () => {
  const name = pipeName("retry");
  const server = net.createServer();
  let received = "";
  const receivedLine = new Promise((resolve) => {
    server.on("connection", (socket) => {
      socket.write(VALID_HANDSHAKE);
      socket.setEncoding("utf8");
      socket.on("data", (chunk) => {
        received += chunk;
        if (received.includes("\n")) resolve();
      });
    });
  });
  const delayedListen = new Promise((resolve) => setTimeout(resolve, 60))
    .then(() => listen(server, pipePath(name)));

  const ipcMain = new EventEmitter();
  const adapterErrors = [];
  const bridgePromise = connectMixedMode({
    ipcMain,
    pipeName: name,
    timeoutMs: 1_000,
    retryDelayMs: 20,
    onError: (error) => adapterErrors.push(error),
  });
  await delayedListen;
  const bridge = await bridgePromise;
  const webContents = { mainFrame: {}, isDestroyed: () => false };
  bridge.attachWindow(webContents);

  ipcMain.emit(
    "rum:browser-event",
    { sender: webContents, senderFrame: webContents.mainFrame },
    rumEvent(),
  );
  assert.equal(received, "");
  ipcMain.emit(
    BRIDGE_CHANNEL,
    { sender: webContents, senderFrame: webContents.mainFrame },
    "not-json",
  );
  ipcMain.emit(
    BRIDGE_CHANNEL,
    { sender: webContents, senderFrame: webContents.mainFrame },
    rumEvent(),
  );
  await receivedLine;

  assert.match(received, /^action,/);
  assert.match(received, /is_electron=true/);
  assert.match(received, /duration=42i/);
  assert.equal(adapterErrors.length, 1);
  assert.match(adapterErrors[0].message, /not valid JSON/);
  await bridge.stop();
  await close(server);
});

test("Mixed Mode rejects malformed capabilities and bounds connection retries", async () => {
  const malformedName = pipeName("malformed");
  const server = net.createServer((socket) => {
    socket.end("@guance-capabilities\tprotocol=2\trum=1\n");
  });
  await listen(server, pipePath(malformedName));
  await assert.rejects(
    connectMixedMode({
      ipcMain: new EventEmitter(),
      pipeName: malformedName,
      timeoutMs: 500,
      retryDelayMs: 20,
    }),
    /does not support RUM bridge protocol 1/,
  );
  await close(server);

  const started = Date.now();
  await assert.rejects(
    connectMixedMode({
      ipcMain: new EventEmitter(),
      pipeName: pipeName("missing"),
      timeoutMs: 80,
      retryDelayMs: 20,
    }),
    /Timed out connecting/,
  );
  assert.ok(Date.now() - started < 1_000, "Mixed Mode retries exceeded the bound");
});

test("Full Mode validates the installed native directory before process startup", async () => {
  await assert.rejects(
    startFullMode({
      ipcMain: new EventEmitter(),
      nativeDirectory: __dirname,
      nativeSettings: {},
    }),
    /installed native runtime is missing/,
  );
});
