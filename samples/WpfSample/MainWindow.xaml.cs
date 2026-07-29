using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Guance.Rum.Windows;

namespace WpfSample;

public partial class MainWindow : Window
{
    private const string WebViewTestUrlEnvironmentVariable = "GUANCE_RUM_WEBVIEW_TEST_URL";
    private readonly HttpClient httpClient = new(RumSdk.CreateHttpMessageHandler());
    private int sampleCounter;
    private bool diagnosticListenerAttached;
    private bool networkReplayImagesLoading;
    private bool webViewAttached;

    public MainWindow()
    {
        InitializeComponent();
        AppendLog("Ready. SDK initialization is performed by App.OnStartup.");
    }

    private async void OnNetworkReplayImagesLoaded(object sender, RoutedEventArgs e)
    {
        if (networkReplayImagesLoading || NetworkReplayPngImage.Source is not null || NetworkReplayJpegImage.Source is not null)
        {
            return;
        }

        networkReplayImagesLoading = true;
        NetworkReplayImageStatus.Text = "Loading network images...";
        try
        {
            var results = await Task.WhenAll(
                LoadNetworkReplayImageAsync(NetworkReplayPngImage, "https://picsum.photos/seed/standalone/200/200"),
                LoadNetworkReplayImageAsync(NetworkReplayJpegImage, "https://picsum.photos/seed/composition/200/200"));
            var loadedCount = results.Count(loaded => loaded);
            NetworkReplayImageStatus.Text = $"Loaded {loadedCount}/2 network images from picsum.photos";
            AppendLog($"Replay network image composition loaded {loadedCount}/2 images.");
        }
        catch (Exception ex)
        {
            NetworkReplayImageStatus.Text = $"Network images unavailable: {ex.GetType().Name}";
            AppendLog($"Replay network images failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            networkReplayImagesLoading = false;
        }
    }

    private async Task<bool> LoadNetworkReplayImageAsync(Image target, string url)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer).ConfigureAwait(true);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();
            target.Source = bitmap;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Network Replay image {url} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void OnMaskNetworkReplayImagesClicked(object sender, RoutedEventArgs e)
    {
        RumSdk.SetSessionReplayImagePrivacy(NetworkReplayImagePanel, SessionReplayImagePrivacy.MaskAll);
        AppendLog("Replay network image privacy set to MaskAll.");
    }

    private void OnMaskLargeNetworkReplayImagesClicked(object sender, RoutedEventArgs e)
    {
        RumSdk.SetSessionReplayImagePrivacy(NetworkReplayImagePanel, SessionReplayImagePrivacy.MaskLargeOnly);
        AppendLog("Replay network image privacy set to MaskLargeOnly.");
    }

    private void OnShowNetworkReplayImagesClicked(object sender, RoutedEventArgs e)
    {
        RumSdk.SetSessionReplayImagePrivacy(NetworkReplayImagePanel, SessionReplayImagePrivacy.MaskNone);
        AppendLog("Replay network image privacy set to MaskNone.");
    }

    private async void OnWebViewTestLoaded(object sender, RoutedEventArgs e)
    {
        if (webViewAttached)
        {
            return;
        }

        var configuredUrl = Environment.GetEnvironmentVariable(WebViewTestUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            WebViewTestStatus.Text = $"WebView2 smoke page skipped: {WebViewTestUrlEnvironmentVariable} is not set.";
            AppendLog(WebViewTestStatus.Text);
            return;
        }

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            WebViewTestStatus.Text = $"{WebViewTestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.";
            AppendLog(WebViewTestStatus.Text);
            return;
        }

        webViewAttached = true;
        try
        {
            RumSdk.AttachWebView(WebViewTestBrowser);
            await WebViewTestBrowser.EnsureCoreWebView2Async().ConfigureAwait(true);
            WebViewTestBrowser.Source = uri;
            WebViewTestStatus.Text = $"WebView2 RUM bridge attached. Loading {uri.Host}.";
            AppendLog(WebViewTestStatus.Text);
        }
        catch (Exception ex)
        {
            webViewAttached = false;
            WebViewTestStatus.Text = $"WebView2 initialization failed: {ex.GetType().Name}: {ex.Message}";
            AppendLog(WebViewTestStatus.Text);
        }
    }

    private void OnSetUserClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Set user", () =>
        {
            var userId = TextOrDefault(UserIdBox, "sample-user-001");
            RumSdk.SetUser(
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
            RumSdk.ClearUser();
            AppendLog("User cleared.");
        });
    }

    private void OnAddGlobalContextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add global context", () =>
        {
            var value = NextSampleValue("global");
            RumSdk.AddGlobalContext("sample_global_context", value);
            AppendLog($"Global context sample_global_context={value}");
        });
    }

    private void OnAddRumContextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add RUM context", () =>
        {
            var value = NextSampleValue("rum");
            RumSdk.AddRumGlobalContext("sample_rum_context", value);
            AppendLog($"RUM context sample_rum_context={value}");
        });
    }

    private void OnStartViewClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Start view", () =>
        {
            var viewName = TextOrDefault(ViewNameBox, "WpfManualView");
            RumSdk.StartView(viewName, SampleProperties("start_view"));
            AppendLog($"View started: {viewName}");
        });
    }

    private void OnStopViewClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Stop view", () =>
        {
            RumSdk.StopView(SampleProperties("stop_view"));
            AppendLog("View stopped.");
        });
    }

    private async void OnScopedActionClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Scoped action", async () =>
        {
            var actionName = TextOrDefault(ActionNameBox, "SampleAction");
            using (RumSdk.StartAction(actionName, "click", SampleProperties("scoped_action")))
            {
                await Task.Delay(250).ConfigureAwait(true);
            }

            AppendLog($"Scoped action completed: {actionName}");
        });
    }

    private void OnAddActionClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add action", () =>
        {
            var actionName = TextOrDefault(ActionNameBox, "SampleAction");
            RumSdk.AddAction(actionName, "custom", TimeSpan.FromMilliseconds(120), SampleProperties("add_action"));
            AppendLog($"Action added: {actionName}");
        });
    }

    private async void OnAutoHttpSuccessClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Automatic HTTP success", async () =>
        {
            using (RumSdk.StartAction("Auto HTTP success", "click", SampleProperties("auto_http_success")))
            using (var response = await httpClient.GetAsync(GetResourceUrl()).ConfigureAwait(true))
            {
                AppendLog($"HTTP success status={(int)response.StatusCode}");
            }
        });
    }

    private async void OnAutoHttpFailureClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Automatic HTTP failure", async () =>
        {
            using (RumSdk.StartAction("Auto HTTP failure", "click", SampleProperties("auto_http_failure")))
            using (var response = await httpClient.GetAsync("https://example.com/not-found").ConfigureAwait(true))
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
            var resourceId = RumSdk.StartResource(GetResourceUrl(), "GET", SampleProperties("manual_resource_start"));
            await Task.Delay(120).ConfigureAwait(true);
            stopwatch.Stop();

            RumSdk.StopResource(
                resourceId,
                200,
                RumResourceTiming.FromPhases(
                    dns: TimeSpan.FromMilliseconds(8),
                    tcp: TimeSpan.FromMilliseconds(12),
                    ssl: TimeSpan.FromMilliseconds(18),
                    ttfb: TimeSpan.FromMilliseconds(40),
                    totalDuration: stopwatch.Elapsed,
                    source: "wpf_sample"),
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
            var resourceId = RumSdk.StartResource("https://example.invalid/sample", "POST", SampleProperties("manual_resource_error_start"));
            await Task.Delay(90).ConfigureAwait(true);
            stopwatch.Stop();

            RumSdk.StopResource(
                resourceId,
                503,
                RumResourceTiming.FromTotalElapsed(stopwatch.Elapsed, "wpf_sample"),
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
                RumSdk.AddError(ex, SampleProperties("exception_error"));
                AppendLog($"Exception error added: {ex.GetType().Name}");
            }
        });
    }

    private void OnAddCustomErrorClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add custom error", () =>
        {
            RumSdk.AddError(
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
            RumSdk.AddLongTask(
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
        AppendLog("Session Replay is disabled for the Phase 1 release.");
    }

    private void OnStopReplayClicked(object sender, RoutedEventArgs e)
    {
        AppendLog("Session Replay is disabled for the Phase 1 release.");
    }

    private void OnMaskReplayTextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Mask replay text", () =>
        {
            RumSdk.SetSessionReplayTextAndInputPrivacy(ReplayPrivacyBox, SessionReplayTextAndInputPrivacy.MaskAllInputs);
            AppendLog("Replay text/input privacy set to MaskAllInputs.");
        });
    }

    private void OnAllowReplayTextClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Allow replay text", () =>
        {
            RumSdk.SetSessionReplayTextAndInputPrivacy(ReplayPrivacyBox, SessionReplayTextAndInputPrivacy.Allow);
            AppendLog("Replay text/input privacy set to Allow.");
        });
    }

    private void OnHideReplayTouchClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Hide replay touch", () =>
        {
            RumSdk.SetSessionReplayTouchPrivacy(ReplayPrivacyBox, SessionReplayTouchPrivacy.Hide);
            AppendLog("Replay touch privacy set to Hide.");
        });
    }

    private void OnShowReplayTouchClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Show replay touch", () =>
        {
            RumSdk.SetSessionReplayTouchPrivacy(ReplayPrivacyBox, SessionReplayTouchPrivacy.Show);
            AppendLog("Replay touch privacy set to Show.");
        });
    }

    private void OnHideReplayElementClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Hide replay element", () =>
        {
            RumSdk.SetSessionReplayHidden(ReplayHiddenTarget);
            AppendLog("Replay element hidden.");
        });
    }

    private void OnShowReplayElementClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Show replay element", () =>
        {
            RumSdk.SetSessionReplayHidden(ReplayHiddenTarget, false);
            AppendLog("Replay element visible.");
        });
    }

    private async void OnFlushClicked(object sender, RoutedEventArgs e)
    {
        await RunSampleAsync("Flush", async () =>
        {
            await RumSdk.FlushAsync().ConfigureAwait(true);
            AppendLog("Flush completed.");
        });
    }

    private void OnSnapshotClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Diagnostics snapshot", () =>
        {
            var snapshot = RumSdk.GetDiagnosticsSnapshot();
            AppendLog(
                $"Snapshot session={snapshot.SessionId} sampled={snapshot.SessionSampled} " +
                $"activeView={snapshot.HasActiveView} activeActions={snapshot.ActiveActionCount} " +
                $"activeResources={snapshot.ActiveResourceCount} enqueued={snapshot.RumEventsEnqueued} " +
                $"rumOk={snapshot.RumUploadSuccessCount} replayOk={snapshot.ReplayUploadSuccessCount} " +
                $"lastRumStatus={snapshot.LastRumUploadStatusCode}");
        });
    }

    private void OnAddDiagnosticListenerClicked(object sender, RoutedEventArgs e)
    {
        RunSample("Add diagnostic listener", () =>
        {
            if (!diagnosticListenerAttached)
            {
                RumSdk.AddDiagnosticListener(OnRumDiagnostic);
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
        SampleLogBox.Clear();
        AppendLog("Log cleared.");
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachDiagnosticListener();
        if (webViewAttached)
        {
            RumSdk.DetachWebView(WebViewTestBrowser);
            webViewAttached = false;
        }
        httpClient.Dispose();
        base.OnClosed(e);
    }

    private async Task RunSampleAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
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
        Dispatcher.Invoke(() =>
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

        RumSdk.RemoveDiagnosticListener(OnRumDiagnostic);
        diagnosticListenerAttached = false;
    }

    private void AppendLog(string message)
    {
        SampleLogBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        SampleLogBox.ScrollToEnd();
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
            ["sample_source"] = "wpf-sample",
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
