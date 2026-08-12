# Electron vcpkg Consumer Acceptance

These are two independent Electron RUM acceptance fixtures for the installed
vcpkg package boundary. They live under `tests` because they verify packaged
artifacts from this repository; they are not versioned application samples.
Choose one SDK ownership model and verify that project on its own. Do not
combine their Main Process or native lifecycle code.

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

The install scripts build a local overlay port from the current source tree and
label it with the development-only version `0.0.0`. The fixture manifests do
not declare a minimum published SDK version or consult the Guance registry.
This prevents a successful worktree build from being mistaken for verification
of a published release.

These fixtures do not create a tag, update the registry, publish a package, or
verify any published package version. A published-package smoke test must use
an exact registry version and baseline without setting
`GUANCE_WINDOWS_NATIVE_SOURCE_PATH`.

The `scripts/verify-*.cjs` files and Mixed Mode `scripts/start.ps1` are
acceptance helpers, not production application requirements.

Browser Session Replay remains experimental and is disabled by default. Both
projects expose the Browser RUM `records` capability and start Browser Replay
only when the Native SDK/Bridge configuration enables Replay. When disabled,
the adapter exposes no `records` capability and rejects any
`session_replay` envelope that bypasses that capability check.
