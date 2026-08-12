# Electron Preview Samples

These are two independent minimal Electron RUM projects. Choose one SDK
ownership model and install, build, start, and verify that project on its own.
Do not combine their Main Process or native lifecycle code.

| Project | Data path | SDK owner | vcpkg feature |
| --- | --- | --- | --- |
| [`FullMode`](./FullMode/) | Renderer -> Preload -> Main -> Bridge EXE -> Native SDK | `guance_windows_electron_bridge.exe` | `electron-bridge` |
| [`MixedMode`](./MixedMode/) | Renderer -> Preload -> Main -> named pipe -> application native process | Existing application `guance_sdk_handle` | `electron-adapter` |

Both projects consume the installed SDK adapter under
`tools/guance-windows-native/electron`. They do not copy the private JSON,
line-protocol, handshake, retry, or named-pipe implementation.

The installed public Electron entries are:

```text
electron/main/index.cjs
electron/preload/standalone.cjs
electron/preload/install.cjs
```

Files under `electron/internal` belong to the SDK and are not application
integration points.

The sample install scripts build a local overlay port from the current source
tree while the next package version is under development. They do not create a
tag, update the registry, or publish a package. The `scripts/verify-*.cjs`
files and Mixed Mode `scripts/start.ps1` are sample verification helpers, not
production application requirements.
