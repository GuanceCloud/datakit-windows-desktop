# Changelog

## 0.1.0

- Initial Windows desktop RUM SDK with .NET wrapper and native C ABI.
- Supports RUM line protocol measurements: `view`, `action`, `resource`, `error`, and `long_task`.
- Adds WPF, WinForms, WinUI 3, `HttpClient`, unhandled exception, UI-thread block, and Session Replay collection.
- Packages native runtime assets for `win-x64`, `win-arm64`, and `win-x86` when corresponding DLLs are built before packing.
- Hardens native WinHTTP upload diagnostics/configuration and extends the native C ABI with scoped actions and resource metadata.
- Adds Session Replay tree safety limits, UI long-task coalescing, and richer resource timing semantics.
- Adds clean NuGet consumer smoke validation, crash-process smoke coverage, release manifest generation, performance budget tests, WinUI `UseGuanceRum` ergonomics, and manual resource timing phase helpers.
- Adds WinUI 3 package consumer runtime smoke, .NET 8 sample defaults with `net6.0` package compatibility retained, HTTP resource timing provider hooks, custom-rendered Session Replay placeholders, transport/queue fault-injection tests, and UTF-8 no-BOM release manifests.
