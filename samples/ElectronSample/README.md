# Electron Sample

Electron native-bridge RUM, Log, Trace, and experimental Session Replay sample.

The adapter separates initialization ownership from page collection policy:
`initializeElectronMonitoring()` lets Electron Main create and own the Native
Host for a full Electron application, while `connectElectronMonitoring()`
connects selected Electron pages to an SDK instance already initialized and
owned by a C++ application. Both modes use the same `defaultPage` plus `pages`
overrides, preload arguments, and trusted `webContents` registry.

Configuration, collector-only Browser initialization, native ownership boundaries, Replay opt-in, run instructions, and verification are documented in [`../../docs/electron-phase-1-acceptance.md`](../../docs/electron-phase-1-acceptance.md).

Security scope: this directory is a legacy compatibility and acceptance sample, not a production application and not part of the `Guance.Windows` NuGet package. It pins Electron `22.3.27` and emits a Windows x64 package intended to cover Windows 7 SP1 through Windows 11. Electron 22 is end-of-life, so this compatibility build receives no Chromium, Node.js, or Electron security updates; the managed Windows SDK remains a Windows 10+ target. Do not redistribute the generated executable or use it in production; applications that adopt the integration pattern must select and maintain an Electron release appropriate for their own security and operating-system requirements.

The main process also translates each window's `unresponsive`/`responsive` and
`render-process-gone` signals into a strict, allow-listed native bridge command.
Renderer hangs are reported only after recovery, with one measured duration per
incident. Browser RUM continues to own renderer JavaScript long tasks. This
adapter observes renderer/window process events; it does not claim to recover a
crash of the Electron main process itself.

Quick start:

```powershell
npm install
npm run dev
```

The Electron and managed samples share `../rum.local.json`. Copy `../rum.local.json.example`, then edit the ignored local file. This flat file is a Sample-only compatibility input; Electron normalizes it in the main process to common `transport`/`application`/`runtime` settings plus parallel `rum`, `log`, and `trace` capability objects. Environment variables override matching JSON values. Do not ship a token-bearing local file or expose it to renderer assets in production.

Keep environment-specific endpoints outside the repository. Use `GUANCE_RUM_DATAKIT_URL` for the intake endpoint and `GUANCE_RUM_WEBVIEW_URL` or `GUANCE_RUM_ELECTRON_REMOTE_URL` for the remote renderer URL. RUM is controlled independently by `GUANCE_RUM_ENABLED` and `GUANCE_RUM_SAMPLE_RATE`; Browser Log by `GUANCE_LOG_ENABLED` and `GUANCE_LOG_SAMPLE_RATE`; Browser Trace header injection by `GUANCE_TRACE_ENABLED`, `GUANCE_TRACE_SAMPLE_RATE`, `GUANCE_TRACE_TYPE`, and the comma-separated `GUANCE_TRACE_ALLOWED_URLS` allow-list. The former `GUANCE_RUM_ALLOWED_TRACING_URLS` name remains compatible. See `../../docs/electron-phase-1-acceptance.md` for the normalized production shape.

Disk and network budgets are also configurable in `rum.local.json`: `maxCacheBytes`, `maxCacheFiles`, `maxCacheAgeSeconds`, `maxBatchItems`, `maxBatchBytes`, `maxUploadBytesPerSecond`, `uploadBurstBytes`, `maxUploadRequestsPerSecond`, and `maxUploadBatchesPerCycle`. Matching `GUANCE_RUM_*` environment variables override the file values.

Session Replay is experimental and disabled by default. Set `GUANCE_RUM_SESSION_REPLAY_ENABLED=true` (or `sessionReplayEnabled: true` in `rum.local.json`) to enable and verify it; the renderer sends rrweb records through the native bridge and does not upload Replay directly.

Build and verify the standalone Windows x64 desktop application:

```powershell
npm run acceptance:win
```

Then run:

`release/GuanceWindowsElectronSample-win32-x64/GuanceWindowsElectronSample.exe`

The output is an unsigned, unpacked local acceptance application. Keep the adjacent DLLs and `resources` directory with the executable.
