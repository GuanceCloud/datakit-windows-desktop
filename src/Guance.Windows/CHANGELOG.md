# Guance.Windows Changelog

All notable changes to the managed `Guance.Windows` NuGet package are recorded here.

## [Unreleased]

### Changed

- NuGet releases now use an independent `nuget_<version>` tag stream.
- The release validator requires an exact changelog heading before packaging.

## [0.1.0-alpha.1]

### Added

- Initial experimental release of the Guance Windows observability SDK.
- Managed APIs for RUM, distributed tracing, logging, upload scheduling, and bounded file caching.
- Native runtime assets for supported Windows architectures.
- WPF, WinForms, WinUI 3, WebView2, and Electron integration support.
- Opt-in experimental Session Replay, disabled by default.
