# Electron Full Mode Preview

This project independently verifies:

```text
Renderer -> standalone preload -> Main Process -> Bridge EXE -> Native SDK
```

The installed Bridge EXE initializes and shuts down the SDK. The installed
Main Process adapter starts that EXE, waits for its ready handshake, validates
trusted Renderer IPC, converts Browser RUM JSON, and stops the EXE cleanly.

## Install and start

Requirements: Windows x64, Node.js 22.12 or later, vcpkg, and the Visual Studio
C++ tools.

```powershell
npm install
npm run native:install

$env:GUANCE_RUM_APP_ID = "<rum-application-id>"
npm start
```

The sample uses the installed self-contained
`electron/preload/standalone.cjs` with `contextIsolation: true`,
`nodeIntegration: false`, and `sandbox: true`.

## Native settings

`main.cjs` passes these application-owned settings to `startFullMode()`:

| Input | Required | Sample default |
| --- | --- | --- |
| `GUANCE_RUM_APP_ID` | Yes | None |
| `GUANCE_RUM_DATAKIT_URL` | No | `http://127.0.0.1:9529` |
| `GUANCE_RUM_SERVICE` | No | `electron-full-preview` |
| `GUANCE_RUM_ENV` | No | `local` |

The sample also uses `app.getVersion()`, `userData/rum-cache`, full RUM
sampling, disabled Browser Log and Replay, and debug output. A production
application must replace those sample policies with its own settings.

Renderer configuration stays minimal:

```js
rum.init({
  datakitOrigin: "http://127.0.0.1",
});
```

Do not add `applicationId` in Renderer code. Full Mode uses the identity passed
to the native Bridge EXE.

## Verification

```powershell
npm run check
npm run verify
```

`verify:native` exercises the installed public Main Process adapter and Bridge
EXE. `verify:electron` adds the standalone preload and a sandboxed Renderer.
This project never starts a named-pipe server owned by the application.
