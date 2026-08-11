using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Guance.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI3Sample;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new(GuanceSdk.CreateHttpMessageHandler());
    private int sampleCounter;
    private bool diagnosticListenerAttached;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        AppendLog("Ready. SDK initialization is performed by App.");
    }

    private void OnSetUserClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Set user", () =>
        {
            var userId = TextOrDefault(UserIdBox, "sample-user-001");
            GuanceSdk.SetUser(
                userId,
                NullIfWhiteSpace(UserNameBox.Text),
                NullIfWhiteSpace(UserEmailBox.Text),
                SampleProperties("set_user"));
            AppendLog($"User set: {userId}");
        });
    }

    private void OnClearUserClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Clear user", () =>
        {
            GuanceSdk.ClearUser();
            AppendLog("User cleared.");
        });
    }

    private void OnAddGlobalContextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add global context", () =>
        {
            var value = NextSampleValue("global");
            GuanceSdk.AddGlobalContext("sample_global_context", value);
            AppendLog($"Global context sample_global_context={value}");
        });
    }

    private void OnAddRumContextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add RUM context", () =>
        {
            var value = NextSampleValue("rum");
            GuanceSdk.AddRumGlobalContext("sample_rum_context", value);
            AppendLog($"RUM context sample_rum_context={value}");
        });
    }

    private void OnStartViewClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Start view", () =>
        {
            var viewName = TextOrDefault(ViewNameBox, "WinUIManualView");
            GuanceSdk.StartView(viewName, SampleProperties("start_view"));
            AppendLog($"View started: {viewName}");
        });
    }

    private void OnStopViewClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Stop view", () =>
        {
            GuanceSdk.StopView(SampleProperties("stop_view"));
            AppendLog("View stopped.");
        });
    }

    private async void OnScopedActionClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Scoped action", async () =>
        {
            var actionName = TextOrDefault(ActionNameBox, "SampleAction");
            using (GuanceSdk.StartAction(actionName, "click", needWait: true, properties: SampleProperties("scoped_action")))
            {
                await Task.Delay(250);
            }

            AppendLog($"Scoped action completed: {actionName}");
        });
    }

    private void OnAddActionClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add action", () =>
        {
            var actionName = TextOrDefault(ActionNameBox, "SampleAction");
            GuanceSdk.AddAction(actionName, "custom", TimeSpan.FromMilliseconds(120), SampleProperties("add_action"));
            AppendLog($"Action added: {actionName}");
        });
    }

    private async void OnAutoHttpSuccessClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Automatic HTTP success", async () =>
        {
            using (GuanceSdk.StartAction("Auto HTTP success", "click", needWait: true, properties: SampleProperties("auto_http_success")))
            using (var response = await httpClient.GetAsync(GetResourceUrl()))
            {
                AppendLog($"HTTP success status={(int)response.StatusCode}");
            }
        });
    }

    private async void OnAutoHttpFailureClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Automatic HTTP failure", async () =>
        {
            using (GuanceSdk.StartAction("Auto HTTP failure", "click", needWait: true, properties: SampleProperties("auto_http_failure")))
            using (var response = await httpClient.GetAsync("https://example.com/not-found"))
            {
                AppendLog($"HTTP failure sample status={(int)response.StatusCode}");
            }
        });
    }

    private async void OnManualResourceSuccessClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Manual resource success", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var resourceId = GuanceSdk.StartResource(GetResourceUrl(), "GET", SampleProperties("manual_resource_start"));
            await Task.Delay(120);
            stopwatch.Stop();

            GuanceSdk.StopResource(
                resourceId,
                200,
                RumResourceTiming.FromPhases(
                    dns: TimeSpan.FromMilliseconds(8),
                    tcp: TimeSpan.FromMilliseconds(12),
                    ssl: TimeSpan.FromMilliseconds(18),
                    ttfb: TimeSpan.FromMilliseconds(40),
                    totalDuration: stopwatch.Elapsed,
                    source: "winui_sample"),
                responseSize: 2048,
                requestSize: 256,
                resourceType: "document",
                requestHeader: "accept: text/html",
                responseHeader: "content-type: text/html",
                properties: SampleProperties("manual_resource_success"));

            AppendLog($"Manual resource success id={resourceId}");
        });
    }

    private async void OnManualResourceErrorClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Manual resource error", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var resourceId = GuanceSdk.StartResource("https://example.invalid/sample", "POST", SampleProperties("manual_resource_error_start"));
            await Task.Delay(90);
            stopwatch.Stop();

            GuanceSdk.StopResource(
                resourceId,
                503,
                RumResourceTiming.FromTotalElapsed(stopwatch.Elapsed, "winui_sample"),
                responseSize: 0,
                requestSize: 512,
                resourceType: "native",
                requestHeader: "content-type: application/json",
                responseHeader: "content-type: application/json",
                errorStack: "Sample manual resource failure stack",
                errorMessage: "Sample manual resource failure",
                properties: SampleProperties("manual_resource_error"));

            AppendLog($"Manual resource error id={resourceId}");
        });
    }

    private void OnAddExceptionErrorClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add exception error", () =>
        {
            try
            {
                throw new InvalidOperationException("sample exception error");
            }
            catch (Exception ex)
            {
                GuanceSdk.AddError(ex, SampleProperties("exception_error"));
                AppendLog($"Exception error added: {ex.GetType().Name}");
            }
        });
    }

    private void OnAddCustomErrorClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add custom error", () =>
        {
            GuanceSdk.AddError(
                "Sample custom error stack",
                "Sample custom error message",
                "SampleError",
                "logger",
                SampleProperties("custom_error"));
            AppendLog("Custom error added.");
        });
    }

    private void OnAddLongTaskClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add long task", () =>
        {
            GuanceSdk.AddLongTask(
                TimeSpan.FromMilliseconds(750),
                "Sample long task stack",
                SampleProperties("add_long_task"));
            AppendLog("Long task added.");
        });
    }

    private void OnBlockUiThreadClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Block UI thread", () =>
        {
            Thread.Sleep(900);
            AppendLog("UI thread block completed.");
        });
    }

    private void OnStartReplayClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Start experimental Session Replay", () =>
        {
            GuanceSdk.StartSessionReplayRecording();
            AppendLog("Experimental Session Replay start requested (requires sessionReplayEnabled=true).");
        });
    }

    private void OnStopReplayClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Stop experimental Session Replay", () =>
        {
            GuanceSdk.StopSessionReplayRecording();
            AppendLog("Experimental Session Replay stopped.");
        });
    }

    private void OnMaskReplayTextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Mask replay text", () =>
        {
            GuanceSdk.SetSessionReplayTextAndInputPrivacy(ReplayPrivacyBox, SessionReplayTextAndInputPrivacy.MaskAllInputs);
            AppendLog("Replay text/input privacy set to MaskAllInputs.");
        });
    }

    private void OnAllowReplayTextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Allow replay text", () =>
        {
            GuanceSdk.SetSessionReplayTextAndInputPrivacy(ReplayPrivacyBox, SessionReplayTextAndInputPrivacy.Allow);
            AppendLog("Replay text/input privacy set to Allow.");
        });
    }

    private void OnHideReplayTouchClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Hide replay touch", () =>
        {
            GuanceSdk.SetSessionReplayTouchPrivacy(ReplayPrivacyBox, SessionReplayTouchPrivacy.Hide);
            AppendLog("Replay touch privacy set to Hide.");
        });
    }

    private void OnShowReplayTouchClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Show replay touch", () =>
        {
            GuanceSdk.SetSessionReplayTouchPrivacy(ReplayPrivacyBox, SessionReplayTouchPrivacy.Show);
            AppendLog("Replay touch privacy set to Show.");
        });
    }

    private void OnHideReplayElementClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Hide replay element", () =>
        {
            GuanceSdk.SetSessionReplayHidden(ReplayHiddenTarget);
            AppendLog("Replay element hidden.");
        });
    }

    private void OnShowReplayElementClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Show replay element", () =>
        {
            GuanceSdk.SetSessionReplayHidden(ReplayHiddenTarget, false);
            AppendLog("Replay element visible.");
        });
    }

    private async void OnFlushClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Flush", async () =>
        {
            await GuanceSdk.FlushAsync();
            AppendLog("Flush completed.");
        });
    }

    private void OnSnapshotClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Diagnostics snapshot", () =>
        {
            var snapshot = GuanceSdk.GetDiagnosticsSnapshot();
            var cache = GuanceSdk.GetCacheDiagnosticsSnapshot();
            var logs = GuanceSdk.GetLogDiagnosticsSnapshot();
            AppendLog(
                $"Snapshot session={snapshot.SessionId} sampled={snapshot.SessionSampled} " +
                $"activeView={snapshot.HasActiveView} activeActions={snapshot.ActiveActionCount} " +
                $"activeResources={snapshot.ActiveResourceCount} enqueued={snapshot.RumEventsEnqueued} " +
                $"rumOk={snapshot.RumUploadSuccessCount} replayOk={snapshot.ReplayUploadSuccessCount} " +
                $"lastRumStatus={snapshot.LastRumUploadStatusCode} cacheBytes={cache.AllocatedBytes} " +
                $"logsEnqueued={logs.LogsEnqueued} logUploadOk={logs.UploadSuccessCount}");
        });
    }

    private void OnAddDiagnosticListenerClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add diagnostic listener", () =>
        {
            if (!diagnosticListenerAttached)
            {
                GuanceSdk.AddDiagnosticListener(OnRumDiagnostic);
                diagnosticListenerAttached = true;
            }

            AppendLog("Diagnostic listener attached.");
        });
    }

    private void OnRemoveDiagnosticListenerClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Remove diagnostic listener", () =>
        {
            DetachDiagnosticListener();
            AppendLog("Diagnostic listener removed.");
        });
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        SampleLogBox.Text = string.Empty;
        AppendLog("Log cleared.");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        DetachDiagnosticListener();
        httpClient.Dispose();
    }

    private async Task RunSampleAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog($"{name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RunSample(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppendLog($"{name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnRumDiagnostic(object? sender, RumDiagnosticEvent item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var status = item.StatusCode is null ? "" : $" status={item.StatusCode}";
            AppendLog($"Diagnostic {item.Level} {item.Source}{status}: {item.Message}");
        });
    }

    private void DetachDiagnosticListener()
    {
        if (!diagnosticListenerAttached)
        {
            return;
        }

        GuanceSdk.RemoveDiagnosticListener(OnRumDiagnostic);
        diagnosticListenerAttached = false;
    }

    private void AppendLog(string message)
    {
        SampleLogBox.Text += $"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}";
    }

    private string GetResourceUrl()
    {
        return TextOrDefault(ResourceUrlBox, "https://example.com/");
    }

    private string NextSampleValue(string prefix)
    {
        return $"{prefix}-{++sampleCounter}";
    }

    private Dictionary<string, object?> SampleProperties(string sampleCase)
    {
        return new Dictionary<string, object?>
        {
            ["sample_source"] = "winui-sample",
            ["sample_case"] = sampleCase,
            ["sample_counter"] = ++sampleCounter,
            ["sample_time"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private static string TextOrDefault(TextBox textBox, string fallback)
    {
        return string.IsNullOrWhiteSpace(textBox.Text) ? fallback : textBox.Text.Trim();
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
