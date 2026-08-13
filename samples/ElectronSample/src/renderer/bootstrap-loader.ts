import type { DesktopBootstrap, DesktopBridge } from "./contracts";

interface RendererLocation {
  href: string;
  pathname: string;
  protocol: string;
}

type BootstrapBridge = Pick<DesktopBridge, "getBootstrap">;
type BootstrapFetch = (input: string | URL) => Promise<Response>;

function isBuiltInRemoteRenderer(location: RendererLocation): boolean {
  const remotePath = location.pathname === "/remote" || location.pathname.startsWith("/remote/");
  return (location.protocol === "http:" || location.protocol === "https:") && remotePath;
}

export async function loadDesktopBootstrap(
  bridge: BootstrapBridge | undefined,
  location: RendererLocation,
  fetchBootstrap: BootstrapFetch,
): Promise<DesktopBootstrap> {
  if (bridge) {
    return bridge.getBootstrap();
  }

  if (!isBuiltInRemoteRenderer(location)) {
    throw new Error(
      "Electron preload bridge was not detected. Open the Electron window with npm run dev instead of opening the Vite URL directly in a browser.",
    );
  }

  const response = await fetchBootstrap(new URL("bootstrap", location.href));
  if (!response.ok) {
    throw new Error(`Remote bootstrap failed with HTTP ${response.status}.`);
  }

  const contentType = response.headers.get("content-type") || "";
  if (!contentType.toLowerCase().includes("application/json")) {
    throw new Error(`Remote bootstrap returned ${contentType || "an unknown content type"} instead of JSON.`);
  }

  return response.json() as Promise<DesktopBootstrap>;
}
