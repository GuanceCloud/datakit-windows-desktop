# Guance Windows Native Changelog

All notable changes to the `guance-windows-native` vcpkg port are recorded here.

## [Unreleased]

### Changed

- vcpkg releases now use an independent `vcpkg_<version>` tag stream.
- The release validator requires an exact changelog heading before port generation.

## [0.1.0-alpha.4]

### Added

- An extended native Action API for actions that require explicit completion.

### Changed

- Action tracking now applies frequency protection, view-change completion, and a five-second maximum duration consistently with the managed SDK.
- Native crash and application-not-responding events now use the shared RUM error taxonomy and field layout.

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
