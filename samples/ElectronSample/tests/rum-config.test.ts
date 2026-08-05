import { describe, expect, it } from "vitest";
import type { DesktopRumEnvironment } from "../src/renderer/contracts";
import { buildRumConfig } from "../src/renderer/rum-config";

function environment(overrides: Partial<DesktopRumEnvironment> = {}): DesktopRumEnvironment {
  return {
    enabled: true,
    debug: true,
    sessionReplayEnabled: false,
    sessionReplayPrivacyLevel: "mask",
    userId: "desktop-test-user",
    ...overrides,
  };
}

describe("buildRumConfig", () => {
  it("stays disabled when the Windows native bridge is unavailable", () => {
    const result = buildRumConfig(environment({ enabled: false }));

    expect(result.enabled).toBe(false);
    expect(result.reason).toContain("Windows 原生 RUM Bridge");
  });

  it("builds a collector-only Browser RUM configuration for native bridge mode", () => {
    const result = buildRumConfig(environment());

    expect(result.enabled).toBe(true);
    expect(result.intakeMode).toBe("bridge");
    expect(result.config).toMatchObject({
      applicationId: "00000000-aaaa-0000-aaaa-000000000000",
      datakitOrigin: "http://127.0.0.1",
      trackUserInteractions: true,
      trackViewsManually: true,
      sessionReplaySampleRate: 0,
    });
    expect(result.config).not.toHaveProperty("clientToken");
    expect(result.config).not.toHaveProperty("site");
    expect(result.config).not.toHaveProperty("service");
    expect(result.config).not.toHaveProperty("sessionPersistence");
  });

  it("allows experimental replay when the native bridge enables it", () => {
    const result = buildRumConfig(environment({ sessionReplayEnabled: true }));

    expect(result.config).toMatchObject({
      sessionReplaySampleRate: 100,
      sessionReplayOnErrorSampleRate: 0,
      compressIntakeRequests: false,
    });
  });
});
