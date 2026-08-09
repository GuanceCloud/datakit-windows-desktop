# Guance Windows Native Changelog

All notable changes to the `guance-windows-native` vcpkg port are recorded here.

## [Unreleased]

### Changed

- vcpkg releases now use an independent `vcpkg_<version>` tag stream.
- The release validator requires an exact changelog heading before port generation.

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
