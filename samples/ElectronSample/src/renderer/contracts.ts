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

export interface DesktopRumEnvironment {
  enabled: boolean;
  debug: boolean;
  sessionReplayEnabled: boolean;
  sessionReplayPrivacyLevel: "allow" | "mask-user-input" | "mask";
  userId: string;
}

export interface DesktopBootstrap {
  app: DesktopAppInfo;
  rum: DesktopRumEnvironment;
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
}

declare global {
  interface Window {
    guanceDesktop?: DesktopBridge;
  }
}
