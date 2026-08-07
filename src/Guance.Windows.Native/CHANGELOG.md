# Guance Windows Native Changelog

All notable changes to the `guance-windows-native` vcpkg port are recorded here.

## [Unreleased]

### Changed

- vcpkg releases now use an independent `vcpkg_<version>` tag stream.
- The release validator requires an exact changelog heading before port generation.

## [0.1.0-alpha.1]

### Added

- Initial experimental Windows x64 dynamic-library release.
- Stable C ABI entry points for RUM, distributed tracing, logging, native monitoring, and diagnostics.
- Installable `GuanceWindowsNative` CMake package with the `Guance::WindowsNative` target.
- Bounded file-backed queues without a default SQLite dependency.
- Opt-in experimental Session Replay, disabled by default at runtime.
