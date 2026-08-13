import type { DesktopMonitoringEnvironment } from "./contracts";

export interface GuanceConfigResult {
  enabled: boolean;
  reason?: string;
  intakeMode?: "bridge";
  config?: Record<string, unknown>;
}

// Browser RUM validates that an intake option exists before bridge mode takes
// over. No request is sent to this placeholder: FTWebViewJavascriptBridge owns
// event delivery before init runs, and the Windows native host owns upload.
export const BRIDGE_VALIDATION_ORIGIN = "http://127.0.0.1";

export function buildRumConfig(
  bridgeEnabled: boolean,
  rum: DesktopMonitoringEnvironment["rum"],
  trace: DesktopMonitoringEnvironment["trace"],
): GuanceConfigResult {
  if (!bridgeEnabled || !rum.enabled) {
    return {
      enabled: false,
      reason: "Windows native RUM Bridge is disabled. Check the native application ID and intake URL.",
    };
  }

  return {
    enabled: true,
    intakeMode: "bridge",
    config: {
      applicationId: "00000000-aaaa-0000-aaaa-000000000000",
      datakitOrigin: BRIDGE_VALIDATION_ORIGIN,
      sessionSampleRate: 100,
      sessionReplaySampleRate: rum.sessionReplay.enabled ? 100 : 0,
      sessionReplayOnErrorSampleRate: 0,
      trackUserInteractions: true,
      trackViewsManually: true,
      actionNameAttribute: "data-guance-action-name",
      compressIntakeRequests: false,
      silentMultipleInit: true,
      ...(trace.enabled
        ? {
            tracingSampleRate: trace.sampleRate,
            traceType: trace.type,
            allowedTracingUrls: trace.allowedUrls,
          }
        : {}),
    },
  };
}
