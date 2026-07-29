# Electron Sample

Phase 1 Electron hybrid RUM acceptance sample.

Configuration, run instructions, architecture, and the acceptance flow are documented in [`../../docs/electron-phase-1-acceptance.md`](../../docs/electron-phase-1-acceptance.md).

Quick start:

```powershell
npm install
npm run dev
```

Build and verify the standalone Windows x64 desktop application:

```powershell
npm run acceptance:win
```

Then run:

`release/GuanceRUMWindowsElectronSample-win32-x64/GuanceRUMWindowsElectronSample.exe`

The output is an unsigned, unpacked local acceptance application. Keep the adjacent DLLs and `resources` directory with the executable.
