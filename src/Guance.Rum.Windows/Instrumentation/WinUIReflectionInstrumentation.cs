using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Guance.Rum.Windows;

internal static class WinUIReflectionInstrumentation
{
    private static readonly ConditionalWeakTable<object, AttachmentMarker> WindowAttachments = new();
    private static readonly ConditionalWeakTable<object, AttachmentMarker> ElementAttachments = new();
    private static readonly object ActiveClientGate = new();
    private static WeakReference<RumClient>? ActiveClient;

    public static void Attach(RumClient client, object window, string? viewName)
    {
        SetActiveClient(client);
        var type = window.GetType();
        var name = string.IsNullOrWhiteSpace(viewName) ? ReadTitle(window) ?? type.Name : viewName;
        if (WindowAttachments.TryGetValue(window, out _))
        {
            AttachContentTree(client, window);
            return;
        }

        WindowAttachments.Add(window, new AttachmentMarker());
        var started = 0;
        var closed = 0;
        AddEventHandler(window, "Activated", () =>
        {
            if (Volatile.Read(ref closed) != 0 || Interlocked.Exchange(ref started, 1) != 0)
            {
                return;
            }

            if (TryGetActiveClient(out var active))
            {
                active.StartView(name);
                active.CaptureSessionReplayFullSnapshot(window);
                AttachContentTree(active, window);
            }
        });
        AddEventHandler(window, "SizeChanged", () =>
        {
            if (Volatile.Read(ref closed) != 0 || !TryGetActiveClient(out var active))
            {
                return;
            }
            var (width, height) = ReadSize(window);
            active.CaptureSessionReplayResize(name, width, height);
            AttachContentTree(active, window);
        });
        AddEventHandler(window, "Closed", () =>
        {
            if (Interlocked.Exchange(ref closed, 1) == 0 && TryGetActiveClient(out var active))
            {
                active.StopView();
            }
        });
        AttachContentTree(client, window);
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

    private static string? ReadTitle(object window)
    {
        return window.GetType().GetRuntimeProperty("Title")?.GetValue(window) as string;
    }

    private static void AttachContentTree(RumClient client, object window)
    {
        var content = window.GetType().GetRuntimeProperty("Content")?.GetValue(window);
        if (content is not null)
        {
            AttachElementTree(client, content, new HashSet<object>());
        }
    }

    private static void AttachElementTree(RumClient client, object element, ISet<object> visited)
    {
        if (!visited.Add(element))
        {
            return;
        }

        AttachElement(client, element);
        foreach (var child in EnumerateChildren(element))
        {
            AttachElementTree(client, child, visited);
        }
    }

    private static void AttachElement(RumClient client, object element)
    {
        if (ElementAttachments.TryGetValue(element, out _))
        {
            return;
        }

        ElementAttachments.Add(element, new AttachmentMarker());
        if (IsWebViewCandidate(element))
        {
            client.AttachDiscoveredWebView(element);
        }
        AddEventHandler(element, "Loaded", (_, _) =>
        {
            if (IsWebViewCandidate(element))
            {
                client.AttachDiscoveredWebView(element);
            }
            AttachElementTree(client, element, new HashSet<object>());
        });

        if (HasTypeName(element, "ButtonBase", "Button", "HyperlinkButton", "AppBarButton", "AppBarToggleButton", "CommandBarFlyoutCommandBar"))
        {
            AddEventHandler(element, "Click", (_, _) => TrackWinUIAction(client, element, "click"));
        }

        if (HasTypeName(element, "MenuFlyoutItem", "ToggleMenuFlyoutItem", "NavigationViewItem", "TreeViewItem"))
        {
            AddEventHandler(element, "Click", (_, _) => TrackWinUIAction(client, element, "menu"));
            AddEventHandler(element, "Invoked", (_, _) => TrackWinUIAction(client, element, "menu"));
        }

        if (HasTypeName(element, "TextBox", "RichEditBox"))
        {
            AddEventHandler(element, "GotFocus", (_, _) => TrackWinUIInputFocus(client, element));
            AddEventHandler(element, "TextChanged", (_, _) => CaptureWinUIInputChange(client, element));
        }

        if (HasTypeName(element, "PasswordBox"))
        {
            AddEventHandler(element, "GotFocus", (_, _) => TrackWinUIInputFocus(client, element));
            AddEventHandler(element, "PasswordChanged", (_, _) => CaptureWinUIInputChange(client, element));
        }

        if (HasTypeName(element, "Selector", "ComboBox", "ListBox", "ListViewBase", "ListView", "GridView", "NavigationView", "TreeView"))
        {
            AddEventHandler(element, "SelectionChanged", (_, _) => TrackWinUIAction(client, element, "selection"));
            AddEventHandler(element, "SelectionChanged", (_, _) => AttachElementTree(client, element, new HashSet<object>()));
            AddEventHandler(element, "ItemInvoked", (_, _) => TrackWinUIAction(client, element, "selection"));
        }

        if (HasTypeName(element, "ToggleButton", "CheckBox", "RadioButton", "AppBarToggleButton"))
        {
            AddEventHandler(element, "Checked", (_, _) => TrackWinUIAction(client, element, "toggle"));
            AddEventHandler(element, "Unchecked", (_, _) => TrackWinUIAction(client, element, "toggle"));
        }

        if (HasTypeName(element, "ToggleSwitch"))
        {
            AddEventHandler(element, "Toggled", (_, _) => TrackWinUIAction(client, element, "toggle"));
        }
    }

    private static IEnumerable<object> EnumerateChildren(object element)
    {
        foreach (var propertyName in new[] { "Content", "Child", "Header", "Footer", "PaneHeader", "PaneFooter", "TemplateRoot", "ContentTemplateRoot" })
        {
            var value = ReadProperty(element, propertyName);
            if (IsWinUICandidate(value))
            {
                yield return value!;
            }
        }

        foreach (var propertyName in new[] { "Children", "Items", "MenuItems", "PrimaryCommands", "SecondaryCommands", "FooterMenuItems" })
        {
            if (ReadProperty(element, propertyName) is not System.Collections.IEnumerable items)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (IsWinUICandidate(item))
                {
                    yield return item!;
                }
            }
        }
    }

    private static void TrackWinUIAction(RumClient client, object element, string actionType, bool replayAsInput = false)
    {
        if (!TryGetActiveClient(out client))
        {
            return;
        }
        var name = ElementName(element);
        client.AddAction(name, actionType, TimeSpan.Zero);
        if (replayAsInput)
        {
            client.CaptureSessionReplayInput(name);
            return;
        }

        client.CaptureSessionReplayInteraction(actionType, name);
    }

    private static void TrackWinUIInputFocus(RumClient client, object element)
    {
        if (CanTrackWinUIInput(element))
        {
            TrackWinUIAction(client, element, "input", replayAsInput: true);
        }
    }

    private static void CaptureWinUIInputChange(RumClient client, object element)
    {
        if (!CanTrackWinUIInput(element) || !IsWinUIInputFocused(element) || !TryGetActiveClient(out client))
        {
            return;
        }

        client.CaptureSessionReplayInput(ElementName(element));
    }

    private static bool CanTrackWinUIInput(object element)
    {
        return ReadProperty(element, "IsEnabled") is not bool enabled || enabled
            ? ReadProperty(element, "IsReadOnly") is not bool readOnly || !readOnly
            : false;
    }

    private static bool IsWinUIInputFocused(object element)
    {
        var focusState = ReadProperty(element, "FocusState")?.ToString();
        return !string.IsNullOrWhiteSpace(focusState) && !string.Equals(focusState, "Unfocused", StringComparison.OrdinalIgnoreCase);
    }

    private static string ElementName(object element)
    {
        return ReadString(element, "Name") ??
               ReadString(element, "Text") ??
               ReadString(element, "Header") ??
               ReadString(element, "PlaceholderText") ??
               ReadContentString(element) ??
               element.GetType().Name;
    }

    private static string? ReadContentString(object element)
    {
        var content = ReadProperty(element, "Content");
        return content is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static string? ReadString(object target, string propertyName)
    {
        var value = ReadProperty(target, propertyName);
        return value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static object? ReadProperty(object target, string propertyName)
    {
        return target.GetType().GetRuntimeProperty(propertyName)?.GetValue(target);
    }

    private static bool IsWinUICandidate(object? value)
    {
        if (value is null || value is string)
        {
            return false;
        }

        var type = value.GetType();
        if (type.GetRuntimeProperty("CoreWebView2") is not null)
        {
            return true;
        }
        while (type is not null)
        {
            if (type.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) == true)
            {
                return true;
            }

            type = type.BaseType;
        }

        return false;
    }

    private static bool IsWebViewCandidate(object element)
    {
        return element.GetType().GetRuntimeProperty("CoreWebView2") is not null;
    }

    private static bool HasTypeName(object target, params string[] typeNames)
    {
        var type = target.GetType();
        while (type is not null)
        {
            if (typeNames.Contains(type.Name, StringComparer.Ordinal))
            {
                return true;
            }

            type = type.BaseType;
        }

        return false;
    }

    private static (double Width, double Height) ReadSize(object window)
    {
        var content = window.GetType().GetRuntimeProperty("Content")?.GetValue(window);
        var width = ReadDouble(content, "ActualWidth") ?? ReadDouble(window, "Width") ?? ReadDouble(window, "Bounds.Width") ?? 0;
        var height = ReadDouble(content, "ActualHeight") ?? ReadDouble(window, "Height") ?? ReadDouble(window, "Bounds.Height") ?? 0;
        return (width, height);
    }

    private static double? ReadDouble(object? target, string propertyPath)
    {
        if (target is null)
        {
            return null;
        }

        object? current = target;
        foreach (var part in propertyPath.Split('.'))
        {
            current = current?.GetType().GetRuntimeProperty(part)?.GetValue(current);
            if (current is null)
            {
                return null;
            }
        }

        return current switch
        {
            double d => d,
            float f => f,
            int i => i,
            decimal m => (double)m,
            _ => null
        };
    }

    private static void AddEventHandler(object target, string eventName, Action action)
    {
        AddEventHandler(target, eventName, (_, _) => action());
    }

    private static void AddEventHandler(object target, string eventName, Action<object?, object?> action)
    {
        var eventInfo = target.GetType().GetRuntimeEvent(eventName);
        var handlerType = eventInfo?.EventHandlerType;
        var invoke = handlerType?.GetRuntimeMethods().FirstOrDefault(method => method.Name == "Invoke");
        if (eventInfo is null || handlerType is null || invoke is null)
        {
            return;
        }

        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();

        Expression sender = parameters.Length > 0 ? Expression.Convert(parameters[0], typeof(object)) : Expression.Constant(null, typeof(object));
        Expression args = parameters.Length > 1 ? Expression.Convert(parameters[1], typeof(object)) : Expression.Constant(null, typeof(object));
        var body = Expression.Call(
            Expression.Constant(action),
            typeof(Action<object?, object?>).GetRuntimeMethod(nameof(Action.Invoke), new[] { typeof(object), typeof(object) })!,
            sender,
            args);
        var lambda = Expression.Lambda(handlerType, body, parameters);
        eventInfo.AddEventHandler(target, lambda.Compile());
    }

    private sealed class AttachmentMarker
    {
    }
}
