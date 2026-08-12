"use strict";

const { MAX_BRIDGE_PAYLOAD_BYTES } = require("./constants.cjs");

const MAX_PROPERTIES_PER_SECTION = 256;
const MAX_LOG_MESSAGE_BYTES = 256 * 1024;
const MAX_LOG_PROPERTY_BYTES = 64 * 1024;
const SUPPORTED_MEASUREMENTS = new Set([
  "view",
  "action",
  "resource",
  "error",
  "long_task",
]);
const SAFE_PROPERTY_KEY = /^[A-Za-z0-9_.-]{1,128}$/;
const RESERVED_PROPERTY_KEYS = new Set(["__proto__", "constructor", "prototype"]);
const RESERVED_LOG_KEYS = new Set(["message", "status"]);

function assertRecordObject(value, label) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }
}

function escapeKey(value) {
  return String(value)
    .replace(/([, =])/g, "\\$1")
    .replace(/\r/g, "\\r")
    .replace(/\n/g, "\\n");
}

function escapeFieldString(value) {
  return String(value)
    .replace(/\\/g, "\\\\")
    .replace(/"/g, '\\"')
    .replace(/\r/g, "\\r")
    .replace(/\n/g, "\\n");
}

function serializeStructuredValue(value) {
  try {
    return JSON.stringify(value);
  } catch {
    return undefined;
  }
}

function serializeTagValue(value) {
  if (value === null || value === undefined) return undefined;
  if (typeof value === "object") {
    const serialized = serializeStructuredValue(value);
    return serialized === undefined ? undefined : escapeKey(serialized);
  }
  if (typeof value === "number" && !Number.isFinite(value)) return undefined;
  return escapeKey(value);
}

function serializeFieldValue(value) {
  if (value === null || value === undefined) return '""';
  if (typeof value === "boolean") return value ? "true" : "false";
  if (typeof value === "number") {
    if (!Number.isFinite(value)) return undefined;
    if (Number.isSafeInteger(value)) return `${value}i`;
    return Object.is(value, -0) ? "0.0" : String(value);
  }
  if (typeof value === "object") {
    const serialized = serializeStructuredValue(value);
    return serialized === undefined ? undefined : `"${escapeFieldString(serialized)}"`;
  }
  return `"${escapeFieldString(value)}"`;
}

function collectProperties(properties, serializeValue) {
  assertRecordObject(properties, "RUM properties");
  const output = [];
  let inspected = 0;
  for (const [key, value] of Object.entries(properties)) {
    inspected += 1;
    if (inspected > MAX_PROPERTIES_PER_SECTION) break;
    if (!SAFE_PROPERTY_KEY.test(key) || RESERVED_PROPERTY_KEYS.has(key)) continue;
    const serialized = serializeValue(value);
    if (serialized !== undefined) output.push([key, serialized]);
  }
  return output;
}

function parseBridgeEvent(serializedEvent) {
  if (
    typeof serializedEvent !== "string" ||
    Buffer.byteLength(serializedEvent, "utf8") > MAX_BRIDGE_PAYLOAD_BYTES
  ) {
    throw new Error("Browser RUM bridge payload is invalid or too large.");
  }

  let event;
  try {
    event = JSON.parse(serializedEvent);
  } catch {
    throw new Error("Browser RUM bridge payload is not valid JSON.");
  }
  assertRecordObject(event, "Browser bridge event");
  if (event.name === "log") {
    const record = event.data;
    assertRecordObject(record, "Browser Log record");
    if (
      typeof record.message !== "string" ||
      record.message.length === 0 ||
      record.message.includes("\0") ||
      Buffer.byteLength(record.message, "utf8") > MAX_LOG_MESSAGE_BYTES
    ) {
      throw new Error("Browser Log message is invalid or too large.");
    }
    if (
      typeof record.status !== "string" ||
      !/^[A-Za-z0-9_.-]{1,64}$/.test(record.status)
    ) {
      throw new Error("Browser Log status is invalid.");
    }
    return { name: "log", record };
  }
  if (event.name !== "rum") {
    throw new Error("Browser bridge event type is not supported by this adapter.");
  }

  const record = event.data;
  assertRecordObject(record, "Browser RUM record");
  if (!SUPPORTED_MEASUREMENTS.has(record.measurement)) {
    throw new Error("Browser RUM measurement is not supported.");
  }
  if (!Number.isSafeInteger(record.time) || record.time <= 0) {
    throw new Error("Browser RUM timestamp must be a positive millisecond integer.");
  }
  assertRecordObject(record.tags, "Browser RUM tags");
  assertRecordObject(record.fields, "Browser RUM fields");
  return { name: "rum", record };
}

function browserRumRecordToLine(record, trustedTags) {
  const tags = new Map(collectProperties(record.tags, serializeTagValue));
  for (const [key, value] of collectProperties(trustedTags, serializeTagValue)) {
    tags.set(key, value);
  }

  const fields = new Map(collectProperties(record.fields, serializeFieldValue));
  if (fields.size === 0) {
    throw new Error("Browser RUM record must contain at least one field.");
  }

  const tagSection = [...tags.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, value]) => `,${escapeKey(key)}=${value}`)
    .join("");
  const fieldSection = [...fields.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, value]) => `${escapeKey(key)}=${value}`)
    .join(",");
  const timestampNanoseconds = BigInt(record.time) * 1_000_000n;

  return `${record.measurement}${tagSection} ${fieldSection} ${timestampNanoseconds}\n`;
}

function browserRumEventToLine(serializedEvent, trustedTags = {}) {
  const event = parseBridgeEvent(serializedEvent);
  if (event.name !== "rum") {
    throw new Error("Browser bridge event is not a RUM line-protocol record.");
  }
  return browserRumRecordToLine(event.record, trustedTags);
}

function serializeLogProperty(value) {
  if (value === undefined) return undefined;
  if (typeof value === "number" && !Number.isFinite(value)) return undefined;
  const serialized = typeof value === "string"
    ? value
    : typeof value === "object"
      ? serializeStructuredValue(value)
      : String(value);
  if (
    serialized === undefined ||
    serialized.includes("\0") ||
    Buffer.byteLength(serialized, "utf8") > MAX_LOG_PROPERTY_BYTES
  ) {
    return undefined;
  }
  return serialized;
}

function browserLogEventToCommand(event) {
  const status = event.record.status.toLowerCase() === "warn"
    ? "warning"
    : event.record.status.toLowerCase();
  const parts = [
    "@guance-log",
    `status=${status}`,
    `message=${Buffer.from(event.record.message, "utf8").toString("base64")}`,
  ];

  let inspected = 0;
  for (const [key, value] of Object.entries(event.record)) {
    inspected += 1;
    if (inspected > MAX_PROPERTIES_PER_SECTION) break;
    if (
      RESERVED_LOG_KEYS.has(key) ||
      RESERVED_PROPERTY_KEYS.has(key) ||
      !SAFE_PROPERTY_KEY.test(key)
    ) {
      continue;
    }
    const serialized = serializeLogProperty(value);
    if (serialized === undefined) continue;
    parts.push(`property-key=${Buffer.from(key, "utf8").toString("base64")}`);
    parts.push(`property-value=${Buffer.from(serialized, "utf8").toString("base64")}`);
  }

  const line = `${parts.join("\t")}\n`;
  if (Buffer.byteLength(line, "utf8") > MAX_BRIDGE_PAYLOAD_BYTES) {
    throw new Error("Browser Log native command is too large.");
  }
  return line;
}

function browserBridgeEventToNativeInput(serializedEvent, trustedTags = {}) {
  const event = parseBridgeEvent(serializedEvent);
  if (event.name === "log") {
    return { measurement: "log", line: browserLogEventToCommand(event) };
  }
  return {
    measurement: event.record.measurement,
    line: browserRumRecordToLine(event.record, trustedTags),
  };
}

module.exports = {
  browserBridgeEventToNativeInput,
  browserRumEventToLine,
  parseBridgeEvent,
};
