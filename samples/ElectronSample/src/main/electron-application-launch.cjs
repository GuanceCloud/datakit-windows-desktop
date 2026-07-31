"use strict";

const NANOSECONDS_PER_MILLISECOND = 1_000_000n;
const BACKGROUND_THRESHOLD_NS = 10_000_000_000n;

function systemLaunchMoment() {
  return {
    unixNanoseconds: BigInt(Date.now()) * NANOSECONDS_PER_MILLISECOND,
    monotonicNanoseconds: process.hrtime.bigint(),
  };
}

function elapsedNanoseconds(start, end) {
  return end > start ? end - start : 0n;
}

class ElectronApplicationLaunchTracker {
  constructor({
    now = systemLaunchMoment,
    processUptimeNanoseconds = BigInt(Math.max(0, Math.floor(process.uptime() * 1e9))),
    logger = console,
  } = {}) {
    this.now = now;
    this.logger = logger;
    const created = now();
    this.processStartMonotonicNanoseconds =
      created.monotonicNanoseconds - processUptimeNanoseconds;
    this.processStartUnixNanoseconds =
      created.unixNanoseconds - processUptimeNanoseconds;
    this.sdkInitialization = undefined;
    this.backgroundedAt = undefined;
    this.coldReported = false;
  }

  markSdkInitialized() {
    this.sdkInitialization = this.now();
  }

  markWindowCreated() {
    return this.now();
  }

  reportCold(nativeHost, windowCreated) {
    if (this.coldReported || !nativeHost || !this.sdkInitialization) {
      return false;
    }

    const firstFrame = this.now();
    const preApplicationDuration = elapsedNanoseconds(
      this.processStartMonotonicNanoseconds,
      this.sdkInitialization.monotonicNanoseconds,
    );
    const applicationDuration = elapsedNanoseconds(
      this.sdkInitialization.monotonicNanoseconds,
      windowCreated.monotonicNanoseconds,
    );
    const firstFrameDuration = elapsedNanoseconds(
      windowCreated.monotonicNanoseconds,
      firstFrame.monotonicNanoseconds,
    );
    this.coldReported = nativeHost.sendLaunch({
      type: "cold",
      startTimeNanoseconds: this.processStartUnixNanoseconds,
      durationNanoseconds:
        preApplicationDuration + applicationDuration + firstFrameDuration,
      preApplicationDurationNanoseconds: preApplicationDuration,
      applicationDurationNanoseconds: applicationDuration,
      firstFrameDurationNanoseconds: firstFrameDuration,
    });
    return this.coldReported;
  }

  enterBackground() {
    this.backgroundedAt = this.now().monotonicNanoseconds;
  }

  beginForeground() {
    const backgroundedAt = this.backgroundedAt;
    this.backgroundedAt = undefined;
    if (backgroundedAt === undefined) {
      return undefined;
    }

    const foreground = this.now();
    return elapsedNanoseconds(
      backgroundedAt,
      foreground.monotonicNanoseconds,
    ) >= BACKGROUND_THRESHOLD_NS
      ? foreground
      : undefined;
  }

  async reportHotAfterFrame(nativeHost, window, foreground) {
    if (window.isDestroyed() || window.webContents.isDestroyed()) {
      return false;
    }

    try {
      await window.webContents.executeJavaScript(
        "new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))",
        true,
      );
    } catch (error) {
      this.logger.warn(
        "[electron-main][rum-bridge] hot launch frame probe failed",
        error,
      );
      return false;
    }

    const firstFrame = this.now();
    return nativeHost?.sendLaunch({
      type: "hot",
      startTimeNanoseconds: foreground.unixNanoseconds,
      durationNanoseconds: elapsedNanoseconds(
        foreground.monotonicNanoseconds,
        firstFrame.monotonicNanoseconds,
      ),
    }) ?? false;
  }
}

module.exports = {
  ElectronApplicationLaunchTracker,
  elapsedNanoseconds,
};
