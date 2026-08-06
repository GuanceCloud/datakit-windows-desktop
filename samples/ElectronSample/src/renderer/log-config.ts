import type { DesktopMonitoringEnvironment } from "./contracts";
import { BRIDGE_VALIDATION_ORIGIN } from "./rum-config";

export interface LogConfigResult {
  enabled: boolean;
  config?: Record<string, unknown>;
}

export function buildLogConfig(
  bridgeEnabled: boolean,
  log: DesktopMonitoringEnvironment["log"],
): LogConfigResult {
  if (!bridgeEnabled || !log.enabled) {
    return { enabled: false };
  }

  return {
    enabled: true,
    config: {
      datakitOrigin: BRIDGE_VALIDATION_ORIGIN,
      sessionSampleRate: 100,
      forwardErrorsToLogs: true,
      forwardConsoleLogs: ["error", "warn"],
      silentMultipleInit: true,
    },
  };
}
