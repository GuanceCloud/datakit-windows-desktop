import { describe, expect, it } from "vitest";
import type { DesktopMonitoringEnvironment } from "../src/renderer/contracts";
import { buildRumConfig } from "../src/renderer/rum-config";

function monitoring(
  overrides: Partial<DesktopMonitoringEnvironment> = {},
): DesktopMonitoringEnvironment {
  return {
    bridgeEnabled: true,
    debug: true,
    userId: "desktop-test-user",
    rum: {
      enabled: true,
      sessionReplay: { enabled: false, privacyLevel: "mask" },
    },
    log: { enabled: true },
    trace: {
      enabled: false,
      sampleRate: 100,
      type: "w3c_traceparent",
      allowedUrls: [],
    },
    ...overrides,
  };
}

function build(environment = monitoring()) {
  return buildRumConfig(
    environment.bridgeEnabled,
    environment.rum,
    environment.trace,
  );
}

describe("buildRumConfig", () => {
  it("stays disabled when the Windows native bridge is unavailable", () => {
    const result = build(monitoring({ bridgeEnabled: false }));

    expect(result.enabled).toBe(false);
    expect(result.reason).toContain("Windows native RUM Bridge");
  });

  it("builds a collector-only Browser RUM configuration for native bridge mode", () => {
    const result = build();

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

  it("allows experimental replay when the RUM feature enables it", () => {
    const environment = monitoring();
    environment.rum.sessionReplay.enabled = true;
    const result = build(environment);

    expect(result.config).toMatchObject({
      sessionReplaySampleRate: 100,
      sessionReplayOnErrorSampleRate: 0,
      compressIntakeRequests: false,
    });
  });

  it("composes the parallel Trace policy into the Browser RUM adapter", () => {
    const environment = monitoring({
      trace: {
        enabled: true,
        sampleRate: 75,
        type: "w3c_traceparent",
        allowedUrls: ["https://api.example.test"],
      },
    });
    const result = build(environment);

    expect(result.config).toMatchObject({
      tracingSampleRate: 75,
      traceType: "w3c_traceparent",
      allowedTracingUrls: ["https://api.example.test"],
    });
  });
});
