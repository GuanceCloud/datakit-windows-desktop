"use strict";

const fs = require("node:fs");
const path = require("node:path");

const LOCAL_SETTINGS_FILE_NAME = "rum.local.json";

function findInAncestors(startDirectory) {
  let directory = path.resolve(startDirectory);
  while (true) {
    const candidate = path.join(directory, LOCAL_SETTINGS_FILE_NAME);
    if (fs.existsSync(candidate)) {
      return candidate;
    }

    const parent = path.dirname(directory);
    if (parent === directory) {
      return undefined;
    }
    directory = parent;
  }
}

function readSettingsFile(filePath) {
  const json = fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, "");
  const settings = JSON.parse(json);
  if (!settings || typeof settings !== "object" || Array.isArray(settings)) {
    throw new Error(`${filePath} must contain a JSON object.`);
  }
  return { filePath, settings };
}

function loadLocalRumSettings({
  currentDirectory,
  appPath,
  executablePath,
  isPackaged,
}) {
  if (isPackaged) {
    const sidecarPath = path.join(
      path.dirname(executablePath),
      LOCAL_SETTINGS_FILE_NAME,
    );
    return fs.existsSync(sidecarPath)
      ? readSettingsFile(sidecarPath)
      : { filePath: undefined, settings: {} };
  }

  const searchDirectories = [
    currentDirectory,
    path.extname(appPath) ? path.dirname(appPath) : appPath,
  ];

  const visited = new Set();
  for (const directory of searchDirectories) {
    const resolvedDirectory = path.resolve(directory);
    if (visited.has(resolvedDirectory)) {
      continue;
    }
    visited.add(resolvedDirectory);

    const filePath = findInAncestors(resolvedDirectory);
    if (!filePath) {
      continue;
    }

    return readSettingsFile(filePath);
  }

  return { filePath: undefined, settings: {} };
}

function createLocalRumSettingsReader({ environment, settings }) {
  const readEnvironment = (name) => {
    const value = environment[name];
    return typeof value === "string" && value.trim().length > 0
      ? value.trim()
      : undefined;
  };

  const readString = (environmentName, jsonName, fallback = "") => {
    const environmentValue = readEnvironment(environmentName);
    if (environmentValue !== undefined) {
      return environmentValue;
    }

    const jsonValue = settings[jsonName];
    return typeof jsonValue === "string" && jsonValue.trim().length > 0
      ? jsonValue.trim()
      : fallback;
  };

  const readBoolean = (environmentName, jsonName, fallback = false) => {
    const value = readEnvironment(environmentName) ?? settings[jsonName];
    const normalized =
      typeof value === "string" ? value.trim().toLowerCase() : value;
    if (normalized === true || normalized === "true" || normalized === "1") {
      return true;
    }
    if (normalized === false || normalized === "false" || normalized === "0") {
      return false;
    }
    return fallback;
  };

  const readNumber = (environmentName, jsonName, fallback) => {
    const value = readEnvironment(environmentName) ?? settings[jsonName];
    if (
      value === null ||
      value === undefined ||
      (typeof value === "string" && value.trim().length === 0)
    ) {
      return fallback;
    }
    const number = typeof value === "number" ? value : Number(value);
    return Number.isFinite(number) ? number : fallback;
  };

  const normalizeStringArray = (value, fallback) => {
    let resolved = value;
    if (typeof resolved === "string") {
      const trimmed = resolved.trim();
      if (trimmed.startsWith("[")) {
        try {
          resolved = JSON.parse(trimmed);
        } catch {
          return fallback;
        }
      } else {
        resolved = trimmed.split(",");
      }
    }
    if (!Array.isArray(resolved)) {
      return fallback;
    }
    const entries = resolved
      .filter((item) => typeof item === "string")
      .map((item) => item.trim())
      .filter(Boolean);
    return entries.length > 0 ? entries : fallback;
  };

  const readStringArray = (environmentName, jsonName, fallback = []) => {
    const environmentValue = readEnvironment(environmentName);
    return normalizeStringArray(
      environmentValue === undefined ? settings[jsonName] : environmentValue,
      fallback,
    );
  };

  return {
    readBoolean,
    readEnvironmentString: readEnvironment,
    readNumber,
    readString,
    readStringArray,
  };
}

function resolveRumIngestionConfiguration(reader, defaultDatakitUrl) {
  const environmentDatawayUrl = reader.readEnvironmentString(
    "GUANCE_RUM_DATAWAY_URL",
  );
  const environmentDatakitUrl = reader.readEnvironmentString(
    "GUANCE_RUM_DATAKIT_URL",
  );
  const datawayUrl = reader.readString(
    "GUANCE_RUM_DATAWAY_URL",
    "datawayUrl",
  );
  const datakitUrl = reader.readString(
    "GUANCE_RUM_DATAKIT_URL",
    "datakitUrl",
  );

  if (environmentDatakitUrl !== undefined) {
    return { datawayUrl: "", datakitUrl: environmentDatakitUrl };
  }
  if (environmentDatawayUrl !== undefined) {
    return { datawayUrl: environmentDatawayUrl, datakitUrl: "" };
  }
  if (datakitUrl) {
    return { datawayUrl: "", datakitUrl };
  }
  if (datawayUrl) {
    return { datawayUrl, datakitUrl: "" };
  }
  return { datawayUrl: "", datakitUrl: defaultDatakitUrl };
}

function resolveAllowedTraceUrls(reader) {
  const environmentName = reader.readEnvironmentString(
    "GUANCE_TRACE_ALLOWED_URLS",
  ) === undefined
    ? "GUANCE_RUM_ALLOWED_TRACING_URLS"
    : "GUANCE_TRACE_ALLOWED_URLS";
  return reader.readStringArray(environmentName, "allowedTracingUrls", []);
}

function resolveWebViewUrl(reader) {
  return (
    reader.readString(
      "GUANCE_RUM_ELECTRON_REMOTE_URL",
      "electronRemoteUrl",
    ) ||
    reader.readString("GUANCE_RUM_WEBVIEW_URL", "webViewUrl")
  );
}

function isSupportedWebViewUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

module.exports = {
  createLocalRumSettingsReader,
  isSupportedWebViewUrl,
  loadLocalRumSettings,
  resolveAllowedTraceUrls,
  resolveRumIngestionConfiguration,
  resolveWebViewUrl,
};
