# Guance RUM Windows SDK

Windows desktop RUM SDK for reporting user behavior data to Dataway or local DataKit. The first version targets Windows 10+, ships `net6.0`/`net8.0` and Windows desktop target assets, uses .NET 8 for samples and release smoke tests, and provides a native C ABI core for C/C++ applications.

## Capabilities

- RUM line protocol aligned with the Android SDK measurements: `view`, `action`, `resource`, `error`, and `long_task`.
- Public Dataway mode with `DatawayUrl + ClientToken`, and local DataKit mode with `DatakitUrl`.
- Persistent SQLite queue with retry, batching, and oldest-first discard limits.
- Manual APIs for View, Action, Error, Resource, LongTask, user binding, global context, flush, and shutdown.
- .NET automatic instrumentation entry points for WPF, WinForms, WinUI, `HttpClient`, unhandled exceptions, and UI-thread long tasks.
- Electron hybrid UI acceptance sample with a Vite/file renderer, secure preload bridge, loopback resource tests, an independently instrumented remote renderer window, and the official Guance Browser RUM SDK.
- Native C ABI for C/C++ safe automatic boundaries and manual behavior reporting.
- Experimental Session Replay implementation for .NET and native apps. Replay validation and release support are deferred to Phase 2 and are disabled by default in every Phase 1 sample.
- Native fallback persistence uses an on-disk FIFO file queue when SQLite is not linked, so the packaged DLL keeps failed events across process restarts.

Automatic WPF, WinForms, and WinUI instrumentation captures window view lifecycle, resize replay events, button/menu clicks, text input focus and changes, selector changes, toggle changes, keyboard shortcuts where available, WPF commands, WinForms grid cell interactions, and dynamically added controls discovered during idle scans or WinUI loaded-tree scans. `HttpClient` diagnostics classify resources as `http` or `grpc` and add network instrumentation metadata when available.
HTTP header and URL query capture is configurable through `RumConfig.Privacy`; credential-like headers and query parameters are redacted by default. Resource events also carry trace/span IDs, HTTP protocol/version metadata, and timing semantics that mark whether `resource_ttfb` came from a total-elapsed fallback or a caller-provided phase measurement. Apps that already have deeper network timing can set `RumConfig.HttpResourceTimingProvider`; automatic `HttpClient` collection will use that provider for DNS/TCP/TLS/TTFB phase fields and fall back safely if the provider returns `null` or throws. UI-thread long task collection coalesces repeated block reports during a configurable cooldown.

The Electron renderer uses the Web RUM contract and is accepted into the same Guance RUM application as the Windows samples when it uses the same application ID. It is identified as Windows through the browser OS dimensions and the `windows_desktop_platform=windows`, `windows_desktop_runtime=electron`, and `windows_integration_mode=hybrid` context tags; it does not impersonate the managed `df_windows_rum_sdk` identity.

## Quick Start

```csharp
using Guance.Rum.Windows;

RumSdk.Init(new RumConfig
{
    DatawayUrl = "https://openway.guance.com",
    ClientToken = "<client-token>",
    RumAppId = "<rum-app-id>",
    ServiceName = "desktop-client",
    Env = "prod",
    // Phase 1 validates RUM only. Session Replay is a Phase 2 target.
    SessionReplay = new RumSessionReplayConfig { Enabled = false },
    Privacy = new RumPrivacyConfig
    {
        CaptureHttpHeaders = true,
        CaptureUrlQueryString = true,
        RedactedHeaderNames = new[] { "Authorization", "Cookie", "X-Api-Key" },
        RedactedQueryParameterNames = new[] { "token", "access_token", "password" }
    },
    HttpResourceTimingProvider = (request, response, elapsed, exception) =>
    {
        // Optional: return caller-measured phase timings when your HTTP stack has them.
        return null;
    },
    Debug = true
});

RumSdk.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
{
    EnableWpf = true,
    EnableWinForms = true,
    EnableWinUI = true,
    EnableHttpClient = true,
    EnableUnhandledException = true,
    EnableUiThreadBlock = true,
    UiThreadLongTaskCooldown = TimeSpan.FromSeconds(5)
});

// WinUI 3 windows should be attached when they are created because WinUI does
// not expose a stable process-wide window enumeration API.
// mainWindow = new MainWindow().UseGuanceRum("MainWindow");
// RumSdk.AttachWinUIWindow(mainWindow, "MainWindow");
```

Use `FTSDKConfig.builder(datakitUrl)` style local deployment by setting only `DatakitUrl`:

```csharp
RumSdk.Init(new RumConfig
{
    DatakitUrl = "http://127.0.0.1:9529",
    RumAppId = "<rum-app-id>",
    ServiceName = "desktop-client"
});
```

Manual APIs mirror the Android SDK behavior:

```csharp
RumSdk.StartView("MainWindow");
using (RumSdk.StartAction("Save", "click"))
{
    // user action work
}

var resourceId = RumSdk.StartResource("https://api.example.com/v1/items", "GET");
RumSdk.StopResource(
    resourceId,
    statusCode: 200,
    timing: RumResourceTiming.FromPhases(
        dns: TimeSpan.FromMilliseconds(1),
        tcp: TimeSpan.FromMilliseconds(2),
        ssl: TimeSpan.FromMilliseconds(3),
        ttfb: TimeSpan.FromMilliseconds(40),
        totalDuration: TimeSpan.FromMilliseconds(120)),
    responseSize: 1024);

try
{
    throw new InvalidOperationException("demo");
}
catch (Exception ex)
{
    RumSdk.AddError(ex);
}
```

Session Replay is experimental and deferred from Phase 1. Phase 2 work can opt in and override privacy per UI element:

```csharp
RumSdk.SetSessionReplayTextAndInputPrivacy(passwordBox, SessionReplayTextAndInputPrivacy.MaskAll);
RumSdk.SetSessionReplayTouchPrivacy(secretPanel, SessionReplayTouchPrivacy.Hide);
RumSdk.SetSessionReplayImagePrivacy(imagePanel, SessionReplayImagePrivacy.MaskAll);
RumSdk.SetSessionReplayHidden(customerSsnPanel);

RumSdk.StartSessionReplayRecording();
RumSdk.StopSessionReplayRecording();
```

Runtime diagnostics can be read without enabling debug logging:

```csharp
var diagnostics = RumSdk.GetDiagnosticsSnapshot();
Console.WriteLine($"queued={diagnostics.RumEventsEnqueued}, retries={diagnostics.RumUploadRetryCount}");

RumSdk.AddDiagnosticListener((_, item) =>
{
    Console.WriteLine($"{item.Level} {item.Source}: {item.Message}");
});
```

## Electron Hybrid Sample

The Electron acceptance UI installs `@cloudcare/browser-rum` in the renderer and exercises all five Phase 1 RUM signals:

```powershell
cd samples/ElectronSample
npm install
npm run dev
```

Use `npm start` for production-style `file://` loading, `npm run smoke` for the bounded Electron startup smoke, or `npm run acceptance:win` to build and verify a standalone Windows x64 desktop application under `samples/ElectronSample/release`. Configuration uses the same `GUANCE_RUM_*` environment variables as the managed samples. See `docs/electron-phase-1-acceptance.md` for the Dataway, DataKit, packaged executable, and remote-renderer flows.

## Build

```bash
dotnet restore Guance.Rum.Windows.sln
dotnet test tests/Guance.Rum.Windows.Tests/Guance.Rum.Windows.Tests.csproj
powershell -ExecutionPolicy Bypass -File build\pack.ps1 -OutputDirectory dist
```

Native core:

```bash
cmake -S src/Guance.Rum.NativeCore -B build/native
cmake --build build/native --config Release
```

On Windows machines without CMake/MSVC, the repository also includes a Zig-based validation path:

```powershell
powershell -ExecutionPolicy Bypass -File build\native-windows-zig.ps1 -Zig C:\path\to\zig.exe
```

The NuGet package includes native runtime assets when the matching DLL exists before `dotnet pack`:

| RID | Package path | Status |
| --- | --- | --- |
| `win-x64` | `runtimes/win-x64/native/guance_rum_native.dll` | CI and remote smoke verified |
| `win-arm64` | `runtimes/win-arm64/native/guance_rum_native.dll` | Cross-build and package verification with `-TargetArch arm64 -SkipSmoke` |
| `win-x86` | `runtimes/win-x86/native/guance_rum_native.dll` | Cross-build and package verification with `-TargetArch x86 -SkipSmoke` |

Use `build\pack.ps1` for release validation; it restores, tests, runs the Electron `npm ci`/test/build/bounded runtime smoke, packs, verifies all native RID assets, runs a clean NuGet consumer smoke, and writes `release-manifest.json` with artifact hashes, target frameworks, native asset inventory, and Electron validation status. The consumer smoke runs the console app, builds WPF and WinForms consumers, and publishes a minimal programmatic WinUI 3 consumer against the generated NuGet package. WinUI runtime launch is attempted as a best-effort check because SSH/CI sessions are often non-interactive; use `build\pack.ps1 -RequireWinUIRuntimeSmoke` on an interactive Windows desktop to make the WinUI run marker a hard gate. `build\consumer-smoke.ps1` can be run separately against an existing package. `-SkipConsumerSmoke`, `-SkipWinUISmoke`, and `-SkipElectronRuntimeSmoke` are available for constrained environments, and every skipped runtime check is recorded in the manifest.

## Dataway Contract

The managed SDK posts `text/plain` line protocol to `v1/write/rum`. Public Dataway requests append `token=<clientToken>&to_headless=true`; local DataKit requests do not require a token. 2xx through 4xx responses are treated as terminal for queued data, matching the Android SDK retry boundary; 5xx and network failures remain queued for retry with exponential backoff and jitter. HTTP header capture redacts credentials such as `Authorization`, `Cookie`, and API-token headers by default. Native WinHTTP upload URL-encodes Dataway tokens, supports request timeout and named proxy configuration, and exposes last status/error/latency through diagnostics.

The experimental Session Replay transport posts `multipart/form-data` to `v1/write/rum/replay`. That transport, its Android-compatible envelope, player routing, privacy, and performance gates are Phase 2 work and are not part of the Phase 1 release claim.
