# Guance.Windows.Native Changelog

All notable changes to `Guance.Windows.Native`, distributed through the
`guance-windows-native` vcpkg port, are recorded here.

## [0.1.0-alpha.7]

### Fixed

- Native RUM and Log uploads now emit zlib-wrapped Deflate bodies compatible with `Content-Encoding: deflate` receivers.

## [0.1.0-alpha.6]

### Added

- Automatic Deflate compression for Native RUM and Log intake uploads, with a configurable partial-batch flush interval.
- Persistent anonymous user identifiers scoped by RUM application ID and shared across Native and Electron telemetry.
- Automatic application launch Actions for Native and Electron integrations.

### Fixed

- Session Replay no longer uploads segments without an associated View ID.

## [0.1.0-alpha.5]

### Added

- An opt-in `electron-adapter` vcpkg feature that installs the SDK-owned Electron Main Process and Preload entry points for Mixed Mode applications.
- An in-process Electron Bridge Server C API for forwarding Browser telemetry through an application-owned SDK handle.
- Browser Log and Browser Session Replay forwarding through the Electron adapter for both Full Mode and Mixed Mode.

### Changed

- The `electron-bridge` vcpkg feature now includes the Electron adapter alongside the Full Mode Bridge executable.
- Electron Session Replay now follows the authoritative Native Bridge enablement and privacy capabilities, and rejects Replay input when Native Replay is disabled.

## [0.1.0-alpha.4]

### Added

- An extended native Action API for actions that require explicit completion.
- `guance_sdk_get_version()` for reading the release version compiled into the native runtime.
- `guance_sdk_write_electron_bridge_line(...)` for routing validated Browser RUM, launch, error, log, and Replay bridge messages into an application-owned SDK handle.
- `guance_rum_add_launch_action_ext(...)` for associating a measured launch Action with an existing Browser View.

### Changed

- Action tracking now applies frequency protection, view-change completion, and a five-second maximum duration consistently with the managed SDK.
- Native crash and application-not-responding events now use the shared RUM error taxonomy and field layout.
- Native RUM, Log, and Session Replay payloads now report the compiled SDK release through `sdk_version`.
- Electron bridge ingestion now keeps Native Core authoritative for Session identity instead of accepting a Browser-provided Session override.
- Electron cold-launch Actions now associate with the first main Browser View when it becomes available, with a bounded fallback when no View arrives.

## [0.1.0-alpha.3]

### Added

- Native RUM and log data-modifier callbacks for changing existing tags and fields before caching.
- Optional HTTP request and response header collection with configurable sensitive-header redaction.
- An opt-in `electron-bridge` vcpkg feature that installs `guance_windows_electron_bridge.exe` and its native runtime dependency.

### Changed

- URL and HTTP-header privacy rules now run consistently after application data modifiers.
- Native Resource completion can attach request and response headers for SDK-side privacy filtering.

## [0.1.0-alpha.2]

### Added

- Native crash and UI hang monitoring with next-start recovery reporting.
- Automatic WinHTTP trace propagation and resource collection support.
- Native console integration sample and expanded public C/C++ SDK headers.

### Changed

- Hardened native package, CMake, release-tag, and vcpkg registry validation.
- Expanded Electron native bridge and experimental Session Replay diagnostics.

## [0.1.0-alpha.1]

### Added

- Initial experimental Windows x64 dynamic-library release.
- Stable C ABI entry points for RUM, distributed tracing, logging, native monitoring, and diagnostics.
- Installable `GuanceWindowsNative` CMake package with the `Guance::WindowsNative` target.
- Bounded file-backed queues without a default SQLite dependency.
- Opt-in experimental Session Replay, disabled by default at runtime.
