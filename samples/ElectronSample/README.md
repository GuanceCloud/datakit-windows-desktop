# Electron Sample

Electron native-bridge RUM, Log, Trace, and experimental Session Replay sample.

The adapter separates initialization ownership from page collection policy:
`initializeElectronMonitoring()` lets Electron Main create and own the Native
Host for a full Electron application, while `connectElectronMonitoring()`
connects selected Electron pages to an SDK instance already initialized and
owned by a C++ application through a named-pipe Adapter. Both modes use the
same `defaultPage` plus `pages` overrides, preload arguments, and trusted
`webContents` registry.

Configuration, collector-only Browser initialization, native ownership boundaries, Replay opt-in, run instructions, and verification are documented in the [official Electron integration guide](https://docs.guance.com/real-user-monitoring/windows/advanced-electron/).

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

To run the documented C++-owned mode, build the Native targets and start the
C++ host in a separate terminal. The host calls `guance_sdk_init()` before it
opens the named pipe and keeps ownership of that SDK Handle:

```powershell
npm run native:build
$env:GUANCE_RUM_NATIVE_DATAKIT_URL = "http://127.0.0.1:9529"
$env:GUANCE_RUM_NATIVE_APP_ID = "<rum-application-id>"
$env:GUANCE_RUM_NATIVE_SERVICE = "native-desktop-client"
$env:GUANCE_RUM_NATIVE_ENV = "prod"
$env:GUANCE_RUM_NATIVE_VERSION = "1.0.0"
$env:GUANCE_RUM_NATIVE_CACHE_PATH = "$env:LOCALAPPDATA\Guance\native-owned-cache"
$env:GUANCE_RUM_NATIVE_SAMPLE_RATE = "1"
$env:GUANCE_RUM_NATIVE_LOG_ENABLED = "1"
$env:GUANCE_RUM_NATIVE_TRACE_ENABLED = "1"
$env:GUANCE_RUM_NATIVE_TRACE_SAMPLE_RATE = "1"
$env:GUANCE_RUM_NATIVE_TRACE_TYPE = "w3c_traceparent"
$env:GUANCE_RUM_NATIVE_OWNED_EXIT_ON_DISCONNECT = "1"
& "..\..\src\Guance.Windows.Native\bin\win-x64\guance_windows_electron_native_owned_host.exe"
```

Then connect Electron from a second terminal:

```powershell
npm run start:native-owned
```

The capabilities handshake contains only RUM, Log, Replay, Trace, privacy, and
debug switches. It does not expose the intake URL, Client Token, application ID,
cache path, or upload settings to Electron. Closing Electron disconnects the
pipe; `connectElectronMonitoring()` does not shut down the C++ SDK Handle. The
sample-only `GUANCE_RUM_NATIVE_OWNED_EXIT_ON_DISCONNECT=1` setting lets the C++
host decide to shut itself down after the client disconnects.

The Electron and managed samples share `../rum.local.json`. Copy `../rum.local.json.example`, then edit the ignored local file. This flat file is a Sample-only compatibility input; Electron normalizes it in the main process to common `transport`/`application`/`runtime` settings plus parallel `rum`, `log`, and `trace` capability objects. Environment variables override matching JSON values. Do not ship a token-bearing local file or expose it to renderer assets in production.

Keep environment-specific endpoints outside the repository. Use `GUANCE_RUM_DATAKIT_URL` for the intake endpoint and `GUANCE_RUM_WEBVIEW_URL` or `GUANCE_RUM_ELECTRON_REMOTE_URL` for the remote renderer URL. RUM is controlled independently by `GUANCE_RUM_ENABLED` and `GUANCE_RUM_SAMPLE_RATE`; Browser Log by `GUANCE_LOG_ENABLED` and `GUANCE_LOG_SAMPLE_RATE`; Browser Trace header injection by `GUANCE_TRACE_ENABLED`, `GUANCE_TRACE_SAMPLE_RATE`, `GUANCE_TRACE_TYPE`, and the comma-separated `GUANCE_TRACE_ALLOWED_URLS` allow-list. The former `GUANCE_RUM_ALLOWED_TRACING_URLS` name remains compatible. See the [official Electron integration guide](https://docs.guance.com/real-user-monitoring/windows/advanced-electron/) for the normalized production shape.

Disk and network budgets are also configurable in `rum.local.json`: `maxCacheBytes`, `maxCacheFiles`, `maxCacheAgeSeconds`, `maxBatchItems`, `maxBatchBytes`, `maxUploadBytesPerSecond`, `uploadBurstBytes`, `maxUploadRequestsPerSecond`, and `maxUploadBatchesPerCycle`. Matching `GUANCE_RUM_*` environment variables override the file values.

Session Replay is experimental and disabled by default. Set `GUANCE_RUM_SESSION_REPLAY_ENABLED=true` (or `sessionReplayEnabled: true` in `rum.local.json`) to enable and verify it; the renderer sends rrweb records through the native bridge and does not upload Replay directly.

Build and verify the standalone Windows x64 desktop application:

```powershell
npm run acceptance:win
```

Then run:

`release/GuanceWindowsElectronSample-win32-x64/GuanceWindowsElectronSample.exe`

The output is an unsigned, unpacked local acceptance application. Keep the adjacent DLLs and `resources` directory with the executable.
