"use strict";

const { browserRumViewContext } = require("./browser-rum-line-protocol.cjs");

const MONITORING_DISABLED_ARGUMENT = "--guance-monitoring-disabled";
const REPLAY_ARGUMENT_PREFIX = "--guance-rum-replay=";
const COLD_LAUNCH_VIEW_WAIT_MS = 1000;
const SUPPORTED_PRIVACY_LEVELS = new Set(["allow", "mask-user-input", "mask"]);

function booleanOverride(value, fallback) {
  return typeof value === "boolean" ? value : fallback;
}

function normalizePrivacyLevel(value, fallback = "mask") {
  return SUPPORTED_PRIVACY_LEVELS.has(value) ? value : fallback;
}

function normalizeNativePolicy(policy = {}) {
  return {
    rum: {
      enabled: Boolean(policy.rum?.enabled),
      sessionReplay: {
        enabled: Boolean(policy.rum?.enabled && policy.rum?.sessionReplay?.enabled),
        privacyLevel: normalizePrivacyLevel(policy.rum?.sessionReplay?.privacyLevel),
      },
    },
    log: { enabled: Boolean(policy.log?.enabled) },
    trace: {
      enabled: Boolean(policy.trace?.enabled),
      sampleRate: Number.isFinite(policy.trace?.sampleRate)
        ? Math.min(100, Math.max(0, policy.trace.sampleRate))
        : 100,
      type: policy.trace?.type || "w3c_traceparent",
      allowedUrls: Array.isArray(policy.trace?.allowedUrls)
        ? policy.trace.allowedUrls.filter((value) => typeof value === "string" && value.length > 0)
        : [],
    },
  };
}

function mergePageConfig(base = {}, override = {}) {
  return {
    ...base,
    ...override,
    rum: {
      ...base.rum,
      ...override.rum,
      sessionReplay: {
        ...base.rum?.sessionReplay,
        ...override.rum?.sessionReplay,
      },
    },
    log: { ...base.log, ...override.log },
    trace: { ...base.trace, ...override.trace },
  };
}

function resolvePagePolicy(nativePolicy, electronConfig, pageId) {
  const defaultPage = electronConfig?.defaultPage || { enabled: true };
  const configured = mergePageConfig(defaultPage, electronConfig?.pages?.[pageId]);
  const pageEnabled = configured.enabled !== false;
  const rumEnabled = pageEnabled && nativePolicy.rum.enabled &&
    booleanOverride(configured.rum?.enabled, true);
  const replayEnabled = rumEnabled && nativePolicy.rum.sessionReplay.enabled &&
    booleanOverride(configured.rum?.sessionReplay?.enabled, true);
  const logEnabled = pageEnabled && nativePolicy.log.enabled &&
    booleanOverride(configured.log?.enabled, true);
  const traceEnabled = pageEnabled && nativePolicy.trace.enabled && rumEnabled &&
    booleanOverride(configured.trace?.enabled, true);

  return {
    enabled: pageEnabled,
    rum: {
      enabled: rumEnabled,
      sessionReplay: {
        enabled: replayEnabled,
        privacyLevel: normalizePrivacyLevel(
          configured.rum?.sessionReplay?.privacyLevel,
          nativePolicy.rum.sessionReplay.privacyLevel,
        ),
      },
    },
    log: { enabled: logEnabled },
    trace: {
      enabled: traceEnabled,
      sampleRate: Number.isFinite(configured.trace?.sampleRate)
        ? Math.min(100, Math.max(0, configured.trace.sampleRate))
        : nativePolicy.trace.sampleRate,
      type: configured.trace?.type || nativePolicy.trace.type,
      allowedUrls: Array.isArray(configured.trace?.allowedUrls)
        ? [...configured.trace.allowedUrls]
        : [...nativePolicy.trace.allowedUrls],
    },
  };
}

function bridgeEventAllowed(policy, serializedEvent) {
  try {
    const envelope = JSON.parse(serializedEvent);
    if (envelope?.name === "rum") {
      return policy.rum.enabled;
    }
    if (envelope?.name === "log") {
      return policy.log.enabled;
    }
    if (envelope?.name === "session_replay") {
      return policy.rum.sessionReplay.enabled;
    }
    return false;
  } catch {
    return false;
  }
}

class ElectronMonitoringIntegration {
  constructor({ mode, nativeBridge, nativePolicy, electron, ownsNativeBridge }) {
    if (!nativeBridge || typeof nativeBridge.send !== "function") {
      throw new Error("nativeBridge.send(serializedEvent, rendererLabel) is required.");
    }
    this.mode = mode;
    this.nativeBridge = nativeBridge;
    this.nativePolicy = normalizeNativePolicy(nativePolicy);
    this.electron = electron || {};
    this.ownsNativeBridge = ownsNativeBridge;
    this.renderers = new Map();
    this.mainRenderer = undefined;
    this.mainView = undefined;
    this.pendingColdLaunch = undefined;
    this.pendingColdLaunchTimer = undefined;
  }

  pagePolicy(pageId) {
    return resolvePagePolicy(this.nativePolicy, this.electron, pageId);
  }

  createPreloadArguments(pageId, additionalArguments = []) {
    const policy = this.pagePolicy(pageId);
    const argumentsForPreload = [...additionalArguments];
    if (!policy.enabled) {
      argumentsForPreload.push(MONITORING_DISABLED_ARGUMENT);
      return argumentsForPreload;
    }
    if (policy.rum.sessionReplay.enabled) {
      argumentsForPreload.push(
        `${REPLAY_ARGUMENT_PREFIX}${policy.rum.sessionReplay.privacyLevel}`,
      );
    }
    return argumentsForPreload;
  }

  createPageIntegration(pageId, {
    preload,
    additionalArguments = [],
    rendererLabel = pageId,
  }) {
    if (typeof preload !== "string" || preload.length === 0) {
      throw new Error("A preload path is required for Electron page integration.");
    }
    const policy = this.pagePolicy(pageId);
    return {
      policy,
      webPreferences: {
        preload,
        additionalArguments: this.createPreloadArguments(pageId, additionalArguments),
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
      attach: (webContents) => this.registerWebContents(
        webContents,
        pageId,
        rendererLabel,
      ),
    };
  }

  registerWebContents(webContents, pageId, rendererLabel = pageId) {
    const policy = this.pagePolicy(pageId);
    if (!policy.enabled) {
      return policy;
    }
    this.renderers.set(webContents, { pageId, rendererLabel, policy });
    if (pageId === "main") {
      this.mainRenderer = webContents;
      this.mainView = undefined;
    }
    webContents.once?.("destroyed", () => this.unregisterWebContents(webContents));
    return policy;
  }

  unregisterWebContents(webContents) {
    this.renderers.delete(webContents);
    if (this.mainRenderer === webContents) {
      this.flushPendingColdLaunch();
      this.mainRenderer = undefined;
      this.mainView = undefined;
    }
  }

  trustedRenderer(event) {
    const registration = this.renderers.get(event.sender);
    if (!registration || event.senderFrame !== event.sender.mainFrame) {
      return undefined;
    }
    return registration;
  }

  send(event, serializedEvent) {
    const registration = this.trustedRenderer(event);
    if (!registration || !bridgeEventAllowed(registration.policy, serializedEvent)) {
      return false;
    }
    let view;
    if (registration.pageId === "main") {
      try {
        view = browserRumViewContext(serializedEvent);
      } catch {
        return false;
      }
    }
    const accepted = this.nativeBridge.send(serializedEvent, registration.rendererLabel);
    if (accepted && view) {
      this.mainView = view;
      this.flushPendingColdLaunch();
    }
    return accepted;
  }

  flushPendingColdLaunch() {
    if (!this.pendingColdLaunch) {
      return false;
    }
    if (this.pendingColdLaunchTimer) {
      clearTimeout(this.pendingColdLaunchTimer);
      this.pendingColdLaunchTimer = undefined;
    }
    const launch = this.pendingColdLaunch;
    this.pendingColdLaunch = undefined;
    return this.nativeBridge.sendLaunch?.({ ...launch, view: this.mainView }) ?? false;
  }

  sendProcessFailure(failure) {
    return this.nativeBridge.sendProcessFailure?.(failure) ?? false;
  }

  sendLaunch(launch) {
    if (typeof this.nativeBridge.sendLaunch !== "function") {
      return false;
    }
    if (
      launch?.type === "cold" &&
      this.mainRenderer &&
      !this.mainView
    ) {
      if (this.pendingColdLaunch) {
        return false;
      }
      this.pendingColdLaunch = launch;
      this.pendingColdLaunchTimer = setTimeout(
        () => this.flushPendingColdLaunch(),
        COLD_LAUNCH_VIEW_WAIT_MS,
      );
      this.pendingColdLaunchTimer.unref?.();
      return true;
    }
    return this.nativeBridge.sendLaunch?.({ ...launch, view: this.mainView }) ?? false;
  }

  shutdown() {
    this.flushPendingColdLaunch();
    this.renderers.clear();
    this.mainRenderer = undefined;
    this.mainView = undefined;
    if (this.ownsNativeBridge) {
      return this.nativeBridge.shutdown?.() || Promise.resolve();
    }
    return this.nativeBridge.disconnect?.() || Promise.resolve();
  }
}

function initializeElectronMonitoring({
  configuration,
  electron,
  createNativeBridge,
}) {
  if (typeof createNativeBridge !== "function") {
    throw new Error("createNativeBridge(configuration) is required.");
  }
  const nativeBridge = createNativeBridge(configuration);
  nativeBridge.start?.();
  return new ElectronMonitoringIntegration({
    mode: "electron-owned",
    nativeBridge,
    nativePolicy: configuration,
    electron: electron || configuration.electron,
    ownsNativeBridge: true,
  });
}

function connectElectronMonitoring({ nativeBridge, nativePolicy, electron }) {
  return new ElectronMonitoringIntegration({
    mode: "native-owned",
    nativeBridge,
    nativePolicy,
    electron,
    ownsNativeBridge: false,
  });
}

module.exports = {
  COLD_LAUNCH_VIEW_WAIT_MS,
  MONITORING_DISABLED_ARGUMENT,
  REPLAY_ARGUMENT_PREFIX,
  ElectronMonitoringIntegration,
  connectElectronMonitoring,
  initializeElectronMonitoring,
  resolvePagePolicy,
};
