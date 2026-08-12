"use strict";

module.exports = Object.freeze({
  BRIDGE_CHANNEL: "guance:electron-rum:browser-event:v1",
  BRIDGE_CONFIGURATION_CHANNEL: "guance:electron-rum:configuration:v1",
  DEFAULT_PIPE_NAME: "guance-rum-electron-native-owned",
  MAX_BRIDGE_PAYLOAD_BYTES: 1024 * 1024,
  MAX_CAPABILITIES_BYTES: 16 * 1024,
});
