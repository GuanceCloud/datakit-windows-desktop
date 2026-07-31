# Electron Sample

Phase 1 Electron hybrid RUM acceptance sample.

Configuration, run instructions, architecture, and the acceptance flow are documented in [`../../docs/electron-phase-1-acceptance.md`](../../docs/electron-phase-1-acceptance.md).

The sample pins Electron `22.3.27` and emits a Windows x64 package intended to cover Windows 7 SP1 through Windows 11. Electron 22 is end-of-life, so this compatibility build receives no Chromium, Node.js, or Electron security updates; the managed Windows SDK remains a Windows 10+ target.

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

The Electron and managed samples share `../rum.local.json`. Copy `../rum.local.json.example`, then edit the ignored local file. Environment variables override matching JSON values. A packaged application reads `rum.local.json` beside the executable.

Build and verify the standalone Windows x64 desktop application:

```powershell
npm run acceptance:win
```

Then run:

`release/GuanceRUMWindowsElectronSample-win32-x64/GuanceRUMWindowsElectronSample.exe`

The output is an unsigned, unpacked local acceptance application. Keep the adjacent DLLs and `resources` directory with the executable.
