import { describe, expect, it } from "vitest";
import type { DesktopRumEnvironment } from "../src/renderer/contracts";
import { buildRumConfig } from "../src/renderer/rum-config";

function environment(overrides: Partial<DesktopRumEnvironment> = {}): DesktopRumEnvironment {
  return {
    applicationId: "rum-windows-electron",
    clientToken: "",
    site: "",
    datakitOrigin: "http://127.0.0.1:9529",
    service: "guance-rum-windows-electron",
    env: "local",
    version: "0.1.0",
    debug: true,
    sessionSampleRate: 100,
    userId: "desktop-test-user",
    ...overrides,
  };
}

describe("buildRumConfig", () => {
  it("keeps the sample disabled until an application id is supplied", () => {
    const result = buildRumConfig(environment({ applicationId: "" }));

    expect(result.enabled).toBe(false);
    expect(result.reason).toContain("GUANCE_RUM_APP_ID");
  });

  it("builds a local DataKit configuration for a file renderer", () => {
    const result = buildRumConfig(environment());

    expect(result.enabled).toBe(true);
    expect(result.intakeMode).toBe("datakit");
    expect(result.config).toMatchObject({
      applicationId: "rum-windows-electron",
      datakitOrigin: "http://127.0.0.1:9529",
      sessionPersistence: "local-storage",
      trackUserInteractions: true,
      trackViewsManually: true,
    });
  });

  it("prefers public Dataway when the URL and client token are both present", () => {
    const result = buildRumConfig(
      environment({
        clientToken: "public-browser-token",
        site: "https://rum-openway.guance.com",
      }),
    );

    expect(result.intakeMode).toBe("dataway");
    expect(result.config).toMatchObject({
      clientToken: "public-browser-token",
      site: "https://rum-openway.guance.com",
    });
    expect(result.config).not.toHaveProperty("datakitOrigin");
  });

  it("hard-disables replay for the Phase 1 acceptance sample", () => {
    const result = buildRumConfig(environment());

    expect(result.config).toMatchObject({
      sessionReplaySampleRate: 0,
      sessionReplayOnErrorSampleRate: 0,
      compressIntakeRequests: false,
    });
  });

  it("normalizes unsupported environments and out-of-range sample rates", () => {
    const result = buildRumConfig(
      environment({
        env: "development",
        sessionSampleRate: 160,
      }),
    );

    expect(result.config).toMatchObject({
      env: "local",
      sessionSampleRate: 100,
    });
  });
});
