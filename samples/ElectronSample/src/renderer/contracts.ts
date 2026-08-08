export interface DesktopAppInfo {
  name: string;
  version: string;
  platform: string;
  arch: string;
  osVersion: string;
  electronVersion: string;
  chromiumVersion: string;
  nodeVersion: string;
  packaged: boolean;
  rendererMode: "vite-dev-server" | "packaged-file" | "remote-http";
}

export type DesktopTraceType =
  | "ddtrace"
  | "zipkin"
  | "zipkin_single_header"
  | "w3c_traceparent"
  | "w3c_traceparent_64bit"
  | "skywalking_v3"
  | "jaeger";

export interface DesktopMonitoringEnvironment {
  bridgeEnabled: boolean;
  debug: boolean;
  userId: string;
  rum: {
    enabled: boolean;
    sessionReplay: {
      enabled: boolean;
      privacyLevel: "allow" | "mask-user-input" | "mask";
    };
  };
  log: {
    enabled: boolean;
  };
  trace: {
    enabled: boolean;
    sampleRate: number;
    type: DesktopTraceType;
    allowedUrls: string[];
  };
}

export interface DesktopBootstrap {
  app: DesktopAppInfo;
  monitoring: DesktopMonitoringEnvironment;
  hybrid: {
    localOrigin: string;
    remoteUrl: string;
    remoteAvailable: boolean;
    isRemoteRenderer: boolean;
    apiBaseUrl: string;
  };
}

export interface DesktopBridge {
  getBootstrap(): Promise<DesktopBootstrap>;
  minimize(): void;
  toggleMaximize(): Promise<boolean>;
  close(): void;
  selectWorkspace(): Promise<{ name: string } | null>;
  openRemoteWorkspace(): Promise<{
    opened: boolean;
    reused?: boolean;
    reason?: string;
    instrumentation?: "built-in" | "external";
  }>;
  showNotification(message: string): Promise<boolean>;
  runNativeAcceptanceScenario(): Promise<{
    accepted: boolean;
    scenarioId?: string;
    signals?: string[];
    reason?: string;
  }>;
  crashNativeBridge(): Promise<{
    accepted: boolean;
    recoveryFilter?: string;
    reason?: string;
  }>;
}

declare global {
  interface Window {
    guanceDesktop?: DesktopBridge;
  }
}
