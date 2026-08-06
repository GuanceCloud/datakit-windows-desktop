# Guance Windows SDK

Windows desktop observability SDK for RUM, trace propagation, logging, and experimental Session Replay through Dataway or local DataKit. The first version targets Windows 10+, ships `net6.0`/`net8.0` and Windows desktop target assets, uses .NET 8 for samples and release smoke tests, and provides a native C ABI core for C/C++ applications.

## Capabilities

- RUM line protocol aligned with the Android SDK measurements: `view`, `action`, `resource`, `error`, and `long_task`.
- Public Dataway mode with `DatawayUrl + ClientToken`, and local DataKit mode with `DatakitUrl`.
- Versioned, checksummed batch-file cache with a shared hard disk cap, crash recovery, age/file limits, and stream-aware eviction.
- Manual APIs for View, Action, Error, Resource, LongTask, user binding, global context, flush, and shutdown.
- Android-aligned custom Logging input with RUM correlation, level filtering, sampling, batching, a dedicated durable queue, and `System.Diagnostics.Trace` auto-capture.
- .NET automatic instrumentation entry points for WPF, WinForms, WinUI, `HttpClient`, unhandled exceptions, and UI-thread long tasks.
- Electron hybrid sample with a Vite/file renderer, the official Browser RUM collector, an Android-WebView-compatible `FTWebViewJavascriptBridge`, native session/context ownership, native persistence/upload, loopback resource tests, and an isolated remote renderer window.
- Native C ABI for C/C++ safe automatic boundaries, WinHTTP trace propagation, and manual behavior reporting.
- Opt-in native Win32 UI hang detection plus next-start recovery reporting for unhandled SEH and `std::terminate`; minimal crash dumps are local-only and disabled by default.
- Experimental Session Replay implementation for .NET, native, WebView2, and Electron apps. It is disabled by default but can be enabled and verified in every Sample; it is not a stable compatibility claim.
- Managed and native runtimes use the same `active/ready/sending` batch-file lifecycle and retain retryable uploads across process restarts.

Automatic WPF, WinForms, and WinUI instrumentation captures window view lifecycle, resize replay events, button/menu clicks, text input focus and changes, selector changes, toggle changes, keyboard shortcuts where available, WPF commands, WinForms grid cell interactions, and dynamically added controls discovered during idle scans or WinUI loaded-tree scans. `HttpClient` diagnostics classify resources as `http` or `grpc` and add network instrumentation metadata when available.
Automatically collected interaction Actions use `action_type=click` for pointer/control activation and `action_type=key` for keyboard activation. Application startup Actions retain `launch_cold` and `launch_hot`; semantic values such as `menu`, `input`, `selection`, `toggle`, `value_change`, `shortcut`, `command`, `grid_cell_click`, and `submit` are not emitted as automatic Action types.
HTTP header and URL query capture is configurable through `GuanceConfig.Privacy`; credential-like headers and query parameters are redacted by default. When trace linking is enabled, Resource events carry the same trace/span IDs injected into the outgoing request, plus HTTP protocol/version metadata and timing semantics that mark whether `resource_ttfb` came from a total-elapsed fallback or a caller-provided phase measurement. Apps that already have deeper network timing can set `GuanceConfig.HttpResourceTimingProvider`; automatic `HttpClient` collection will use that provider for DNS/TCP/TLS/TTFB phase fields and fall back safely if the provider returns `null` or throws. UI-thread long task collection coalesces repeated block reports during a configurable cooldown.

The Electron renderer uses Browser RUM and Browser Logs only as page collectors/serializers. A preload bridge sends serialized RUM, Log, and experimental Replay records to the Electron main process and then to the Windows native core. Electron configuration keeps `rum`, `log`, and `trace` as parallel capabilities beneath shared transport/application/runtime settings. The native side owns the real application ID, session, sampling, trusted context, `sdk_name=df_windows_rum_sdk`, durable queue, and upload; renderer bootstrap never receives the Dataway token or real intake configuration.

## Quick Start

```csharp
using Guance.Windows;

GuanceSdk.Init(new GuanceConfig
{
    DatawayUrl = "https://openway.guance.com",
    ClientToken = "<client-token>",
    RumAppId = "<rum-app-id>",
    ServiceName = "desktop-client",
    Env = "prod",
    Trace = new TraceConfig
    {
        EnableAutoTrace = true,
        EnableLinkRumData = true,
        TraceType = TraceType.TraceParent,
        // Use an explicit allow-list before sending tracing headers.
        ShouldTrace = uri => uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
    },
    Logging = new LogConfig
    {
        EnableCustomLog = true,
        EnableLinkRumData = true,
        EnableTraceCapture = true,
        SampleRate = 1.0,
        LevelFilters = new[] { LogStatus.Info, LogStatus.Warning, LogStatus.Error },
        GlobalContext = new Dictionary<string, object?> { ["channel"] = "desktop" }
    },
    // Experimental and disabled by default. Set Enabled=true only after
    // reviewing privacy and sampling for your application.
    SessionReplay = new RumSessionReplayConfig
    {
        Enabled = false,
        SampleRate = 1.0,
        TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAll
    },
    Privacy = new RumPrivacyConfig
    {
        CaptureHttpHeaders = true,
        CaptureUrlQueryString = true,
        RedactedHeaderNames = new[] { "Authorization", "Cookie", "X-Api-Key" },
        RedactedQueryParameterNames = new[] { "token", "access_token", "password" }
    },
    Cache = new CacheOptions
    {
        MaxDiskBytes = 128L * 1024 * 1024,
        MaxFiles = 1024,
        MaxAge = TimeSpan.FromDays(7),
        MaxBatchItems = 50,
        MaxBatchBytes = 512L * 1024
    },
    Upload = new UploadOptions
    {
        MaxBytesPerSecond = 256L * 1024,
        BurstBytes = 2L * 1024 * 1024,
        MaxRequestsPerSecond = 2,
        MaxBatchesPerCycle = 4
    },
    HttpResourceTimingProvider = (request, response, elapsed, exception) =>
    {
        // Optional: return caller-measured phase timings when your HTTP stack has them.
        return null;
    },
    Debug = true
});

GuanceSdk.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
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
// GuanceSdk.AttachWinUIWindow(mainWindow, "MainWindow");
```

Use `FTSDKConfig.builder(datakitUrl)` style local deployment by setting only `DatakitUrl`:

```csharp
GuanceSdk.Init(new GuanceConfig
{
    DatakitUrl = "http://127.0.0.1:9529",
    RumAppId = "<rum-app-id>",
    ServiceName = "desktop-client"
});
```

Call `GuanceSdk.GetCacheDiagnosticsSnapshot()` to inspect allocation-aware cache bytes and file count at runtime. `FlushAsync` drains explicitly; shutdown sends only one bounded cycle and leaves remaining batches durable for the next process so application exit cannot silently bypass the configured bandwidth budget.

Trace propagation is opt-in and supports `DdTrace`, `ZipkinMultiHeader`,
`ZipkinSingleHeader`, `TraceParent`, `SkyWalking`, and `Jaeger`. The trace
sampling decision controls the propagated sampled flag; IDs are still generated
for unsampled requests. `EnableLinkRumData` controls only whether those IDs are
copied to the RUM Resource. Set `TraceConfig.ContextProvider` to supply custom
headers and IDs. Global `HttpClient` interception requires
`EnableAutomaticInstrumentation`; custom handler chains can instead wrap their
transport with `RumHttpMessageHandler`.

Manual APIs mirror the Android SDK behavior:

```csharp
GuanceSdk.StartView("MainWindow");
using (GuanceSdk.StartAction("Save", "click"))
{
    // user action work
}

var resourceId = GuanceSdk.StartResource("https://api.example.com/v1/items", "GET");
GuanceSdk.StopResource(
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
    GuanceSdk.AddError(ex);
}
```

Custom logs are sent to the Logging intake independently of RUM events:

```csharp
GuanceSdk.AddLog(
    "payment completed",
    LogStatus.Ok,
    new Dictionary<string, object?> { ["order_id"] = "A1001" });

GuanceSdk.AddLogs(new[]
{
    new LogEntry("cache miss", LogStatus.Debug),
    new LogEntry("security audit", "audit")
});
```

`EnableTraceCapture` installs a `GuanceTraceListener` for the process-wide
`System.Diagnostics.Trace` pipeline. It captures `Trace` and `TraceSource`
events; logging frameworks that do not write to that pipeline should call
`AddLog` from their own sink/provider. Log content is capped at 30 KiB without
splitting a UTF-8 character. The default dedicated queue holds 5,000 records and
discards new records when full; set `DiscardStrategy =
LogDiscardStrategy.DiscardOldest` to retain the newest records instead.

Session Replay is experimental and disabled by default. All Samples accept `sessionReplayEnabled: true` (or `GUANCE_RUM_SESSION_REPLAY_ENABLED=true`) so the feature can be enabled and verified, but this does not promote it to stable. Initialize with `Enabled = true` before using the following APIs; manual start does not override a disabled configuration:

```csharp
GuanceSdk.SetSessionReplayTextAndInputPrivacy(passwordBox, SessionReplayTextAndInputPrivacy.MaskAll);
GuanceSdk.SetSessionReplayTouchPrivacy(secretPanel, SessionReplayTouchPrivacy.Hide);
GuanceSdk.SetSessionReplayImagePrivacy(imagePanel, SessionReplayImagePrivacy.MaskAll);
GuanceSdk.SetSessionReplayHidden(customerSsnPanel);

GuanceSdk.StartSessionReplayRecording();
GuanceSdk.StopSessionReplayRecording();
```

Runtime diagnostics can be read without enabling debug logging:

```csharp
var diagnostics = GuanceSdk.GetDiagnosticsSnapshot();
Console.WriteLine($"queued={diagnostics.RumEventsEnqueued}, retries={diagnostics.RumUploadRetryCount}");

GuanceSdk.AddDiagnosticListener((_, item) =>
{
    Console.WriteLine($"{item.Level} {item.Source}: {item.Message}");
});
```

## Electron Hybrid Sample

The Electron acceptance UI pins Electron `22.3.27`, installs `@cloudcare/browser-rum` and `@cloudcare/browser-logs` in the renderer, and exercises the five RUM signals, native Log ingestion, Browser Trace header injection/resource correlation, plus opt-in experimental Replay. Its Windows x64 compatibility artifact targets Windows 7 SP1 through Windows 11; Electron 22 is end-of-life and receives no further security updates. The managed SDK remains a Windows 10+ target.

```powershell
cd samples/ElectronSample
npm install
npm run dev
```

Use `npm start` for production-style `file://` loading, `npm run smoke` for the bounded Electron startup smoke, or `npm run acceptance:win` to build and verify a standalone Windows x64 desktop application under `samples/ElectronSample/release`. Managed and Electron samples share the ignored `samples/rum.local.json`; `GUANCE_RUM_*` environment variables override JSON values. The `webViewUrl` JSON field configures the WPF WebView2 sample and runtime smoke tests. See `docs/electron-phase-1-acceptance.md` for the Dataway, DataKit, packaged executable, and remote-renderer flows.

## Build

```bash
dotnet restore Guance.Windows.sln
dotnet test tests/Guance.Windows.Tests/Guance.Windows.Tests.csproj
powershell -ExecutionPolicy Bypass -File build\pack.ps1 -OutputDirectory dist
```

Native core:

```bash
cmake -S src/Guance.Windows.Native -B build/native
cmake --build build/native --config Release
```

After the custom registry is configured, C++ consumers use the
`guance-windows-native` vcpkg port and the exported CMake target:

```bash
vcpkg install guance-windows-native
```

```cmake
find_package(GuanceWindowsNative CONFIG REQUIRED)
target_link_libraries(my_app PRIVATE Guance::WindowsNative)
```

Use `guance_sdk.h` as the umbrella C header. Signal-specific includes are
available as `guance_rum.h`, `guance_trace.h`, and `guance_log.h`; C++ helpers
are exposed through `guance_sdk.hpp`.

Native applications can opt into watchdog and crash recovery after creating the
SDK handle:

```cpp
#include "guance_sdk.hpp"

guance_sdk_native_monitoring_config monitoring;
guance_sdk_native_monitoring_config_init(&monitoring);
monitoring.enable_ui_hang_monitoring = 1;
monitoring.main_window_handle = reinterpret_cast<uintptr_t>(main_window);
monitoring.enable_native_crash_reporting = 1;
guance_sdk_enable_native_monitoring(rum, &monitoring);

guance_trace_config trace;
guance_trace_config_init(&trace);
trace.enable_auto_trace = 1;
trace.enable_link_rum_data = 1;
trace.trace_type = GUANCE_TRACE_TRACEPARENT;
guance_trace_configure(rum, &trace);

guance_log_config logging;
guance_log_config_init(&logging);
logging.enable_custom_log = 1;
logging.enable_link_rum_data = 1;
guance_log_configure(rum, &logging);
guance_log_add(rum, "native startup", "info", nullptr, 0);
```

The default thresholds are 500 ms for a UI long task and 5 seconds for an
application hang. Crash handlers only persist a bounded envelope (and,
optionally, a local minimal dump); the RUM Error is queued on the next launch.
See `docs/native-c-api.md` for lifecycle and C++ `std::terminate` details.

On Windows machines without CMake/MSVC, the repository also includes a Zig-based validation path:

```powershell
powershell -ExecutionPolicy Bypass -File build\native-windows-zig.ps1 -Zig C:\path\to\zig.exe
```

The NuGet package includes native runtime assets when the matching DLL exists before `dotnet pack`:

| RID | Package path | Status |
| --- | --- | --- |
| `win-x64` | `runtimes/win-x64/native/guance_windows_native.dll` | CI and remote smoke verified |
| `win-arm64` | `runtimes/win-arm64/native/guance_windows_native.dll` | Cross-build and package verification with `-TargetArch arm64 -SkipSmoke` |
| `win-x86` | `runtimes/win-x86/native/guance_windows_native.dll` | Cross-build and package verification with `-TargetArch x86 -SkipSmoke` |

Use `build\pack.ps1` for release validation; it restores, tests, runs the Electron `npm ci`/test/build/bounded runtime smoke, packs, verifies all native RID assets, runs a clean NuGet consumer smoke, and writes `release-manifest.json` with artifact hashes, target frameworks, native asset inventory, and Electron validation status. The consumer smoke runs the console app, builds WPF and WinForms consumers, and publishes a minimal programmatic WinUI 3 consumer against the generated NuGet package. WinUI runtime launch is attempted as a best-effort check because SSH/CI sessions are often non-interactive; use `build\pack.ps1 -RequireWinUIRuntimeSmoke` on an interactive Windows desktop to make the WinUI run marker a hard gate. `build\consumer-smoke.ps1` can be run separately against an existing package. `-SkipConsumerSmoke`, `-SkipWinUISmoke`, and `-SkipElectronRuntimeSmoke` are available for constrained environments, and every skipped runtime check is recorded in the manifest.

## Dataway Contract

The SDK posts RUM `text/plain` line protocol to `v1/write/rum` and Logging line protocol to `v1/write/logging`. The two data types use independent persistent queues and retry state, with RUM flushed first. Public Dataway requests append `token=<clientToken>&to_headless=true`; local DataKit requests do not require a token. 2xx through 4xx responses are treated as terminal for queued data, matching the Android SDK retry boundary; 5xx and network failures remain queued for retry with exponential backoff and jitter. HTTP header capture redacts credentials such as `Authorization`, `Cookie`, and API-token headers by default. Native WinHTTP upload URL-encodes Dataway tokens, supports request timeout and named proxy configuration, and exposes last status/error/latency through diagnostics.

The experimental Session Replay transport posts Windows-identified `multipart/form-data` (`sdk_name=df_windows_rum_sdk`, `source=windows`) to `v1/write/rum/replay`. It can be enabled and verified, including Browser rrweb records delivered by Electron/WebView bridges, but playback compatibility, privacy, stability, and performance remain experimental and are not part of the stable release claim.
