#if WINDOWS
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Guance.Windows.Queue;
using Guance.Windows.SessionReplay;
using Guance.Windows.Transport;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfImage = System.Windows.Controls.Image;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfSlider = System.Windows.Controls.Slider;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;
using Xunit;

namespace Guance.Windows.Tests;

[Collection(WpfUiTestCollection.Name)]
[Trait("Execution", "DesktopUiRuntime")]
public sealed class DesktopAutoInstrumentationSmokeTests
{
    [Fact]
    [Trait("Category", "Phase2")]
    public async Task WpfAutoInstrumentation_CapturesActionResizeAndReplay()
    {
        await RunStaAsync(async () =>
        {
            _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var rumQueue = new TestRumQueue();
            var replayQueue = new TestReplayQueue();
            await using var client = CreateClient(rumQueue, replayQueue);

            var button = new WpfButton { Name = "SmokeButton", Content = "Save" };
            var keyboardButton = new WpfButton { Name = "KeyboardButton", Content = "Open" };
            var textBox = new WpfTextBox { Name = "SmokeInput", Text = "initial" };
            var readOnlyTextBox = new WpfTextBox { Name = "ReadOnlyLog", Text = "ready", IsReadOnly = true };
            var slider = new WpfSlider { Name = "ReplaySlider", Minimum = 0, Maximum = 100, Value = 20 };
            var window = new Window
            {
                Title = "WpfSmokeWindow",
                Width = 320,
                Height = 240,
                Content = new WpfStackPanel { Children = { button, keyboardButton, textBox, readOnlyTextBox, slider } }
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

            await client.FlushAsync();
            var initialReplayBodies = replayQueue.Items.Select(item => Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body))).ToArray();
            Assert.Contains(initialReplayBodies, body => body.Contains("\"type\":10", StringComparison.Ordinal));

            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            Thread.Sleep(120);
            PumpWpfDispatcher();
            keyboardButton.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(keyboardButton),
                Environment.TickCount,
                System.Windows.Input.Key.Space)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
                Source = keyboardButton
            });
            keyboardButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, keyboardButton));
            Thread.Sleep(120);
            PumpWpfDispatcher();
            textBox.Focus();
            textBox.Text = "secret-value";
            readOnlyTextBox.Text = "programmatic log update";
            window.Width = 420;
            window.Height = 300;
            Thread.Sleep(120);
            PumpWpfDispatcher();
            slider.Value = 80;
            Thread.Sleep(120);
            PumpWpfDispatcher();

            await client.FlushAsync();
            window.Close();
            await client.FlushAsync();

            Assert.Contains(rumQueue.Items, item =>
                item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                item.Line.Contains("action_name=SmokeButton", StringComparison.Ordinal) &&
                item.Line.Contains("action_type=click", StringComparison.Ordinal));
            Assert.Contains(rumQueue.Items, item =>
                item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                item.Line.Contains("action_name=KeyboardButton", StringComparison.Ordinal) &&
                item.Line.Contains("action_type=key", StringComparison.Ordinal));
            AssertOnlyExpectedAutomaticActionTypes(rumQueue.Items);
            Assert.Contains(rumQueue.Items, item => item.Line.Contains("action", StringComparison.Ordinal) && item.Line.Contains("ReplaySlider", StringComparison.Ordinal));
            var replayBodies = replayQueue.Items.Select(item => Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body))).ToArray();
            Assert.Contains(replayBodies, body => body.Contains("\"source\":2", StringComparison.Ordinal) && body.Contains("\"positions\":[", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":0", StringComparison.Ordinal) && body.Contains("\"event_type\":\"input\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":4", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"type\":7", StringComparison.Ordinal));
            var fullSnapshotCount = replayBodies.Sum(body => body.Split("\"type\":10", StringSplitOptions.None).Length - 1);
            Assert.True(
                fullSnapshotCount >= 4,
                $"WPF interactions should refresh the Replay visual snapshot so changed control state is visible. count={fullSnapshotCount}");
            Assert.DoesNotContain(replayBodies, body => body.Contains("secret-value", StringComparison.Ordinal));
            Assert.DoesNotContain(rumQueue.Items, item => item.Line.Contains("ReadOnlyLog", StringComparison.Ordinal));
            Assert.DoesNotContain(replayBodies, body => body.Contains("ReadOnlyLog", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task WpfTreeMapper_DeduplicatesTemplatedContentText()
    {
        await RunStaAsync(() =>
        {
            var button = new WpfButton { Name = "ReplayButton", Content = "Start View", Width = 120, Height = 32 };
            var root = new WpfStackPanel { Children = { button } };
            root.Measure(new System.Windows.Size(320, 240));
            root.Arrange(new System.Windows.Rect(0, 0, 320, 240));
            button.ApplyTemplate();
            root.UpdateLayout();

            var mapped = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.Allow });
            Assert.NotNull(mapped);
            Assert.Single(Flatten(mapped!), node => string.Equals(node.Text, "Start View", StringComparison.Ordinal));

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WpfTreeMapper_MapsControlOutlineAndBackground()
    {
        await RunStaAsync(() =>
        {
            var button = new WpfButton
            {
                Name = "ReplayStyledButton",
                Content = "Styled control",
                Width = 160,
                Height = 40,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0xF6, 0xFF)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB)),
                BorderThickness = new Thickness(2)
            };
            var root = new WpfStackPanel { Children = { button } };
            root.Measure(new System.Windows.Size(320, 240));
            root.Arrange(new System.Windows.Rect(0, 0, 320, 240));
            button.ApplyTemplate();
            root.UpdateLayout();

            var mapped = SessionReplayTreeMapper.Map(root, new SessionReplayPrivacyOverrides(), new RumSessionReplayConfig());

            Assert.NotNull(mapped);
            var buttonNode = Assert.Single(Flatten(mapped!), node => node.Attributes.GetValueOrDefault("data-control-type") == typeof(WpfButton).Name);
            var outlinedNode = Assert.Single(
                Flatten(buttonNode),
                node => node.BackgroundColor == "#EFF6FFFF" && node.BorderColor == "#2563EBFF" && node.BorderWidth == 2);
            Assert.Equal("#EFF6FFFF", outlinedNode.BackgroundColor);
            Assert.Equal("#2563EBFF", outlinedNode.BorderColor);
            Assert.Equal(2, outlinedNode.BorderWidth);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WpfTreeMapper_AppliesTextAndInputPrivacyLevels()
    {
        await RunStaAsync(() =>
        {
            var normalInput = new WpfTextBox { Text = "ordinary input", Width = 160, Height = 32 };
            var sensitiveInput = new WpfTextBox { Name = "EmailBox", Text = "customer@example.com", Width = 160, Height = 32 };
            var passwordInput = new WpfPasswordBox { Password = "replay-secret", Width = 160, Height = 32 };
            var comboBox = new WpfComboBox { Width = 160, Height = 32, SelectedIndex = 1 };
            comboBox.Items.Add(new WpfComboBoxItem { Content = "Development" });
            comboBox.Items.Add(new WpfComboBoxItem { Content = "Staging" });
            var checkBox = new WpfCheckBox { Content = "Capture views", IsChecked = true, Width = 160, Height = 32 };
            var label = new WpfTextBlock { Text = "Public label", Width = 160, Height = 24 };
            var root = new WpfStackPanel { Children = { normalInput, sensitiveInput, passwordInput, comboBox, checkBox, label } };
            root.Measure(new System.Windows.Size(500, 400));
            root.Arrange(new System.Windows.Rect(0, 0, 500, 400));
            foreach (var control in root.Children.OfType<System.Windows.Controls.Control>())
            {
                control.ApplyTemplate();
            }
            root.UpdateLayout();

            var sensitiveOnly = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

            Assert.NotNull(sensitiveOnly);
            Assert.Equal("ordinary input", sensitiveOnly!.Children[0].Text);
            Assert.Equal("********", sensitiveOnly.Children[1].Text);
            Assert.DoesNotContain(Flatten(sensitiveOnly.Children[2]), node => node.Text?.Contains("replay-secret", StringComparison.Ordinal) == true);
            Assert.Contains(Flatten(sensitiveOnly.Children[3]), node => string.Equals(node.Text, "Staging", StringComparison.Ordinal));
            Assert.Contains(Flatten(sensitiveOnly.Children[4]), node => string.Equals(node.Text, "Capture views", StringComparison.Ordinal));
            Assert.Equal("Public label", sensitiveOnly.Children[5].Text);

            var allInputs = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAllInputs });

            Assert.NotNull(allInputs);
            Assert.Equal("********", allInputs!.Children[0].Text);
            Assert.Equal("********", allInputs.Children[1].Text);
            Assert.DoesNotContain(Flatten(allInputs.Children[3]), node => string.Equals(node.Text, "Staging", StringComparison.Ordinal));
            Assert.Contains(Flatten(allInputs.Children[4]), node => string.Equals(node.Text, "Capture views", StringComparison.Ordinal));
            Assert.Equal("Public label", allInputs.Children[5].Text);

            var allText = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAll });

            Assert.NotNull(allText);
            Assert.Equal("********", allText!.Children[5].Text);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WinFormsTreeMapper_AppliesTextAndInputPrivacyLevels()
    {
        await RunStaAsync(() =>
        {
            using var root = new WinForms.Panel { Width = 500, Height = 300 };
            var normalInput = new WinForms.TextBox { Text = "ordinary input", Width = 160 };
            var sensitiveInput = new WinForms.TextBox { Name = "EmailBox", Text = "customer@example.com", Width = 160 };
            var passwordInput = new WinForms.TextBox { Text = "replay-secret", UseSystemPasswordChar = true, Width = 160 };
            var comboBox = new WinForms.ComboBox { Text = "Enterprise", Width = 160 };
            var checkBox = new WinForms.CheckBox { Text = "Capture views", Checked = true, Width = 160 };
            var label = new WinForms.Label { Text = "Public label", Width = 160 };
            root.Controls.AddRange(new WinForms.Control[] { normalInput, sensitiveInput, passwordInput, comboBox, checkBox, label });

            var sensitiveOnly = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

            Assert.NotNull(sensitiveOnly);
            Assert.Equal("ordinary input", sensitiveOnly!.Children[0].Text);
            Assert.Equal("********", sensitiveOnly.Children[1].Text);
            Assert.Equal("********", sensitiveOnly.Children[2].Text);
            Assert.Equal("Enterprise", sensitiveOnly.Children[3].Text);
            Assert.Equal("Capture views", sensitiveOnly.Children[4].Text);
            Assert.Equal("Public label", sensitiveOnly.Children[5].Text);

            var allInputs = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAllInputs });

            Assert.NotNull(allInputs);
            Assert.Equal("********", allInputs!.Children[0].Text);
            Assert.Equal("********", allInputs.Children[1].Text);
            Assert.Equal("********", allInputs.Children[2].Text);
            Assert.Equal("********", allInputs.Children[3].Text);
            Assert.Equal("Capture views", allInputs.Children[4].Text);
            Assert.Equal("Public label", allInputs.Children[5].Text);

            var allText = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAll });

            Assert.NotNull(allText);
            Assert.Equal("********", allText!.Children[5].Text);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WpfTreeMapper_EncodesImagePixelsAsPngWireframe()
    {
        await RunStaAsync(() =>
        {
            var image = new WpfImage { Name = "ReplayImage", Source = CreateReplayBitmap(), Width = 64, Height = 64 };
            var root = new WpfStackPanel { Children = { image } };
            root.Measure(new System.Windows.Size(320, 240));
            root.Arrange(new System.Windows.Rect(0, 0, 320, 240));
            root.UpdateLayout();

            var mapped = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { ImagePrivacy = SessionReplayImagePrivacy.MaskNone });

            Assert.NotNull(mapped);
            var imageNode = Assert.Single(Flatten(mapped!), node => node.Attributes.GetValueOrDefault("data-control-type") == typeof(WpfImage).Name);
            Assert.Equal("img", imageNode.TagName);
            Assert.Equal("png", imageNode.ImageMimeType);
            Assert.False(imageNode.ImageIsEmpty);
            var png = Convert.FromBase64String(Assert.IsType<string>(imageNode.ImageBase64));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WpfTreeMapper_UsesEmptyImagePlaceholderWhenPngExceedsLimit()
    {
        await RunStaAsync(() =>
        {
            var image = new WpfImage { Name = "ReplayLargeImage", Source = CreateReplayBitmap(), Width = 64, Height = 64 };
            var root = new WpfStackPanel { Children = { image } };
            root.Measure(new System.Windows.Size(320, 240));
            root.Arrange(new System.Windows.Rect(0, 0, 320, 240));
            root.UpdateLayout();

            var mapped = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig
                {
                    ImagePrivacy = SessionReplayImagePrivacy.MaskNone,
                    MaxImageBytes = 8
                });

            Assert.NotNull(mapped);
            var imageNode = Assert.Single(Flatten(mapped!), node => node.Attributes.GetValueOrDefault("data-control-type") == typeof(WpfImage).Name);
            Assert.Equal("img", imageNode.TagName);
            Assert.True(imageNode.ImageIsEmpty);
            Assert.Null(imageNode.ImageBase64);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WpfTreeMapper_AppliesImagePrivacyLevels()
    {
        await RunStaAsync(() =>
        {
            var smallImage = new WpfImage { Name = "SmallReplayImage", Source = CreateReplayBitmap(), Width = 64, Height = 64 };
            var largeImage = new WpfImage { Name = "LargeReplayImage", Source = CreateReplayBitmap(), Width = 220, Height = 126 };
            var root = new WpfStackPanel { Children = { smallImage, largeImage } };
            root.Measure(new System.Windows.Size(500, 400));
            root.Arrange(new System.Windows.Rect(0, 0, 500, 400));
            root.UpdateLayout();

            var masked = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { ImagePrivacy = SessionReplayImagePrivacy.MaskAll });
            Assert.NotNull(masked);
            Assert.All(masked!.Children, node =>
            {
                Assert.True(node.ImageIsEmpty);
                Assert.Null(node.ImageBase64);
            });

            var largeOnly = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { ImagePrivacy = SessionReplayImagePrivacy.MaskLargeOnly });
            Assert.NotNull(largeOnly);
            Assert.False(largeOnly!.Children[0].ImageIsEmpty);
            Assert.NotNull(largeOnly.Children[0].ImageBase64);
            Assert.True(largeOnly.Children[1].ImageIsEmpty);
            Assert.Null(largeOnly.Children[1].ImageBase64);

            var unmasked = SessionReplayTreeMapper.Map(
                root,
                new SessionReplayPrivacyOverrides(),
                new RumSessionReplayConfig { ImagePrivacy = SessionReplayImagePrivacy.MaskNone });
            Assert.NotNull(unmasked);
            Assert.All(unmasked!.Children, node =>
            {
                Assert.False(node.ImageIsEmpty);
                Assert.NotNull(node.ImageBase64);
            });

            var overrides = new SessionReplayPrivacyOverrides();
            overrides.SetImagePrivacy(smallImage, SessionReplayImagePrivacy.MaskNone);
            var elementOverride = SessionReplayTreeMapper.Map(
                root,
                overrides,
                new RumSessionReplayConfig { ImagePrivacy = SessionReplayImagePrivacy.MaskAll });
            Assert.NotNull(elementOverride);
            Assert.False(elementOverride!.Children[0].ImageIsEmpty);
            Assert.NotNull(elementOverride.Children[0].ImageBase64);
            Assert.True(elementOverride.Children[1].ImageIsEmpty);
            Assert.Null(elementOverride.Children[1].ImageBase64);

            return Task.CompletedTask;
        });
    }

    [Fact]
    [Trait("Category", "Phase2")]
    public async Task WinFormsAutoInstrumentation_CapturesActionResizeAndReplay()
    {
        await RunStaAsync(async () =>
        {
            var rumQueue = new TestRumQueue();
            var replayQueue = new TestReplayQueue();
            await using var client = CreateClient(rumQueue, replayQueue);

            using var form = new WinForms.Form { Text = "WinFormsSmokeWindow", Width = 320, Height = 240 };
            var button = new WinForms.Button { Name = "SmokeButton", Text = "Save", Left = 10, Top = 10, Width = 120, TabIndex = 0 };
            var keyboardButton = new KeyboardWinFormsButton { Name = "KeyboardButton", Text = "Open", Left = 150, Top = 10, Width = 120, TabStop = false };
            var textBox = new WinForms.TextBox { Name = "SmokeInput", Text = "initial", Left = 10, Top = 50, Width = 180, TabIndex = 1 };
            var readOnlyTextBox = new WinForms.TextBox { Name = "ReadOnlyLog", Text = "ready", ReadOnly = true, Left = 10, Top = 80, Width = 180, TabStop = false };
            form.Controls.Add(button);
            form.Controls.Add(keyboardButton);
            form.Controls.Add(textBox);
            form.Controls.Add(readOnlyTextBox);

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
                    button.Focus();
                    PostMessage(button.Handle, 0x0100, (nint)WinForms.Keys.Tab, 0);
                    PostMessage(button.Handle, 0x0101, (nint)WinForms.Keys.Tab, 0);
                    WinForms.Application.DoEvents();
                    await Task.Delay(120);
                    button.PerformClick();
                    await Task.Delay(120);
                    keyboardButton.RaiseKeyDown(WinForms.Keys.Space);
                    keyboardButton.PerformClick();
                    keyboardButton.RaiseKeyUp(WinForms.Keys.Space);
                    await Task.Delay(120);
                    textBox.Focus();
                    textBox.Text = "secret-value";
                    await Task.Delay(120);
                    readOnlyTextBox.Text = "programmatic log update";
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

            Assert.Contains(rumQueue.Items, item =>
                item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                item.Line.Contains("action_name=SmokeButton", StringComparison.Ordinal) &&
                item.Line.Contains("action_type=click", StringComparison.Ordinal));
            Assert.Contains(rumQueue.Items, item =>
                item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                item.Line.Contains("action_name=KeyboardButton", StringComparison.Ordinal) &&
                item.Line.Contains("action_type=key", StringComparison.Ordinal));
            Assert.Contains(rumQueue.Items, item =>
                item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                item.Line.Contains("action_name=SmokeInput", StringComparison.Ordinal) &&
                item.Line.Contains("action_type=key", StringComparison.Ordinal));
            AssertOnlyExpectedAutomaticActionTypes(rumQueue.Items);
            var replayBodies = replayQueue.Items.Select(item => Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body))).ToArray();
            Assert.Contains(replayBodies, body => body.Contains("\"source\":2", StringComparison.Ordinal) && body.Contains("\"positions\":[", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":0", StringComparison.Ordinal) && body.Contains("\"event_type\":\"input\"", StringComparison.Ordinal));
            Assert.Contains(replayBodies, body => body.Contains("\"source\":4", StringComparison.Ordinal));
            Assert.DoesNotContain(replayBodies, body => body.Contains("secret-value", StringComparison.Ordinal));
            Assert.DoesNotContain(rumQueue.Items, item => item.Line.Contains("ReadOnlyLog", StringComparison.Ordinal));
            Assert.DoesNotContain(replayBodies, body => body.Contains("ReadOnlyLog", StringComparison.Ordinal));
        });
    }

    private static GuanceClient CreateClient(TestRumQueue rumQueue, TestReplayQueue replayQueue)
    {
        return new GuanceClient(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAllInputs,
                    FlushInterval = TimeSpan.FromMinutes(5)
                }
            },
            rumQueue,
            new RetryRumTransport(),
            replayQueue,
            new RetryReplayTransport());
    }

    private sealed class KeyboardWinFormsButton : WinForms.Button
    {
        public void RaiseKeyDown(WinForms.Keys key)
        {
            OnKeyDown(new WinForms.KeyEventArgs(key));
        }

        public void RaiseKeyUp(WinForms.Keys key)
        {
            OnKeyUp(new WinForms.KeyEventArgs(key));
        }
    }

    private static void AssertOnlyExpectedAutomaticActionTypes(IEnumerable<QueuedRumEvent> items)
    {
        var allowedTypeTags = new[]
        {
            ",action_type=click,",
            ",action_type=key,",
            ",action_type=launch_cold,",
            ",action_type=launch_hot,"
        };

        Assert.All(
            items.Where(item => item.Line.StartsWith("action,", StringComparison.Ordinal)),
            item => Assert.Contains(
                allowedTypeTags,
                typeTag => item.Line.Contains(typeTag, StringComparison.Ordinal)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

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

    private static IEnumerable<SessionReplayNode> Flatten(SessionReplayNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static System.Windows.Media.Imaging.WriteableBitmap CreateReplayBitmap()
    {
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        bitmap.WritePixels(
            new Int32Rect(0, 0, 2, 2),
            new byte[]
            {
                0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF,
                0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
            },
            8,
            0);
        return bitmap;
    }

    private static byte[] ExtractAndDecompressSegment(byte[] multipartBody)
    {
        var marker = Encoding.ASCII.GetBytes("Content-Type: application/octet-stream\r\n\r\n");
        var start = IndexOf(multipartBody, marker);
        Assert.True(start >= 0);
        start += marker.Length;
        var endMarker = Encoding.ASCII.GetBytes("\r\n--guance-rum-replay-");
        var end = IndexOf(multipartBody, endMarker, start);
        Assert.True(end > start);

        var compressedLength = end - start;
        Assert.True(compressedLength > 6);
        using var compressed = new MemoryStream(multipartBody, start + 2, compressedLength - 6);
        using var zlib = new DeflateStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start = 0)
    {
        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class TestRumQueue : IRumQueue
    {
        private long nextId;

        public List<QueuedRumEvent> Items { get; } = new();

        public Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
            return Task.FromResult(BatchAppendResult.Ready);
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

        public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(++nextId, contentType, body, DateTimeOffset.UtcNow, body.Length));
            return Task.FromResult(BatchAppendResult.Ready);
        }

        public Task<QueueBatch<QueuedSessionReplaySegment>?> AcquireAsync(CancellationToken cancellationToken) =>
            TestQueueLease.AcquireAsync(Items, item => item.Size, cancellationToken, maximumItems: 1);

        public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.CompleteAsync(Items, leaseId, cancellationToken);

        public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.AbandonAsync(Items, leaseId, cancellationToken);

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
