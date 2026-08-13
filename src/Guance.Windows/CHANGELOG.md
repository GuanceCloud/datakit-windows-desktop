# Guance.Windows Changelog

All notable changes to the managed `Guance.Windows` NuGet package are recorded here.

## [0.1.0-alpha.6]

### Added

- Persistent anonymous user identifiers scoped by RUM application ID for unsigned RUM events and RUM-linked Logs, preserving identity across process restarts.
- The bundled Native runtime now records application launch Actions automatically and compresses RUM and Log intake uploads with Deflate.

### Fixed

- The bundled Native Session Replay runtime no longer uploads segments without an associated View ID.
- Native release validation now isolates Action lifecycle event counts from automatic application launch collection.

## [0.1.0-alpha.5]

This version was tagged but not published because its Native x64 release validation failed.

### Added

- Persistent anonymous user identifiers scoped by RUM application ID for unsigned RUM events and RUM-linked Logs, preserving identity across process restarts.
- The bundled Native runtime now records application launch Actions automatically and compresses RUM and Log intake uploads with Deflate.

### Fixed

- The bundled Native Session Replay runtime no longer uploads segments without an associated View ID.

## [0.1.0-alpha.4]

### Changed

- No SDK runtime behavior changes. NuGet release validation now excludes environment-dependent desktop UI runtime smoke tests from package gating while keeping them in branch and pull-request CI.

## [0.1.0-alpha.3]

### Added

- RUM, log, and resource data-modifier callbacks for changing existing tags and fields before caching.
- Optional HTTP request and response header collection with configurable sensitive-header redaction.
- SDK diagnostic reporting for local configuration and runtime troubleshooting.

### Changed

- Action tracking now applies frequency protection, explicit `needWait` completion, view-change completion, and a five-second maximum duration consistently across managed and native APIs.
- Windows crash, application-not-responding, and network failures now use the shared RUM error taxonomy and field layout.
- Privacy rules now run consistently after application data modifiers.

## [0.1.0-alpha.2]

### Added

- Automatic trace propagation, application launch tracking, and Logging intake support.
- Shared desktop sample configuration and expanded public API integration examples.
- Experimental Session Replay coverage across managed desktop and Electron samples.

### Changed

- Hardened solution-wide package, native runtime asset, and consumer validation.
- Expanded bounded cache, diagnostics, privacy, and upload configuration APIs.

## [0.1.0-alpha.1]

### Added

- Initial experimental release of the Guance Windows observability SDK.
- Managed APIs for RUM, distributed tracing, logging, upload scheduling, and bounded file caching.
- Native runtime assets for supported Windows architectures.
- WPF, WinForms, WinUI 3, WebView2, and Electron integration support.
- Opt-in experimental Session Replay, disabled by default.
