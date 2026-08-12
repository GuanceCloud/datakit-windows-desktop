"use strict";

const { MAX_BRIDGE_PAYLOAD_BYTES } = require("./constants.cjs");

const MAX_PROPERTIES_PER_SECTION = 256;
const SUPPORTED_MEASUREMENTS = new Set([
  "view",
  "action",
  "resource",
  "error",
  "long_task",
]);
const SAFE_PROPERTY_KEY = /^[A-Za-z0-9_.-]{1,128}$/;
const RESERVED_PROPERTY_KEYS = new Set(["__proto__", "constructor", "prototype"]);

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

function parseRumEvent(serializedEvent) {
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
  assertRecordObject(event, "Browser RUM bridge event");
  if (event.name !== "rum") {
    throw new Error("Only Browser RUM events are supported by this adapter.");
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
  return record;
}

function browserRumEventToLine(serializedEvent, trustedTags = {}) {
  const record = parseRumEvent(serializedEvent);
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

module.exports = {
  browserRumEventToLine,
};
