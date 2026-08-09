# Guance Windows SDK Samples

The samples exercise the public NuGet, native vcpkg, WebView2, and Electron integration paths without storing environment-specific tokens in source control.

## Local configuration

Copy `rum.local.json.example` to the ignored `rum.local.json` file in this directory. The managed and Electron samples read the same file. Environment variables and command-line arguments override matching JSON values.

For local DataKit intake, set `datakitUrl`. For public DataWay intake, set `datawayUrl` and `clientToken`. Never commit the populated local file.

Trace injection is enabled only when `traceEnabled` is true and `allowedTracingUrls` contains at least one absolute HTTP or HTTPS URL. An empty allow-list disables automatic injection.

## Managed desktop samples

Build all managed samples:

```powershell
dotnet build ../Guance.Windows.sln
```

Run an individual application:

```powershell
dotnet run --project WpfSample/WpfSample.csproj
dotnet run --project WinFormsSample/WinFormsSample.csproj
dotnet run --project WinUI3Sample/WinUI3Sample.csproj
```

WPF demonstrates WebView2 and image privacy in addition to common RUM operations. WinForms and WinUI 3 provide the same View, Action, Resource, Error, Long Task, Replay, diagnostics, and flush controls. All three submit a startup log and use the shared configuration loader.

Common command-line overrides include `--datakit-url`, `--dataway-url`, `--client-token`, `--rum-app-id`, `--service-name`, `--env`, `--version`, `--session-sample-rate`, `--logging-enabled`, `--log-sample-rate`, `--trace-enabled`, `--trace-sample-rate`, `--trace-type`, `--allowed-tracing-urls`, and `--webview-url`.

## Native C sample

`NativeConsoleSample` consumes the installed `GuanceWindowsNative` CMake package. It demonstrates initialization, custom logs, user and global context, View, Action, Resource, Long Task, diagnostics, flush, and shutdown.

Configure CMake with the same vcpkg toolchain and registry used by the host application:

```powershell
cmake -S NativeConsoleSample -B NativeConsoleSample/build `
  -DCMAKE_TOOLCHAIN_FILE=<vcpkg-root>/scripts/buildsystems/vcpkg.cmake
cmake --build NativeConsoleSample/build --config Release
```

The executable reads the same `GUANCE_RUM_*` intake and application environment variables as the managed samples.

## Electron sample

Electron-specific setup, security scope, tests, packaging, and acceptance commands are documented in `ElectronSample/README.md`.

