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
  applicationId: string;
  clientToken: string;
  site: string;
  datakitOrigin: string;
  service: string;
  env: string;
  version: string;
  debug: boolean;
  sessionSampleRate: number;
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
