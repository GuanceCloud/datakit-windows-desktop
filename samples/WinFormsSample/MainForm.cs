using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Guance.Rum.Windows;
using System.Windows.Forms;

namespace WinFormsSample;

public sealed class MainForm : Form
{
    private readonly HttpClient httpClient = new(RumSdk.CreateHttpMessageHandler());
    private readonly TextBox userIdBox = new() { Text = "sample-user-001" };
    private readonly TextBox userNameBox = new() { Text = "WinForms Sample User" };
    private readonly TextBox userEmailBox = new() { Text = "sample@example.com" };
    private readonly TextBox viewNameBox = new() { Text = "WinFormsManualView" };
    private readonly TextBox actionNameBox = new() { Text = "SampleAction" };
    private readonly TextBox resourceUrlBox = new() { Text = "https://example.com/" };
    private readonly TextBox replayPrivacyBox = new() { Text = "sample@example.com" };
    private readonly Panel replayHiddenTarget = new();
    private readonly TextBox sampleLogBox = new();
    private int sampleCounter;
    private bool diagnosticListenerAttached;

    public MainForm()
    {
        Text = "Guance RUM WinForms Sample";
        Width = 920;
        Height = 680;
        MinimumSize = new Size(760, 560);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));

        layout.Controls.Add(new Label
        {
            Text = "Guance RUM WinForms Sample",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildUserContextPage());
        tabs.TabPages.Add(BuildViewActionPage());
        tabs.TabPages.Add(BuildResourcePage());
        tabs.TabPages.Add(BuildErrorLongTaskPage());
        tabs.TabPages.Add(BuildSessionReplayPage());
        tabs.TabPages.Add(BuildDiagnosticsPage());
        layout.Controls.Add(tabs, 0, 1);

        sampleLogBox.Dock = DockStyle.Fill;
        sampleLogBox.Multiline = true;
        sampleLogBox.ReadOnly = true;
        sampleLogBox.ScrollBars = ScrollBars.Vertical;
        sampleLogBox.Font = new Font("Consolas", 9);
        layout.Controls.Add(sampleLogBox, 0, 2);

        Controls.Add(layout);
        AppendLog("Ready. SDK initialization is performed by Program.Main.");
    }

    private TabPage BuildUserContextPage()
    {
        var page = CreatePage("User && Context");
        AddPageControl(page, CreateLabeledInput("User ID", userIdBox));
        AddPageControl(page, CreateLabeledInput("Name", userNameBox));
        AddPageControl(page, CreateLabeledInput("Email", userEmailBox));
        AddPageControl(page, CreateButtonPanel(
            SampleButton("Set User", OnSetUserClicked),
            SampleButton("Clear User", OnClearUserClicked),
            SampleButton("Add Global Context", OnAddGlobalContextClicked),
            SampleButton("Add RUM Context", OnAddRumContextClicked)));
        return page;
    }

    private TabPage BuildViewActionPage()
    {
        var page = CreatePage("View && Action");
        AddPageControl(page, CreateLabeledInput("View", viewNameBox));
        AddPageControl(page, CreateLabeledInput("Action", actionNameBox));
        AddPageControl(page, CreateButtonPanel(
            SampleButton("Start View", OnStartViewClicked),
            SampleButton("Stop View", OnStopViewClicked),
            SampleButton("Scoped Action", OnScopedActionClicked),
            SampleButton("Add Action", OnAddActionClicked)));
        return page;
    }

    private TabPage BuildResourcePage()
    {
        var page = CreatePage("Resource");
        AddPageControl(page, CreateLabeledInput("URL", resourceUrlBox));
        AddPageControl(page, CreateButtonPanel(
            SampleButton("HTTP Success", OnAutoHttpSuccessClicked),
            SampleButton("HTTP Failure", OnAutoHttpFailureClicked),
            SampleButton("Manual Resource Success", OnManualResourceSuccessClicked),
            SampleButton("Manual Resource Error", OnManualResourceErrorClicked)));
        return page;
    }

    private TabPage BuildErrorLongTaskPage()
    {
        var page = CreatePage("Error && LongTask");
        AddPageControl(page, CreateButtonPanel(
            SampleButton("Add Exception Error", OnAddExceptionErrorClicked),
            SampleButton("Add Custom Error", OnAddCustomErrorClicked),
            SampleButton("Add Long Task", OnAddLongTaskClicked),
            SampleButton("Block UI Thread", OnBlockUiThreadClicked)));
        return page;
    }

    private TabPage BuildSessionReplayPage()
    {
        var page = CreatePage("Session Replay (Phase 2)");
        page.Enabled = false;
        AddPageControl(page, new Label
        {
            Text = "Session Replay is disabled for the Phase 1 release.",
            AutoSize = true
        });
        AddPageControl(page, CreateLabeledInput("Replay Input", replayPrivacyBox));
        replayHiddenTarget.BorderStyle = BorderStyle.FixedSingle;
        replayHiddenTarget.Height = 48;
        replayHiddenTarget.Width = 440;
        replayHiddenTarget.Margin = new Padding(0, 0, 0, 8);
        replayHiddenTarget.Controls.Add(new Label
        {
            Text = "Element used by hidden-element privacy calls",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        });
        AddPageControl(page, replayHiddenTarget);
        AddPageControl(page, CreateButtonPanel(
            SampleButton("Start Recording", OnStartReplayClicked),
            SampleButton("Stop Recording", OnStopReplayClicked),
            SampleButton("Mask Text", OnMaskReplayTextClicked),
            SampleButton("Allow Text", OnAllowReplayTextClicked),
            SampleButton("Hide Touch", OnHideReplayTouchClicked),
            SampleButton("Show Touch", OnShowReplayTouchClicked),
            SampleButton("Hide Element", OnHideReplayElementClicked),
            SampleButton("Show Element", OnShowReplayElementClicked)));
        return page;
    }

    private TabPage BuildDiagnosticsPage()
    {
        var page = CreatePage("Diagnostics");
        AddPageControl(page, CreateButtonPanel(
            SampleButton("Flush", OnFlushClicked),
            SampleButton("Snapshot", OnSnapshotClicked),
            SampleButton("Add Listener", OnAddDiagnosticListenerClicked),
            SampleButton("Remove Listener", OnRemoveDiagnosticListenerClicked),
            SampleButton("Clear Log", OnClearLogClicked)));
        return page;
    }

    private static TabPage CreatePage(string title)
    {
        var page = new TabPage(title) { AutoScroll = true };
        page.Controls.Add(new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(16)
        });
        return page;
    }

    private static void AddPageControl(TabPage page, Control control)
    {
        var content = (TableLayoutPanel)page.Controls[0];
        var row = content.RowCount++;
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        content.Controls.Add(control, 0, row);
    }

    private static Control CreateLabeledInput(string label, TextBox textBox)
    {
        var panel = new TableLayoutPanel
        {
            Width = 560,
            Height = 36,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        textBox.Dock = DockStyle.Fill;
        panel.Controls.Add(textBox, 1, 0);
        return panel;
    }

    private static FlowLayoutPanel CreateButtonPanel(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    private static Button SampleButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 36,
            Margin = new Padding(0, 0, 8, 8),
            Padding = new Padding(12, 4, 12, 4)
        };
        button.Click += handler;
        return button;
    }

    private void OnSetUserClicked(object? sender, EventArgs e)
    {
        RunSample("Set user", () =>
        {
            var userId = TextOrDefault(userIdBox, "sample-user-001");
            RumSdk.SetUser(
                userId,
                NullIfWhiteSpace(userNameBox.Text),
                NullIfWhiteSpace(userEmailBox.Text),
                SampleProperties("set_user"));
            AppendLog($"User set: {userId}");
        });
    }

    private void OnClearUserClicked(object? sender, EventArgs e)
    {
        RunSample("Clear user", () =>
        {
            RumSdk.ClearUser();
            AppendLog("User cleared.");
        });
    }

    private void OnAddGlobalContextClicked(object? sender, EventArgs e)
    {
        RunSample("Add global context", () =>
        {
            var value = NextSampleValue("global");
            RumSdk.AddGlobalContext("sample_global_context", value);
            AppendLog($"Global context sample_global_context={value}");
        });
    }

    private void OnAddRumContextClicked(object? sender, EventArgs e)
    {
        RunSample("Add RUM context", () =>
        {
            var value = NextSampleValue("rum");
            RumSdk.AddRumGlobalContext("sample_rum_context", value);
            AppendLog($"RUM context sample_rum_context={value}");
        });
    }

    private void OnStartViewClicked(object? sender, EventArgs e)
    {
        RunSample("Start view", () =>
        {
            var viewName = TextOrDefault(viewNameBox, "WinFormsManualView");
            RumSdk.StartView(viewName, SampleProperties("start_view"));
            AppendLog($"View started: {viewName}");
        });
    }

    private void OnStopViewClicked(object? sender, EventArgs e)
    {
        RunSample("Stop view", () =>
        {
            RumSdk.StopView(SampleProperties("stop_view"));
            AppendLog("View stopped.");
        });
    }

    private async void OnScopedActionClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Scoped action", async () =>
        {
            var actionName = TextOrDefault(actionNameBox, "SampleAction");
            using (RumSdk.StartAction(actionName, "click", SampleProperties("scoped_action")))
            {
                await Task.Delay(250);
            }

            AppendLog($"Scoped action completed: {actionName}");
        });
    }

    private void OnAddActionClicked(object? sender, EventArgs e)
    {
        RunSample("Add action", () =>
        {
            var actionName = TextOrDefault(actionNameBox, "SampleAction");
            RumSdk.AddAction(actionName, "custom", TimeSpan.FromMilliseconds(120), SampleProperties("add_action"));
            AppendLog($"Action added: {actionName}");
        });
    }

    private async void OnAutoHttpSuccessClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Automatic HTTP success", async () =>
        {
            using (RumSdk.StartAction("Auto HTTP success", "click", SampleProperties("auto_http_success")))
            using (var response = await httpClient.GetAsync(GetResourceUrl()))
            {
                AppendLog($"HTTP success status={(int)response.StatusCode}");
            }
        });
    }

    private async void OnAutoHttpFailureClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Automatic HTTP failure", async () =>
        {
            using (RumSdk.StartAction("Auto HTTP failure", "click", SampleProperties("auto_http_failure")))
            using (var response = await httpClient.GetAsync("https://example.com/not-found"))
            {
                AppendLog($"HTTP failure sample status={(int)response.StatusCode}");
            }
        });
    }

    private async void OnManualResourceSuccessClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Manual resource success", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var resourceId = RumSdk.StartResource(GetResourceUrl(), "GET", SampleProperties("manual_resource_start"));
            await Task.Delay(120);
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
                    source: "winforms_sample"),
                responseSize: 2048,
                requestSize: 256,
                resourceType: "document",
                requestHeader: "accept: text/html",
                responseHeader: "content-type: text/html",
                properties: SampleProperties("manual_resource_success"));

            AppendLog($"Manual resource success id={resourceId}");
        });
    }

    private async void OnManualResourceErrorClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Manual resource error", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var resourceId = RumSdk.StartResource("https://example.invalid/sample", "POST", SampleProperties("manual_resource_error_start"));
            await Task.Delay(90);
            stopwatch.Stop();

            RumSdk.StopResource(
                resourceId,
                503,
                RumResourceTiming.FromTotalElapsed(stopwatch.Elapsed, "winforms_sample"),
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

    private void OnAddExceptionErrorClicked(object? sender, EventArgs e)
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

    private void OnAddCustomErrorClicked(object? sender, EventArgs e)
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

    private void OnAddLongTaskClicked(object? sender, EventArgs e)
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

    private void OnBlockUiThreadClicked(object? sender, EventArgs e)
    {
        RunSample("Block UI thread", () =>
        {
            Thread.Sleep(900);
            AppendLog("UI thread block completed.");
        });
    }

    private void OnStartReplayClicked(object? sender, EventArgs e)
    {
        AppendLog("Session Replay is disabled for the Phase 1 release.");
    }

    private void OnStopReplayClicked(object? sender, EventArgs e)
    {
        AppendLog("Session Replay is disabled for the Phase 1 release.");
    }

    private void OnMaskReplayTextClicked(object? sender, EventArgs e)
    {
        RunSample("Mask replay text", () =>
        {
            RumSdk.SetSessionReplayTextAndInputPrivacy(replayPrivacyBox, SessionReplayTextAndInputPrivacy.MaskAllInputs);
            AppendLog("Replay text/input privacy set to MaskAllInputs.");
        });
    }

    private void OnAllowReplayTextClicked(object? sender, EventArgs e)
    {
        RunSample("Allow replay text", () =>
        {
            RumSdk.SetSessionReplayTextAndInputPrivacy(replayPrivacyBox, SessionReplayTextAndInputPrivacy.Allow);
            AppendLog("Replay text/input privacy set to Allow.");
        });
    }

    private void OnHideReplayTouchClicked(object? sender, EventArgs e)
    {
        RunSample("Hide replay touch", () =>
        {
            RumSdk.SetSessionReplayTouchPrivacy(replayPrivacyBox, SessionReplayTouchPrivacy.Hide);
            AppendLog("Replay touch privacy set to Hide.");
        });
    }

    private void OnShowReplayTouchClicked(object? sender, EventArgs e)
    {
        RunSample("Show replay touch", () =>
        {
            RumSdk.SetSessionReplayTouchPrivacy(replayPrivacyBox, SessionReplayTouchPrivacy.Show);
            AppendLog("Replay touch privacy set to Show.");
        });
    }

    private void OnHideReplayElementClicked(object? sender, EventArgs e)
    {
        RunSample("Hide replay element", () =>
        {
            RumSdk.SetSessionReplayHidden(replayHiddenTarget);
            AppendLog("Replay element hidden.");
        });
    }

    private void OnShowReplayElementClicked(object? sender, EventArgs e)
    {
        RunSample("Show replay element", () =>
        {
            RumSdk.SetSessionReplayHidden(replayHiddenTarget, false);
            AppendLog("Replay element visible.");
        });
    }

    private async void OnFlushClicked(object? sender, EventArgs e)
    {
        await RunSampleAsync("Flush", async () =>
        {
            await RumSdk.FlushAsync();
            AppendLog("Flush completed.");
        });
    }

    private void OnSnapshotClicked(object? sender, EventArgs e)
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

    private void OnAddDiagnosticListenerClicked(object? sender, EventArgs e)
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

    private void OnRemoveDiagnosticListenerClicked(object? sender, EventArgs e)
    {
        RunSample("Remove diagnostic listener", () =>
        {
            DetachDiagnosticListener();
            AppendLog("Diagnostic listener removed.");
        });
    }

    private void OnClearLogClicked(object? sender, EventArgs e)
    {
        sampleLogBox.Clear();
        AppendLog("Log cleared.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachDiagnosticListener();
            httpClient.Dispose();
        }

        base.Dispose(disposing);
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
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
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
        sampleLogBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        sampleLogBox.SelectionStart = sampleLogBox.TextLength;
        sampleLogBox.ScrollToCaret();
    }

    private string GetResourceUrl()
    {
        return TextOrDefault(resourceUrlBox, "https://example.com/");
    }

    private string NextSampleValue(string prefix)
    {
        return $"{prefix}-{++sampleCounter}";
    }

    private Dictionary<string, object?> SampleProperties(string sampleCase)
    {
        return new Dictionary<string, object?>
        {
            ["sample_source"] = "winforms-sample",
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
