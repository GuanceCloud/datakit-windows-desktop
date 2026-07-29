import type { DesktopRumEnvironment } from "./contracts";

export interface RumConfigResult {
  enabled: boolean;
  reason?: string;
  intakeMode?: "dataway" | "datakit";
  config?: Record<string, unknown>;
}

const SUPPORTED_ENVIRONMENTS = new Set(["prod", "gray", "pre", "common", "local"]);

function normalizeSampleRate(value: number): number {
  if (!Number.isFinite(value)) {
    return 100;
  }
  return Math.min(100, Math.max(0, value));
}

export function buildRumConfig(environment: DesktopRumEnvironment): RumConfigResult {
  if (!environment.applicationId) {
    return {
      enabled: false,
      reason: "缺少 GUANCE_RUM_APP_ID，当前以演示模式运行。",
    };
  }

  const hasDataway = Boolean(environment.site && environment.clientToken);
  const hasDatakit = Boolean(environment.datakitOrigin);
  if (!hasDataway && !hasDatakit) {
    return {
      enabled: false,
      reason: "需要配置 Dataway + ClientToken，或配置本地 DataKit。",
    };
  }

  const intake = hasDataway
    ? {
        clientToken: environment.clientToken,
        site: environment.site,
      }
    : {
        datakitOrigin: environment.datakitOrigin,
      };

  return {
    enabled: true,
    intakeMode: hasDataway ? "dataway" : "datakit",
    config: {
      applicationId: environment.applicationId,
      ...intake,
      service: environment.service || "guance-rum-windows-electron",
      env: SUPPORTED_ENVIRONMENTS.has(environment.env) ? environment.env : "local",
      version: environment.version || "0.1.0",
      sessionSampleRate: normalizeSampleRate(environment.sessionSampleRate),
      sessionReplaySampleRate: 0,
      sessionReplayOnErrorSampleRate: 0,
      trackUserInteractions: true,
      trackViewsManually: true,
      actionNameAttribute: "data-guance-action-name",
      sessionPersistence: "local-storage",
      compressIntakeRequests: false,
      silentMultipleInit: true,
    },
  };
}
