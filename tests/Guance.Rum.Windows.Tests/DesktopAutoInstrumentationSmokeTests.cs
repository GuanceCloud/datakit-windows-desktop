#if WINDOWS
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;
using WpfButton = System.Windows.Controls.Button;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class DesktopAutoInstrumentationSmokeTests
{
    [Fact]
    public async Task WpfAutoInstrumentation_CapturesActionResizeAndReplay()
    {
        await RunStaAsync(async () =>
        {
            _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var rumQueue = new TestRumQueue();
            var replayQueue = new TestReplayQueue();
            await using var client = CreateClient(rumQueue, replayQueue);

            var button = new WpfButton { Name = "SmokeButton", Content = "Save" };
            var textBox = new WpfTextBox { Name = "SmokeInput", Text = "initial" };
            var window = new Window
            {
                Title = "WpfSmokeWindow",
                Width = 320,
                Height = 240,
                Content = new WpfStackPanel { Children = { button, textBox } }
            };

            window.Show();
            window.Activate();
            client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
            {
                EnableWpf = true,
                EnableWinForms = false,
                EnableWinUI = false,
                EnableHttpClient = false,
                EnableUnhandledException = false,
                EnableUiThreadBlock = false
            });
            PumpWpfDispatcher();

            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            textBox.Text = "secret-value";
            window.Width = 420;
            window.Height = 300;
            PumpWpfDispatcher();

            await client.FlushAsync();
            window.Close();

            Assert.Contains(rumQueue.Items, item => item.Line.Contains("action", StringComparison.Ordinal) && item.Line.Contains("SmokeButton", StringComparison.Ordinal));
            var replayBodies = replayQueue.Items.Select(item => Encoding.UTF8.GetString(item.Body)).ToArray();
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"click\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"input\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"resize\"", StringComparison.Ordinal));
            Assert.DoesNotContain(replayBodies, body => body.Contains("secret-value", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task WinFormsAutoInstrumentation_CapturesActionResizeAndReplay()
    {
        await RunStaAsync(async () =>
        {
            var rumQueue = new TestRumQueue();
            var replayQueue = new TestReplayQueue();
            await using var client = CreateClient(rumQueue, replayQueue);

            using var form = new WinForms.Form { Text = "WinFormsSmokeWindow", Width = 320, Height = 240 };
            var button = new WinForms.Button { Name = "SmokeButton", Text = "Save", Left = 10, Top = 10, Width = 120 };
            var textBox = new WinForms.TextBox { Name = "SmokeInput", Text = "initial", Left = 10, Top = 50, Width = 180 };
            form.Controls.Add(button);
            form.Controls.Add(textBox);

            client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
            {
                EnableWpf = false,
                EnableWinForms = true,
                EnableWinUI = false,
                EnableHttpClient = false,
                EnableUnhandledException = false,
                EnableUiThreadBlock = false
            });

            Exception? callbackError = null;
            var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new WinForms.Timer { Interval = 200 };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    button.PerformClick();
                    textBox.Focus();
                    textBox.Text = "secret-value";
                    form.Width = 420;
                    form.Height = 300;
                    WinForms.Application.DoEvents();
                    await client.FlushAsync();
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
                finally
                {
                    completed.SetResult(null);
                    form.Close();
                    timer.Dispose();
                }
            };
            form.Shown += (_, _) => timer.Start();
            WinForms.Application.Run(form);
            await completed.Task;
            if (callbackError is not null)
            {
                throw callbackError;
            }

            Assert.Contains(rumQueue.Items, item => item.Line.Contains("action", StringComparison.Ordinal) && item.Line.Contains("SmokeButton", StringComparison.Ordinal));
            var replayBodies = replayQueue.Items.Select(item => Encoding.UTF8.GetString(item.Body)).ToArray();
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"click\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"input\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":\"resize\"", StringComparison.Ordinal));
            Assert.DoesNotContain(replayBodies, body => body.Contains("secret-value", StringComparison.Ordinal));
        });
    }

    private static RumClient CreateClient(TestRumQueue rumQueue, TestReplayQueue replayQueue)
    {
        return new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    FlushInterval = TimeSpan.FromMinutes(5)
                }
            },
            rumQueue,
            new RetryRumTransport(),
            replayQueue,
            new RetryReplayTransport());
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void PumpWpfDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestRumQueue : IRumQueue
    {
        private long nextId;

        public List<QueuedRumEvent> Items { get; } = new();

        public Task EnqueueAsync(string line, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QueuedRumEvent>>(Items.Take(count).ToArray());
        }

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => ids.Contains(item.Id));
            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestReplayQueue : ISessionReplayQueue
    {
        private long nextId;

        public List<QueuedSessionReplaySegment> Items { get; } = new();

        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(++nextId, contentType, body, DateTimeOffset.UtcNow, body.Length));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Items.Take(count).ToArray());
        }

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => ids.Contains(item.Id));
            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken)
        {
            return Task.FromResult(SendResult.Retry(500, "hold queue for smoke assertions"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class RetryReplayTransport : ISessionReplayTransport
    {
        public Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken)
        {
            return Task.FromResult(SendResult.Retry(500, "hold queue for smoke assertions"));
        }

        public void Dispose()
        {
        }
    }
}
#endif
