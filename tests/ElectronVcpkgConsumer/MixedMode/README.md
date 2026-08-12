# Electron Mixed Mode Consumer Acceptance

This project independently verifies:

```text
Renderer -> bundled existing preload -> Main Process -> named pipe
         -> application native process -> existing guance_sdk_handle
```

`native-host/main.cpp` initializes one SDK Handle, passes that same Handle to
`guance_electron_bridge_server_start()`, stops the Bridge Server, and only then
calls `guance_sdk_shutdown()`. No Guance production EXE is installed or
started.

## Install, build, and start

Requirements: Windows x64, Node.js 22.12 or later, vcpkg, CMake, and the Visual
Studio C++ tools.

```powershell
npm install
npm run native:build

$env:GUANCE_RUM_APP_ID = "<rum-application-id>"
npm start
```

`native:build` installs the base native library plus `electron-adapter`, then
builds the fixture C++ application. `preload:build` uses esbuild to bundle the
installed public `electron/preload/install.cjs` API into this application's
existing `preload-source.cjs`. The generated `.build/preload.cjs` runs with
`contextIsolation: true`, `nodeIntegration: false`, and `sandbox: true`.

Runtime `require()` of arbitrary local preload files is not used under the
sandbox. A production application should connect its own bundler to the same
public install entry instead of replacing its existing preload.

## Application settings

The fixture launcher maps these values to the application native process:

| Input | Required | Fixture default |
| --- | --- | --- |
| `GUANCE_RUM_APP_ID` | Yes | None |
| `GUANCE_RUM_DATAKIT_URL` | No | `http://127.0.0.1:9529` |
| `GUANCE_RUM_SERVICE` | No | `electron-mixed-preview` |
| `GUANCE_RUM_ENV` | No | `local` |
| `GUANCE_RUM_SESSION_REPLAY_ENABLED` | No | Disabled |
| `GUANCE_RUM_REPLAY_PRIVACY_LEVEL` | No | `mask` |

The native process also owns the application version, cache location, sampling,
debug policy, and the safe per-instance pipe name. Electron receives only the
matching pipe name and never initializes or shuts down the SDK Handle.

Browser Session Replay remains experimental and opt-in. The native process
copies its Replay setting and privacy level into the in-process Bridge Server
options. The installed Electron adapter advertises `records` and the Renderer
starts Browser Replay only when the Bridge Server handshake enables it. When
disabled, the adapter hides the capability and rejects `session_replay` input.

Renderer configuration stays minimal and must not contain `applicationId`:

```js
rum.init({
  datakitOrigin: "http://127.0.0.1",
});
```

## Verification

```powershell
npm run check
npm run verify
```

Both verification paths opt into Replay and assert that Browser RUM and Replay
use the Bridge Server attached to the same SDK Handle created by
`native-host/main.cpp`. `scripts/start.ps1` only coordinates the two fixture
processes; a production application should use its own lifecycle.
