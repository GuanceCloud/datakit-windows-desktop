# Guance.Windows Changelog

All notable changes to the managed `Guance.Windows` NuGet package are recorded here.

## [Unreleased]

### Changed

- NuGet releases now use an independent `nuget_<version>` tag stream.
- The release validator requires an exact changelog heading before packaging.

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
