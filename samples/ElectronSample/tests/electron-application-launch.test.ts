import { describe, expect, it, vi } from "vitest";

const {
  ElectronApplicationLaunchTracker,
} = require("../src/main/electron-application-launch.cjs");

describe("Electron application launch tracker", () => {
  it("emits cold launch phases from process start through first frame", () => {
    let unixNanoseconds = 1_000n;
    let monotonicNanoseconds = 100n;
    const sendLaunch = vi.fn(() => true);
    const tracker = new ElectronApplicationLaunchTracker({
      now: () => ({ unixNanoseconds, monotonicNanoseconds }),
      processUptimeNanoseconds: 100n,
    });

    unixNanoseconds = 1_100n;
    monotonicNanoseconds = 200n;
    tracker.markSdkInitialized();
    unixNanoseconds = 1_300n;
    monotonicNanoseconds = 400n;
    const windowCreated = tracker.markWindowCreated();
    unixNanoseconds = 1_600n;
    monotonicNanoseconds = 700n;

    expect(tracker.reportCold({ sendLaunch }, windowCreated)).toBe(true);
    expect(sendLaunch).toHaveBeenCalledWith({
      type: "cold",
      startTimeNanoseconds: 900n,
      durationNanoseconds: 700n,
      preApplicationDurationNanoseconds: 200n,
      applicationDurationNanoseconds: 200n,
      firstFrameDurationNanoseconds: 300n,
    });
  });

  it("emits hot launch only after ten seconds in the background", async () => {
    let unixNanoseconds = 1_000_000_000_000n;
    let monotonicNanoseconds = 1_000n;
    const sendLaunch = vi.fn(() => true);
    const tracker = new ElectronApplicationLaunchTracker({
      now: () => ({ unixNanoseconds, monotonicNanoseconds }),
      processUptimeNanoseconds: 0n,
    });

    tracker.enterBackground();
    unixNanoseconds += 9_000_000_000n;
    monotonicNanoseconds += 9_000_000_000n;
    expect(tracker.beginForeground()).toBeUndefined();

    tracker.enterBackground();
    unixNanoseconds += 10_000_000_000n;
    monotonicNanoseconds += 10_000_000_000n;
    const foreground = tracker.beginForeground();
    expect(foreground).toBeDefined();
    unixNanoseconds += 50_000_000n;
    monotonicNanoseconds += 50_000_000n;
    const executeJavaScript = vi.fn(() => Promise.resolve(true));
    const window = {
      isDestroyed: () => false,
      webContents: {
        isDestroyed: () => false,
        executeJavaScript,
      },
    };

    await tracker.reportHotAfterFrame({ sendLaunch }, window, foreground);

    expect(executeJavaScript).toHaveBeenCalledOnce();
    expect(sendLaunch).toHaveBeenCalledWith({
      type: "hot",
      startTimeNanoseconds: foreground.unixNanoseconds,
      durationNanoseconds: 50_000_000n,
    });
  });

  it("does not emit hot launch when a renderer frame cannot be confirmed", async () => {
    const sendLaunch = vi.fn(() => true);
    const tracker = new ElectronApplicationLaunchTracker({
      now: () => ({ unixNanoseconds: 1_000n, monotonicNanoseconds: 1_000n }),
      processUptimeNanoseconds: 0n,
      logger: { warn: vi.fn() },
    });
    const window = {
      isDestroyed: () => false,
      webContents: {
        isDestroyed: () => false,
        executeJavaScript: () => Promise.reject(new Error("renderer crashed")),
      },
    };

    const sent = await tracker.reportHotAfterFrame(
      { sendLaunch },
      window,
      { unixNanoseconds: 900n, monotonicNanoseconds: 900n },
    );

    expect(sent).toBe(false);
    expect(sendLaunch).not.toHaveBeenCalled();
  });
});
