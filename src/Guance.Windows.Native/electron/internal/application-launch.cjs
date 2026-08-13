"use strict";

const NANOSECONDS_PER_MILLISECOND = 1_000_000n;
const BACKGROUND_THRESHOLD_NS = 10_000_000_000n;
const DEFAULT_COLD_VIEW_WAIT_MS = 1_000;

function systemMoment() {
  return {
    unixNanoseconds: BigInt(Date.now()) * NANOSECONDS_PER_MILLISECOND,
    monotonicNanoseconds: process.hrtime.bigint(),
  };
}

function elapsed(start, end) {
  return end > start ? end - start : 0n;
}

function addSaturated(left, right) {
  const maximum = 9_223_372_036_854_775_807n;
  return right > maximum - left ? maximum : left + right;
}

function isDestroyed(webContents) {
  return typeof webContents?.isDestroyed === "function" && webContents.isDestroyed();
}

function confirmDoubleAnimationFrame(webContents) {
  if (!webContents || isDestroyed(webContents) ||
      typeof webContents.executeJavaScript !== "function") {
    return Promise.reject(new Error(
      "Electron hot launch first-frame confirmation requires a live WebContents.",
    ));
  }
  return Promise.resolve(webContents.executeJavaScript(
    "new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)))",
    true,
  ));
}

class ElectronApplicationLaunchTracker {
  constructor({
    sendLaunch,
    onError,
    now = systemMoment,
    processUptimeNanoseconds = BigInt(Math.max(
      0,
      Math.floor(process.uptime() * 1e9),
    )),
    coldViewWaitMs = DEFAULT_COLD_VIEW_WAIT_MS,
    scheduleImmediate = setImmediate,
    cancelImmediate = clearImmediate,
    scheduleTimeout = setTimeout,
    cancelTimeout = clearTimeout,
    confirmFirstFrame = confirmDoubleAnimationFrame,
  }) {
    if (typeof sendLaunch !== "function") {
      throw new Error("Electron launch tracking requires sendLaunch().");
    }
    this.sendLaunch = sendLaunch;
    this.onError = onError;
    this.now = now;
    this.scheduleImmediate = scheduleImmediate;
    this.cancelImmediate = cancelImmediate;
    this.scheduleTimeout = scheduleTimeout;
    this.cancelTimeout = cancelTimeout;
    this.confirmFirstFrame = confirmFirstFrame;
    this.coldViewWaitMs = coldViewWaitMs;
    this.sdkInitialized = now();
    this.processStartMonotonicNanoseconds =
      this.sdkInitialized.monotonicNanoseconds - processUptimeNanoseconds;
    this.processStartUnixNanoseconds =
      this.sdkInitialized.unixNanoseconds - processUptimeNanoseconds;
    this.firstWindowCreated = undefined;
    this.firstWindowWebContents = undefined;
    this.windows = new Map();
    this.trustedViews = new WeakMap();
    this.pendingCold = undefined;
    this.pendingColdTimer = undefined;
    this.backgroundedAt = undefined;
    this.backgroundCheck = undefined;
    this.hotPending = false;
    this.coldMeasured = false;
    this.coldReported = false;
    this.disposed = false;
  }

  report(error) {
    if (typeof this.onError === "function") this.onError(error);
  }

  attachWindow(windowOrWebContents) {
    if (this.disposed) {
      throw new Error("Electron launch tracking is already disposed.");
    }
    const browserWindow = windowOrWebContents?.webContents
      ? windowOrWebContents
      : undefined;
    const webContents = browserWindow?.webContents || windowOrWebContents;
    if (!webContents || typeof webContents !== "object") {
      throw new Error("Electron launch tracking requires a BrowserWindow or WebContents.");
    }

    const created = this.now();
    const isFirstWindow = !this.firstWindowCreated;
    if (isFirstWindow) {
      this.firstWindowCreated = created;
      this.firstWindowWebContents = webContents;
    }
    const record = {
      browserWindow,
      webContents,
      focused: Boolean(browserWindow && typeof browserWindow.isFocused === "function" &&
        browserWindow.isFocused()),
      listeners: [],
    };
    this.windows.set(webContents, record);

    const listen = (target, event, handler) => {
      if (!target || typeof target.on !== "function" ||
          typeof target.removeListener !== "function") return false;
      target.on(event, handler);
      record.listeners.push(() => target.removeListener(event, handler));
      return true;
    };

    if (isFirstWindow) {
      let fallbackTimer;
      const completeCold = () => {
        if (fallbackTimer !== undefined) {
          this.cancelTimeout(fallbackTimer);
          fallbackTimer = undefined;
        }
        this.completeCold(created);
      };
      const supportsReadyToShow = listen(browserWindow, "ready-to-show", completeCold);
      if (!supportsReadyToShow) {
        listen(webContents, "did-finish-load", completeCold);
      } else {
        listen(webContents, "did-finish-load", () => {
          if (this.coldMeasured || fallbackTimer !== undefined) return;
          fallbackTimer = this.scheduleTimeout(completeCold, 1_000);
          if (typeof fallbackTimer?.unref === "function") fallbackTimer.unref();
        });
        if (typeof browserWindow.isVisible === "function" && browserWindow.isVisible()) {
          const immediate = this.scheduleImmediate(completeCold);
          record.listeners.push(() => this.cancelImmediate(immediate));
        }
      }
      record.listeners.push(() => {
        if (fallbackTimer !== undefined) this.cancelTimeout(fallbackTimer);
        fallbackTimer = undefined;
      });
    }

    listen(browserWindow, "focus", () => {
      record.focused = true;
      this.windowFocused(record);
    });
    listen(browserWindow, "blur", () => {
      record.focused = false;
      this.scheduleBackgroundCheck();
    });
    listen(browserWindow, "closed", () => {
      this.detachWindow(webContents);
    });

    let detached = false;
    return () => {
      if (detached) return;
      detached = true;
      this.detachWindow(webContents);
    };
  }

  detachWindow(webContents) {
    const record = this.windows.get(webContents);
    if (!record) return;
    this.windows.delete(webContents);
    for (const remove of record.listeners.splice(0)) remove();
    this.scheduleBackgroundCheck();
  }

  completeCold(windowCreated) {
    if (this.disposed || this.coldMeasured) return;
    this.coldMeasured = true;
    const firstFrame = this.now();
    const created = this.firstWindowCreated || windowCreated || this.sdkInitialized;
    const preApplication = elapsed(
      this.processStartMonotonicNanoseconds,
      this.sdkInitialized.monotonicNanoseconds,
    );
    const application = elapsed(
      this.sdkInitialized.monotonicNanoseconds,
      created.monotonicNanoseconds,
    );
    const firstFrameDuration = elapsed(
      created.monotonicNanoseconds,
      firstFrame.monotonicNanoseconds,
    );
    this.pendingCold = {
      type: "cold",
      startTimeNanoseconds: this.processStartUnixNanoseconds,
      durationNanoseconds: addSaturated(
        addSaturated(preApplication, application),
        firstFrameDuration,
      ),
      preApplicationDurationNanoseconds: preApplication,
      applicationDurationNanoseconds: application,
      firstFrameDurationNanoseconds: firstFrameDuration,
    };

    const view = this.trustedViews.get(this.firstWindowWebContents);
    if (view) {
      this.flushCold(view);
      return;
    }
    this.pendingColdTimer = this.scheduleTimeout(
      () => this.flushCold(undefined),
      this.coldViewWaitMs,
    );
    if (typeof this.pendingColdTimer?.unref === "function") {
      this.pendingColdTimer.unref();
    }
  }

  observeTrustedView(webContents, view) {
    if (this.disposed || !webContents || !view) return;
    this.trustedViews.set(webContents, view);
    if (this.pendingCold) this.flushCold(view);
  }

  flushCold(view) {
    if (!this.pendingCold || this.coldReported) return;
    if (this.pendingColdTimer !== undefined) {
      this.cancelTimeout(this.pendingColdTimer);
      this.pendingColdTimer = undefined;
    }
    const launch = this.pendingCold;
    this.pendingCold = undefined;
    try {
      this.sendLaunch(launch, view);
      this.coldReported = true;
    } catch (error) {
      this.report(error);
    }
  }

  allWindowsUnfocused() {
    if (this.windows.size === 0) return true;
    return [...this.windows.values()].every((record) => {
      if (record.browserWindow && typeof record.browserWindow.isFocused === "function") {
        return !record.browserWindow.isFocused();
      }
      return !record.focused;
    });
  }

  scheduleBackgroundCheck() {
    if (this.disposed || !this.coldMeasured || this.backgroundCheck !== undefined) return;
    this.backgroundCheck = true;
    const scheduled = this.scheduleImmediate(() => {
      this.backgroundCheck = undefined;
      if (!this.disposed && this.allWindowsUnfocused() && !this.backgroundedAt) {
        this.backgroundedAt = this.now();
      }
    });
    if (this.backgroundCheck !== undefined) this.backgroundCheck = scheduled;
  }

  windowFocused(record) {
    if (this.disposed || !this.coldMeasured || !this.backgroundedAt || this.hotPending) {
      return;
    }
    const focused = this.now();
    const backgroundDuration = elapsed(
      this.backgroundedAt.monotonicNanoseconds,
      focused.monotonicNanoseconds,
    );
    this.backgroundedAt = undefined;
    if (backgroundDuration < BACKGROUND_THRESHOLD_NS) return;

    this.hotPending = true;
    Promise.resolve(this.confirmFirstFrame(record.webContents))
      .then(() => {
        if (this.disposed) return;
        const firstFrame = this.now();
        this.sendLaunch({
          type: "hot",
          startTimeNanoseconds: focused.unixNanoseconds,
          durationNanoseconds: elapsed(
            focused.monotonicNanoseconds,
            firstFrame.monotonicNanoseconds,
          ),
          preApplicationDurationNanoseconds: 0n,
          applicationDurationNanoseconds: 0n,
          firstFrameDurationNanoseconds: 0n,
        }, this.trustedViews.get(record.webContents));
      })
      .catch((error) => this.report(error))
      .finally(() => { this.hotPending = false; });
  }

  dispose() {
    if (this.disposed) return;
    this.flushCold(undefined);
    this.disposed = true;
    if (this.pendingColdTimer !== undefined) {
      this.cancelTimeout(this.pendingColdTimer);
      this.pendingColdTimer = undefined;
    }
    if (this.backgroundCheck !== undefined) {
      this.cancelImmediate(this.backgroundCheck);
      this.backgroundCheck = undefined;
    }
    for (const webContents of [...this.windows.keys()]) {
      this.detachWindow(webContents);
    }
    this.backgroundedAt = undefined;
  }
}

module.exports = {
  BACKGROUND_THRESHOLD_NS,
  ElectronApplicationLaunchTracker,
  confirmDoubleAnimationFrame,
  elapsed,
  systemMoment,
};
