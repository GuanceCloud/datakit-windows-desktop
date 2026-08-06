import { describe, expect, it } from "vitest";
import { buildLogConfig } from "../src/renderer/log-config";

describe("buildLogConfig", () => {
  it("starts Browser Logs only when the bridge and parallel Log feature are enabled", () => {
    expect(buildLogConfig(true, { enabled: false })).toEqual({ enabled: false });
    expect(buildLogConfig(false, { enabled: true })).toEqual({ enabled: false });

    expect(buildLogConfig(true, { enabled: true }).config).toMatchObject({
      datakitOrigin: "http://127.0.0.1",
      sessionSampleRate: 100,
      forwardErrorsToLogs: true,
    });
  });
});
