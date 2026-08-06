import { describe, expect, it, vi } from "vitest";

import { loadDesktopBootstrap } from "../src/renderer/bootstrap-loader";
import type { DesktopBootstrap } from "../src/renderer/contracts";

const bootstrap = {
  app: {
    name: "test",
    version: "0.1.0",
    platform: "win32",
    arch: "x64",
    osVersion: "test",
    electronVersion: "test",
    chromiumVersion: "test",
    nodeVersion: "test",
    packaged: false,
    rendererMode: "vite-dev-server",
  },
  monitoring: {
    bridgeEnabled: true,
    debug: false,
    userId: "test-user",
    rum: {
      enabled: true,
      sessionReplay: {
        enabled: false,
        privacyLevel: "mask",
      },
    },
    log: { enabled: true },
    trace: {
      enabled: true,
      sampleRate: 100,
      type: "w3c_traceparent",
      allowedUrls: ["http://127.0.0.1:5000"],
    },
  },
  hybrid: {
    localOrigin: "http://127.0.0.1:5173",
    remoteUrl: "",
    remoteAvailable: true,
    isRemoteRenderer: false,
    apiBaseUrl: "http://127.0.0.1:5000",
  },
} satisfies DesktopBootstrap;

describe("loadDesktopBootstrap", () => {
  it("uses the Electron preload bridge for the local renderer", async () => {
    const getBootstrap = vi.fn().mockResolvedValue(bootstrap);
    const fetchBootstrap = vi.fn();

    await expect(
      loadDesktopBootstrap(
        { getBootstrap },
        { href: "http://127.0.0.1:5173/", pathname: "/", protocol: "http:" },
        fetchBootstrap,
      ),
    ).resolves.toBe(bootstrap);

    expect(getBootstrap).toHaveBeenCalledOnce();
    expect(fetchBootstrap).not.toHaveBeenCalled();
  });

  it("rejects a Vite page opened in a normal browser with actionable guidance", async () => {
    const fetchBootstrap = vi.fn();

    await expect(
      loadDesktopBootstrap(
        undefined,
        { href: "http://127.0.0.1:5173/", pathname: "/", protocol: "http:" },
        fetchBootstrap,
      ),
    ).rejects.toThrow("Electron 窗口");

    expect(fetchBootstrap).not.toHaveBeenCalled();
  });

  it("loads JSON bootstrap data for the isolated built-in remote renderer", async () => {
    const fetchBootstrap = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(bootstrap), {
        headers: { "content-type": "application/json; charset=utf-8" },
      }),
    );

    await expect(
      loadDesktopBootstrap(
        undefined,
        { href: "http://127.0.0.1:5000/remote/", pathname: "/remote/", protocol: "http:" },
        fetchBootstrap,
      ),
    ).resolves.toEqual(bootstrap);

    expect(fetchBootstrap).toHaveBeenCalledWith(new URL("http://127.0.0.1:5000/remote/bootstrap"));
  });

  it("rejects an HTML fallback before attempting JSON parsing", async () => {
    const fetchBootstrap = vi.fn().mockResolvedValue(
      new Response("<!doctype html>", {
        headers: { "content-type": "text/html; charset=utf-8" },
      }),
    );

    await expect(
      loadDesktopBootstrap(
        undefined,
        { href: "http://127.0.0.1:5000/remote/", pathname: "/remote/", protocol: "http:" },
        fetchBootstrap,
      ),
    ).rejects.toThrow("instead of JSON");
  });
});
