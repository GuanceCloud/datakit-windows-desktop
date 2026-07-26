#if WINDOWS
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WpfApplication = System.Windows.Application;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfRangeBase = System.Windows.Controls.Primitives.RangeBase;
using WpfSelector = System.Windows.Controls.Primitives.Selector;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using WinForms = System.Windows.Forms;

namespace Guance.Rum.Windows;

internal static partial class WindowsDesktopInstrumentation
{
    private static readonly ConditionalWeakTable<Window, AttachmentMarker> WpfWindowAttachments = new();
    private static readonly ConditionalWeakTable<Window, ReplaySnapshotMarker> WpfReplaySnapshotMarkers = new();
    private static readonly ConditionalWeakTable<WpfApplication, AttachmentMarker> WpfApplicationAttachments = new();
    private static readonly ConditionalWeakTable<WinForms.Form, AttachmentMarker> WinFormsFormAttachments = new();
    private static readonly ConditionalWeakTable<WinForms.Control, AttachmentMarker> WinFormsControlAttachments = new();
    private static readonly ConditionalWeakTable<WinForms.ToolStripItem, AttachmentMarker> WinFormsToolStripItemAttachments = new();
    private static readonly object ActiveClientGate = new();
    private static WeakReference<RumClient>? ActiveClient;
    private static bool WpfClassHandlersRegistered;
    private static bool WinFormsApplicationHandlersRegistered;

    static partial void TryAttachPlatform(RumClient client, AutomaticInstrumentationOptions options)
    {
        SetActiveClient(client);
        if (options.EnableWpf)
        {
            AttachWpf(client);
        }

        if (options.EnableWinForms)
        {
            AttachWinForms(client);
        }
    }

    static partial void DetachPlatform(RumClient client)
    {
        lock (ActiveClientGate)
        {
            if (ActiveClient is not null &&
                ActiveClient.TryGetTarget(out var active) &&
                ReferenceEquals(active, client))
            {
                ActiveClient = null;
            }
        }
    }

    private static void SetActiveClient(RumClient client)
    {
        lock (ActiveClientGate)
        {
            ActiveClient = new WeakReference<RumClient>(client);
        }
    }

    private static bool TryGetActiveClient(out RumClient client)
    {
        lock (ActiveClientGate)
        {
            if (ActiveClient is not null && ActiveClient.TryGetTarget(out client!))
            {
                return true;
            }
        }

        client = null!;
        return false;
    }

    private static void AttachWpf(RumClient client)
    {
        var app = WpfApplication.Current;
        if (app is null)
        {
            return;
        }

        if (WpfApplicationAttachments.TryGetValue(app, out _))
        {
            return;
        }

        WpfApplicationAttachments.Add(app, new AttachmentMarker());
        app.Activated += (_, _) =>
        {
            if (TryGetActiveClient(out var active))
            {
                AttachOpenWpfWindows(active, app);
            }
        };
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (TryGetActiveClient(out var active))
            {
                AttachOpenWpfWindows(active, app);
            }
        }));
        app.Deactivated += (_, _) =>
        {
            if (TryGetActiveClient(out var active))
            {
                active.StopView();
            }
        };

        if (WpfClassHandlersRegistered)
        {
            return;
        }

        WpfClassHandlersRegistered = true;

        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is not null &&
                    IsWebViewCandidate(sender) &&
                    TryGetActiveClient(out var active))
                {
                    active.AttachDiscoveredWebView(sender);
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfButtonBase),
            WpfButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (!TryGetActiveClient(out var active))
                {
                    return;
                }
                var element = sender as FrameworkElement;
                var name = WpfElementName(sender);
                active.AddAction(name, "click", TimeSpan.Zero);
                var ownerWindow = element is null ? app.MainWindow : Window.GetWindow(element) ?? app.MainWindow;
                if (ownerWindow is not null && element is not null)
                {
                    var point = Mouse.GetPosition(ownerWindow);
                    active.CaptureSessionReplayClick(element, name, point.X, point.Y);
                }
                QueueWpfReplaySnapshot(sender);
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfMenuItem),
            WpfMenuItem.ClickEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    TrackWpfAction(sender, "menu");
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfSelector),
            WpfSelector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    TrackWpfAction(sender, "selection");
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfRangeBase),
            WpfRangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    TrackWpfAction(sender, "value_change");
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfTextBoxBase),
            WpfTextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource) &&
                    sender is WpfTextBoxBase textBox &&
                    CanTrackWpfInput(textBox) &&
                    textBox.IsKeyboardFocusWithin &&
                    TryGetActiveClient(out var active))
                {
                    active.CaptureSessionReplayInput(WpfElementName(textBox));
                    QueueWpfReplaySnapshot(textBox);
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfTextBoxBase),
            UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource) && sender is WpfTextBoxBase textBox && CanTrackWpfInput(textBox))
                {
                    TrackWpfAction(textBox, "input", replayAsInput: true);
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfToggleButton),
            WpfToggleButton.CheckedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    TrackWpfAction(sender, "toggle");
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(WpfToggleButton),
            WpfToggleButton.UncheckedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    TrackWpfAction(sender, "toggle");
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource) && TryFormatWpfShortcut(sender, args, out var shortcutName))
                {
                    if (TryGetActiveClient(out var active))
                    {
                        active.AddAction(shortcutName, "shortcut", TimeSpan.Zero);
                        active.CaptureSessionReplayInteraction("shortcut", shortcutName);
                        QueueWpfReplaySnapshot(sender);
                    }
                }
            }),
            true);

        EventManager.RegisterClassHandler(
            typeof(UIElement),
            CommandManager.ExecutedEvent,
            new ExecutedRoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource))
                {
                    var commandName = WpfCommandActionName(sender, args.Command);
                    if (TryGetActiveClient(out var active))
                    {
                        active.AddAction(commandName, "command", TimeSpan.Zero);
                        active.CaptureSessionReplayInteraction("command", commandName);
                        QueueWpfReplaySnapshot(sender);
                    }
                }
            }),
            true);

    }

    private static bool CanTrackWpfInput(WpfTextBoxBase textBox)
    {
        return textBox.IsEnabled && !textBox.IsReadOnly;
    }

    private static void AttachOpenWpfWindows(RumClient client, WpfApplication app)
    {
        foreach (Window window in app.Windows)
        {
            AttachWpfWindow(client, window);
        }
    }

    private static void AttachWpfWindow(RumClient client, Window window)
    {
        if (WpfWindowAttachments.TryGetValue(window, out _))
        {
            return;
        }

        var attachment = new AttachmentMarker();
        WpfWindowAttachments.Add(window, attachment);
        window.Activated += (_, _) =>
        {
            if (TryGetActiveClient(out var active))
            {
                attachment.ViewStarted = true;
                active.StartView(window.TitleOrTypeName());
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (TryGetActiveClient(out var current))
                    {
                        current.CaptureSessionReplayFullSnapshot(window);
                    }
                }));
            }
        };
        window.SizeChanged += (_, args) =>
        {
            if (TryGetActiveClient(out var active))
            {
                active.CaptureSessionReplayResize(window.TitleOrTypeName(), args.NewSize.Width, args.NewSize.Height);
            }
        };
        window.Closed += (_, _) =>
        {
            attachment.ViewStarted = false;
            if (WpfReplaySnapshotMarkers.TryGetValue(window, out var marker))
            {
                marker.Timer?.Stop();
            }
            if (TryGetActiveClient(out var active))
            {
                active.StopView();
            }
        };
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            if (!attachment.ViewStarted && window.IsVisible && TryGetActiveClient(out var active))
            {
                attachment.ViewStarted = true;
                active.StartView(window.TitleOrTypeName());
                active.CaptureSessionReplayFullSnapshot(window);
            }
        }));
    }

    private static void TrackWpfAction(object sender, string actionType, bool replayAsInput = false)
    {
        if (!TryGetActiveClient(out var client))
        {
            return;
        }
        var name = WpfElementName(sender);
        client.AddAction(name, actionType, TimeSpan.Zero);
        if (replayAsInput)
        {
            client.CaptureSessionReplayInput(name);
        }
        else
        {
            client.CaptureSessionReplayInteraction(actionType, name);
        }

        QueueWpfReplaySnapshot(sender);
    }

    private static void QueueWpfReplaySnapshot(object sender)
    {
        if (sender is not DependencyObject dependencyObject)
        {
            return;
        }

        var window = Window.GetWindow(dependencyObject) ?? WpfApplication.Current?.MainWindow;
        if (window is null)
        {
            return;
        }

        var marker = WpfReplaySnapshotMarkers.GetValue(window, static _ => new ReplaySnapshotMarker());
        if (marker.Timer is null)
        {
            marker.Timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background,
                window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            marker.Timer.Tick += (_, _) =>
            {
                marker.Timer.Stop();
                if (window.IsVisible && TryGetActiveClient(out var active))
                {
                    active.CaptureSessionReplayFullSnapshot(window);
                }
            };
        }

        marker.Timer.Stop();
        marker.Timer.Start();
    }

    private static string WpfElementName(object sender)
    {
        return (sender as FrameworkElement).ElementNameOrTypeName();
    }

    private static string WpfCommandActionName(object sender, ICommand command)
    {
        var commandName = command is RoutedCommand routedCommand ? routedCommand.Name : command.ToString();
        var elementName = WpfElementName(sender);
        return string.IsNullOrWhiteSpace(commandName) ? elementName : $"{elementName}.{commandName}";
    }

    private static bool TryFormatWpfShortcut(object sender, System.Windows.Input.KeyEventArgs args, out string shortcutName)
    {
        shortcutName = string.Empty;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            return false;
        }

        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.None ||
            key == Key.LeftCtrl ||
            key == Key.RightCtrl ||
            key == Key.LeftShift ||
            key == Key.RightShift ||
            key == Key.LeftAlt ||
            key == Key.RightAlt ||
            key == Key.LWin ||
            key == Key.RWin)
        {
            return false;
        }

        shortcutName = $"{WpfElementName(sender)}.{modifiers}+{key}";
        return true;
    }

    private static void AttachWinForms(RumClient client)
    {
        if (WinFormsApplicationHandlersRegistered)
        {
            return;
        }

        WinFormsApplicationHandlersRegistered = true;
        WinForms.Application.ThreadException += (_, args) =>
        {
            if (TryGetActiveClient(out var active))
            {
                active.AddError(args.Exception);
            }
        };
        WinForms.Application.Idle += (_, _) =>
        {
            if (!TryGetActiveClient(out var active))
            {
                return;
            }
            foreach (var form in WinForms.Application.OpenForms.Cast<WinForms.Form>().ToArray())
            {
                AttachForm(active, form);
            }
        };
    }

    private static void AttachForm(RumClient client, WinForms.Form form)
    {
        if (!WinFormsFormAttachments.TryGetValue(form, out _))
        {
            WinFormsFormAttachments.Add(form, new AttachmentMarker());
            form.Activated += (_, _) =>
            {
                if (TryGetActiveClient(out var active))
                {
                    active.StartView(FormName(form));
                    active.CaptureSessionReplayFullSnapshot(form);
                }
            };
            form.Resize += (_, _) =>
            {
                if (TryGetActiveClient(out var active))
                {
                    active.CaptureSessionReplayResize(FormName(form), form.Width, form.Height);
                }
            };
            form.FormClosed += (_, _) =>
            {
                if (TryGetActiveClient(out var active))
                {
                    active.StopView();
                }
            };
            form.KeyDown += (_, args) => TrackWinFormsShortcut(client, form, args);
        }

        if (form.MainMenuStrip is not null)
        {
            AttachToolStrip(client, form.MainMenuStrip);
        }

        AttachControls(client, form.Controls);
    }

    private static void AttachControls(RumClient client, WinForms.Control.ControlCollection controls)
    {
        foreach (var control in controls.Cast<WinForms.Control>().ToArray())
        {
            AttachControl(client, control);

            if (control.Controls.Count > 0)
            {
                AttachControls(client, control.Controls);
            }
        }
    }

    private static void AttachControl(RumClient client, WinForms.Control control)
    {
        var isFirstAttachment = !WinFormsControlAttachments.TryGetValue(control, out _);
        if (isFirstAttachment)
        {
            WinFormsControlAttachments.Add(control, new AttachmentMarker());
            control.KeyDown += (_, args) => TrackWinFormsShortcut(client, control, args);
            if (IsWebViewCandidate(control))
            {
                client.AttachDiscoveredWebView(control);
            }
        }

        if (control.ContextMenuStrip is not null)
        {
            AttachToolStrip(client, control.ContextMenuStrip);
        }

        if (control is WinForms.ToolStrip toolStrip)
        {
            AttachToolStrip(client, toolStrip);
        }

        if (!isFirstAttachment)
        {
            return;
        }

        if (control is WinForms.CheckBox or WinForms.RadioButton)
        {
            control.Click += (_, _) => TrackWinFormsControlClick(client, control, "click");
            if (control is WinForms.CheckBox checkBox)
            {
                checkBox.CheckedChanged += (_, _) => TrackWinFormsAction(client, ControlName(checkBox), "toggle");
            }
            else if (control is WinForms.RadioButton radioButton)
            {
                radioButton.CheckedChanged += (_, _) => TrackWinFormsAction(client, ControlName(radioButton), "toggle");
            }
        }
        else if (control is WinForms.ButtonBase)
        {
            control.Click += (_, _) => TrackWinFormsControlClick(client, control, "click");
        }

        if (control is WinForms.TextBoxBase)
        {
            control.Enter += (_, _) => TrackWinFormsInput(client, control);
            control.TextChanged += (_, _) =>
            {
                if (CanTrackWinFormsInput(control) && TryGetActiveClient(out var active))
                {
                    active.CaptureSessionReplayInput(ControlName(control));
                }
            };
        }
        else if (control is WinForms.ComboBox comboBox)
        {
            comboBox.Enter += (_, _) => TrackWinFormsInput(client, comboBox);
            comboBox.SelectionChangeCommitted += (_, _) => TrackWinFormsAction(client, ControlName(comboBox), "selection");
        }
        else if (control is WinForms.NumericUpDown numericUpDown)
        {
            numericUpDown.ValueChanged += (_, _) => TrackWinFormsInput(client, numericUpDown);
        }
        else if (control is WinForms.TrackBar trackBar)
        {
            trackBar.ValueChanged += (_, _) => TrackWinFormsInput(client, trackBar);
        }
        else if (control is WinForms.DateTimePicker dateTimePicker)
        {
            dateTimePicker.ValueChanged += (_, _) => TrackWinFormsInput(client, dateTimePicker);
        }
        else if (control is WinForms.DataGridView grid)
        {
            grid.CellClick += (_, args) =>
            {
                var cellName = $"{ControlName(grid)}[{args.RowIndex},{args.ColumnIndex}]";
                TrackWinFormsAction(client, cellName, "grid_cell_click");
            };
            grid.CellValueChanged += (_, _) => TrackWinFormsInput(client, grid);
            grid.SelectionChanged += (_, _) => TrackWinFormsAction(client, ControlName(grid), "selection");
        }
    }

    private static void AttachToolStrip(RumClient client, WinForms.ToolStrip toolStrip)
    {
        AttachToolStripItems(client, toolStrip.Items);
    }

    private static void AttachToolStripItems(RumClient client, WinForms.ToolStripItemCollection items)
    {
        foreach (var item in items.Cast<WinForms.ToolStripItem>().ToArray())
        {
            AttachToolStripItem(client, item);
        }
    }

    private static void AttachToolStripItem(RumClient client, WinForms.ToolStripItem item)
    {
        if (WinFormsToolStripItemAttachments.TryGetValue(item, out _))
        {
            return;
        }

        WinFormsToolStripItemAttachments.Add(item, new AttachmentMarker());
        item.Click += (_, _) => TrackWinFormsAction(client, ToolStripItemName(item), "menu");

        if (item is WinForms.ToolStripDropDownItem dropDownItem)
        {
            dropDownItem.DropDownOpened += (_, _) => AttachToolStripItems(client, dropDownItem.DropDownItems);
            AttachToolStripItems(client, dropDownItem.DropDownItems);
        }
    }

    private static void TrackWinFormsControlClick(RumClient client, WinForms.Control control, string actionType)
    {
        if (!TryGetActiveClient(out client))
        {
            return;
        }
        var name = ControlName(control);
        client.AddAction(name, actionType, TimeSpan.Zero);
        var point = control.FindForm()?.PointToClient(WinForms.Control.MousePosition) ?? System.Drawing.Point.Empty;
        client.CaptureSessionReplayClick(control, name, point.X, point.Y);
    }

    private static void TrackWinFormsInput(RumClient client, WinForms.Control control)
    {
        if (!CanTrackWinFormsInput(control) || !TryGetActiveClient(out client))
        {
            return;
        }
        var name = ControlName(control);
        client.AddAction(name, "input", TimeSpan.Zero);
        client.CaptureSessionReplayInput(name);
    }

    private static bool CanTrackWinFormsInput(WinForms.Control control)
    {
        return control.Enabled &&
               control.ContainsFocus &&
               (control is not WinForms.TextBoxBase textBox || !textBox.ReadOnly);
    }

    private static void TrackWinFormsAction(RumClient client, string name, string actionType)
    {
        if (!TryGetActiveClient(out client))
        {
            return;
        }
        client.AddAction(name, actionType, TimeSpan.Zero);
        client.CaptureSessionReplayInteraction(actionType, name);
    }

    private static void TrackWinFormsShortcut(RumClient client, WinForms.Control control, WinForms.KeyEventArgs args)
    {
        if (!TryGetActiveClient(out client))
        {
            return;
        }
        if ((args.Modifiers & (WinForms.Keys.Control | WinForms.Keys.Alt | WinForms.Keys.Shift)) == WinForms.Keys.None ||
            args.KeyCode == WinForms.Keys.ControlKey ||
            args.KeyCode == WinForms.Keys.ShiftKey ||
            args.KeyCode == WinForms.Keys.Menu)
        {
            return;
        }

        var name = $"{ControlName(control)}.{args.Modifiers}+{args.KeyCode}";
        client.AddAction(name, "shortcut", TimeSpan.Zero);
        client.CaptureSessionReplayInteraction("shortcut", name);
    }

    private static string ControlName(WinForms.Control control)
    {
        if (!string.IsNullOrWhiteSpace(control.Name))
        {
            return control.Name;
        }

        return string.IsNullOrWhiteSpace(control.Text) ? control.GetType().Name : control.Text;
    }

    private static bool IsWebViewCandidate(object control)
    {
        return control.GetType().GetRuntimeProperty("CoreWebView2") is not null;
    }

    private static string FormName(WinForms.Form form)
    {
        return string.IsNullOrWhiteSpace(form.Text) ? form.GetType().Name : form.Text;
    }

    private static string ToolStripItemName(WinForms.ToolStripItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            return item.Name;
        }

        return string.IsNullOrWhiteSpace(item.Text) ? item.GetType().Name : item.Text;
    }

    private sealed class AttachmentMarker
    {
        public bool ViewStarted { get; set; }
    }

    private sealed class ReplaySnapshotMarker
    {
        public System.Windows.Threading.DispatcherTimer? Timer { get; set; }
    }
}

internal static class WindowsElementNames
{
    public static string TitleOrTypeName(this Window window)
    {
        return string.IsNullOrWhiteSpace(window.Title) ? window.GetType().Name : window.Title;
    }

    public static string ElementNameOrTypeName(this FrameworkElement? element)
    {
        return element is null || string.IsNullOrWhiteSpace(element.Name) ? element?.GetType().Name ?? "unknown" : element.Name;
    }
}
#endif
