import type { DesktopRumEnvironment } from "./contracts";

export interface RumConfigResult {
  enabled: boolean;
  reason?: string;
  intakeMode?: "bridge";
  config?: Record<string, unknown>;
}

// Browser RUM validates that an intake option exists before bridge mode takes
// over. No request is sent to this placeholder: FTWebViewJavascriptBridge owns
// event delivery before init runs, and the Windows native host owns upload.
const BRIDGE_VALIDATION_ORIGIN = "http://127.0.0.1";

export function buildRumConfig(environment: DesktopRumEnvironment): RumConfigResult {
  if (!environment.enabled) {
    return {
      enabled: false,
      reason: "Windows 原生 RUM Bridge 未启用，请检查原生端应用 ID 和上报地址。",
    };
  }

  return {
    enabled: true,
    intakeMode: "bridge",
    config: {
      applicationId: "00000000-aaaa-0000-aaaa-000000000000",
      datakitOrigin: BRIDGE_VALIDATION_ORIGIN,
      sessionSampleRate: 100,
      sessionReplaySampleRate: environment.sessionReplayEnabled ? 100 : 0,
      sessionReplayOnErrorSampleRate: 0,
      trackUserInteractions: true,
      trackViewsManually: true,
      actionNameAttribute: "data-guance-action-name",
      compressIntakeRequests: false,
      silentMultipleInit: true,
    },
  };
}
