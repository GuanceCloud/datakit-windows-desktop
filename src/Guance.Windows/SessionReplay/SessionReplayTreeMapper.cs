using System.Reflection;
using System.Text.RegularExpressions;

#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
#endif

namespace Guance.Windows.SessionReplay;

internal static class SessionReplayTreeMapper
{
    public static SessionReplayNode? Map(object root, SessionReplayPrivacyOverrides overrides, RumSessionReplayConfig config)
    {
        var inherited = SessionReplayPrivacyOverrides.FromConfig(config);
        var budget = new MapBudget(config);
#if WINDOWS
        if (root is DependencyObject dependencyObject)
        {
            return MapWpf(dependencyObject, dependencyObject, overrides, config, inherited, budget, depth: 0);
        }

        if (root is WinForms.Control control)
        {
            return MapWinForms(control, control, overrides, config, inherited, budget, depth: 0);
        }
#endif
        return MapByReflection(root, root, overrides, config, inherited, budget, depth: 0);
    }

#if WINDOWS
    private static SessionReplayNode? MapWpf(DependencyObject root, DependencyObject element, SessionReplayPrivacyOverrides overrides, RumSessionReplayConfig config, ResolvedPrivacy inherited, MapBudget budget, int depth)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return null;
        }

        var privacy = overrides.Resolve(element, inherited, config);
        if (frameworkElement.Visibility != Visibility.Visible)
        {
            return null;
        }

        var point = frameworkElement == root
            ? new System.Windows.Point(0, 0)
            : frameworkElement.TranslatePoint(new System.Windows.Point(0, 0), (UIElement)root);
        if (!budget.TryEnter(depth))
        {
            return budget.TryEmitTruncation() ? TruncatedNode(point.X, point.Y, frameworkElement.ActualWidth, frameworkElement.ActualHeight) : null;
        }

        var node = new SessionReplayNode(TagFor(frameworkElement), point.X, point.Y, frameworkElement.ActualWidth, frameworkElement.ActualHeight)
        {
            Hidden = privacy.Hidden,
            Text = LimitText(TextForWpf(frameworkElement, privacy, config), config)
        };
        node.Attributes["data-control-type"] = frameworkElement.GetType().Name;
        ApplyWpfVisualStyle(frameworkElement, node);
        if (frameworkElement is System.Windows.Controls.Image image)
        {
            ApplyWpfImage(image, node, privacy, config);
        }
        var isWebView = IsWebView(frameworkElement);
        var customRendered = IsCustomRendered(frameworkElement.GetType().Name, config);
        if (isWebView)
        {
            MarkWebView(node, frameworkElement);
        }
        else if (customRendered)
        {
            MarkCustomRendered(node, frameworkElement.GetType().Name);
        }
        if (!frameworkElement.IsEnabled)
        {
            node.Attributes["aria-disabled"] = "true";
        }
        ApplyNodeLimits(node, config);

        if (node.Hidden || customRendered)
        {
            return node;
        }

        foreach (var child in WpfChildren(element))
        {
            var childNode = MapWpf(root, child, overrides, config, privacy, budget, depth + 1);
            if (childNode is not null)
            {
                node.Children.Add(childNode);
            }
        }

        RedactWpfMaskedValueDescendants(frameworkElement, node, privacy, config);

        if (frameworkElement is ContentControl &&
            !string.IsNullOrEmpty(node.Text) &&
            HasDescendantWithText(node, node.Text))
        {
            node.Text = null;
        }

        if (frameworkElement is System.Windows.Controls.Control && HasDescendantWithSameVisualStyle(node))
        {
            node.BackgroundColor = null;
            node.BorderColor = null;
            node.BorderWidth = 0;
            node.CornerRadius = 0;
        }

        return node;
    }

    private static void ApplyWpfVisualStyle(FrameworkElement element, SessionReplayNode node)
    {
        node.Opacity = Math.Clamp(element.Opacity, 0, 1);

        switch (element)
        {
            case System.Windows.Controls.Control control:
                node.BackgroundColor = BrushToReplayColor(control.Background);
                node.BorderColor = BrushToReplayColor(control.BorderBrush);
                node.BorderWidth = MaxThickness(control.BorderThickness);
                node.FontFamily = control.FontFamily?.Source;
                node.FontSize = control.FontSize;
                node.TextColor = BrushToReplayColor(control.Foreground);
                node.TextHorizontalAlignment = ToReplayHorizontalAlignment(control.HorizontalContentAlignment);
                node.TextVerticalAlignment = ToReplayVerticalAlignment(control.VerticalContentAlignment);
                ApplyPadding(node, control.Padding);
                break;
            case Border border:
                node.BackgroundColor = BrushToReplayColor(border.Background);
                node.BorderColor = BrushToReplayColor(border.BorderBrush);
                node.BorderWidth = MaxThickness(border.BorderThickness);
                node.CornerRadius = Math.Max(0, Math.Max(Math.Max(border.CornerRadius.TopLeft, border.CornerRadius.TopRight), Math.Max(border.CornerRadius.BottomRight, border.CornerRadius.BottomLeft)));
                ApplyPadding(node, border.Padding);
                break;
            case System.Windows.Controls.Panel panel:
                node.BackgroundColor = BrushToReplayColor(panel.Background);
                break;
            case TextBlock textBlock:
                node.BackgroundColor = BrushToReplayColor(textBlock.Background);
                node.FontFamily = textBlock.FontFamily?.Source;
                node.FontSize = textBlock.FontSize;
                node.TextColor = BrushToReplayColor(textBlock.Foreground);
                node.TextHorizontalAlignment = ToReplayHorizontalAlignment(textBlock.TextAlignment);
                node.TextVerticalAlignment = "center";
                ApplyPadding(node, textBlock.Padding);
                break;
            case System.Windows.Shapes.Shape shape:
                node.BackgroundColor = BrushToReplayColor(shape.Fill);
                node.BorderColor = BrushToReplayColor(shape.Stroke);
                node.BorderWidth = Math.Max(0, shape.StrokeThickness);
                break;
        }
    }

    private static void ApplyPadding(SessionReplayNode node, Thickness padding)
    {
        node.PaddingTop = Math.Max(0, padding.Top);
        node.PaddingRight = Math.Max(0, padding.Right);
        node.PaddingBottom = Math.Max(0, padding.Bottom);
        node.PaddingLeft = Math.Max(0, padding.Left);
    }

    private static void ApplyWpfImage(System.Windows.Controls.Image image, SessionReplayNode node, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        node.ImageMimeType = "png";
        if (!config.CaptureImages ||
            privacy.ImagePrivacy == SessionReplayImagePrivacy.MaskAll ||
            privacy.ImagePrivacy == SessionReplayImagePrivacy.MaskLargeOnly && IsLargeWpfImage(image, config) ||
            image.Source is null)
        {
            node.ImageIsEmpty = true;
            return;
        }

        try
        {
            var bitmap = image.Source as System.Windows.Media.Imaging.BitmapSource ?? RenderImageSource(image.Source, image, config.MaxImageDimension);
            bitmap = ScaleBitmap(bitmap, config.MaxImageDimension);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            if (stream.Length > config.MaxImageBytes)
            {
                node.ImageIsEmpty = true;
                return;
            }

            node.ImageBase64 = Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            node.ImageIsEmpty = true;
        }
    }

    private static bool IsLargeWpfImage(System.Windows.Controls.Image image, RumSessionReplayConfig config)
    {
        var width = PositiveDimension(image.ActualWidth, image.Width, image.Source?.Width);
        var height = PositiveDimension(image.ActualHeight, image.Height, image.Source?.Height);
        return width > config.LargeImagePrivacyThreshold && height > config.LargeImagePrivacyThreshold;
    }

    private static double PositiveDimension(double? first, double? second, double? third)
    {
        return ValidDimension(first) ?? ValidDimension(second) ?? ValidDimension(third) ?? 0;
    }

    private static double? ValidDimension(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static System.Windows.Media.Imaging.BitmapSource RenderImageSource(System.Windows.Media.ImageSource source, FrameworkElement image, int maxDimension)
    {
        var sourceWidth = double.IsFinite(source.Width) && source.Width > 0 ? source.Width : image.ActualWidth;
        var sourceHeight = double.IsFinite(source.Height) && source.Height > 0 ? source.Height : image.ActualHeight;
        var width = Math.Clamp((int)Math.Ceiling(sourceWidth), 1, maxDimension);
        var height = Math.Clamp((int)Math.Ceiling(sourceHeight), 1, maxDimension);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.Windows.Media.Imaging.BitmapSource ScaleBitmap(System.Windows.Media.Imaging.BitmapSource bitmap, int maxDimension)
    {
        var largestDimension = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (largestDimension <= maxDimension)
        {
            return bitmap;
        }

        var scale = maxDimension / (double)largestDimension;
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static double MaxThickness(Thickness thickness)
    {
        return Math.Max(0, Math.Max(Math.Max(thickness.Left, thickness.Top), Math.Max(thickness.Right, thickness.Bottom)));
    }

    private static string? BrushToReplayColor(System.Windows.Media.Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return null;
        }

        var color = solid.Color;
        var alpha = (byte)Math.Round(color.A * Math.Clamp(solid.Opacity, 0, 1), MidpointRounding.AwayFromZero);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}{alpha:X2}";
    }

    private static string ToReplayHorizontalAlignment(System.Windows.HorizontalAlignment alignment) => alignment switch
    {
        System.Windows.HorizontalAlignment.Center => "center",
        System.Windows.HorizontalAlignment.Right => "right",
        _ => "left"
    };

    private static string ToReplayHorizontalAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "center",
        TextAlignment.Right => "right",
        _ => "left"
    };

    private static string ToReplayVerticalAlignment(System.Windows.VerticalAlignment alignment) => alignment switch
    {
        System.Windows.VerticalAlignment.Center or System.Windows.VerticalAlignment.Stretch => "center",
        System.Windows.VerticalAlignment.Bottom => "bottom",
        _ => "top"
    };

    private static bool HasDescendantWithText(SessionReplayNode node, string text)
    {
        foreach (var child in node.Children)
        {
            if (string.Equals(child.Text, text, StringComparison.Ordinal) || HasDescendantWithText(child, text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDescendantWithSameVisualStyle(SessionReplayNode node)
    {
        if (string.IsNullOrWhiteSpace(node.BackgroundColor) &&
            (string.IsNullOrWhiteSpace(node.BorderColor) || node.BorderWidth <= 0))
        {
            return false;
        }

        return HasDescendantWithSameVisualStyle(node, node.Children);
    }

    private static bool HasDescendantWithSameVisualStyle(SessionReplayNode target, IEnumerable<SessionReplayNode> descendants)
    {
        foreach (var child in descendants)
        {
            if (SameBounds(target, child) &&
                string.Equals(target.BackgroundColor, child.BackgroundColor, StringComparison.Ordinal) &&
                string.Equals(target.BorderColor, child.BorderColor, StringComparison.Ordinal) &&
                Math.Abs(target.BorderWidth - child.BorderWidth) < 0.01 &&
                Math.Abs(target.CornerRadius - child.CornerRadius) < 0.01)
            {
                return true;
            }

            if (HasDescendantWithSameVisualStyle(target, child.Children))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameBounds(SessionReplayNode left, SessionReplayNode right)
    {
        return Math.Abs(left.X - right.X) < 0.5 &&
               Math.Abs(left.Y - right.Y) < 0.5 &&
               Math.Abs(left.Width - right.Width) < 0.5 &&
               Math.Abs(left.Height - right.Height) < 0.5;
    }

    private static IEnumerable<DependencyObject> WpfChildren(DependencyObject element)
    {
        var visualCount = VisualTreeHelper.GetChildrenCount(element);
        if (visualCount > 0)
        {
            for (var i = 0; i < visualCount; i++)
            {
                yield return VisualTreeHelper.GetChild(element, i);
            }
            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            yield return child;
        }
    }

    private static SessionReplayNode? MapWinForms(WinForms.Control root, WinForms.Control control, SessionReplayPrivacyOverrides overrides, RumSessionReplayConfig config, ResolvedPrivacy inherited, MapBudget budget, int depth)
    {
        var privacy = overrides.Resolve(control, inherited, config);
        var location = control == root ? new System.Drawing.Point(0, 0) : root.PointToClient(control.PointToScreen(System.Drawing.Point.Empty));
        if (!budget.TryEnter(depth))
        {
            return budget.TryEmitTruncation() ? TruncatedNode(location.X, location.Y, control.Width, control.Height) : null;
        }

        var node = new SessionReplayNode(TagFor(control), location.X, location.Y, control.Width, control.Height)
        {
            Hidden = privacy.Hidden,
            Text = LimitText(TextForWinForms(control, privacy, config), config)
        };
        node.Attributes["data-control-type"] = control.GetType().Name;
        var isWebView = IsWebView(control);
        var customRendered = IsCustomRendered(control.GetType().Name, config);
        if (isWebView)
        {
            MarkWebView(node, control);
        }
        else if (customRendered)
        {
            MarkCustomRendered(node, control.GetType().Name);
        }
        if (!control.Enabled)
        {
            node.Attributes["aria-disabled"] = "true";
        }
        ApplyNodeLimits(node, config);

        if (node.Hidden || customRendered)
        {
            return node;
        }

        foreach (WinForms.Control child in control.Controls)
        {
            if (child.Visible)
            {
                var childNode = MapWinForms(root, child, overrides, config, privacy, budget, depth + 1);
                if (childNode is not null)
                {
                    node.Children.Add(childNode);
                }
            }
        }

        return node;
    }

    private static string? TextForWpf(FrameworkElement element, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        var text = ReadWpfText(element);
        return ShouldMaskWpfText(element, text, privacy, config) ? MaskText(text) : text;
    }

    private static bool ShouldMaskWpfText(FrameworkElement element, string? text, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.Allow || string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAll)
        {
            return true;
        }

        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAllInputs &&
            element is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or Selector)
        {
            return true;
        }

        if (element is PasswordBox)
        {
            return true;
        }

        return ShouldMaskSensitiveText(element.GetType().Name, element.Name, text, privacy, config);
    }

    private static void RedactWpfMaskedValueDescendants(FrameworkElement element, SessionReplayNode node, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        var value = element switch
        {
            System.Windows.Controls.TextBox textBox => textBox.Text,
            Selector selector => SelectedWpfText(selector),
            _ => ReadWpfText(element)
        };
        if (!ShouldMaskWpfText(element, value, privacy, config))
        {
            return;
        }

        RedactDescendantText(node.Children, value!);
    }

    private static string? SelectedWpfText(Selector selector)
    {
        return selector.SelectedItem switch
        {
            string text => text,
            ContentControl content when content.Content is string text => text,
            _ => null
        };
    }

    private static string? ReadWpfText(FrameworkElement element)
    {
        return element switch
        {
            TextBlock textBlock => textBlock.Text,
            System.Windows.Controls.TextBox textBox => textBox.Text,
            PasswordBox => string.Empty,
            ContentControl content when content.Content is string text => text,
            HeaderedContentControl header when header.Header is string text => text,
            _ => null
        };
    }

    private static string? TextForWinForms(WinForms.Control control, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAll ||
            (privacy.TextAndInputPrivacy != SessionReplayTextAndInputPrivacy.Allow && IsWinFormsPasswordInput(control)) ||
            ShouldMaskSensitiveText(control.GetType().Name, control.Name, control.Text, privacy, config))
        {
            return MaskText(control.Text);
        }

        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAllInputs &&
            control is WinForms.TextBoxBase or WinForms.ComboBox)
        {
            return MaskText(control.Text);
        }

        return control.Text;
    }

    private static bool IsWinFormsPasswordInput(WinForms.Control control)
    {
        return control is WinForms.TextBox textBox &&
               (textBox.UseSystemPasswordChar || textBox.PasswordChar != '\0');
    }

    private static string TagFor(FrameworkElement element)
    {
        return element switch
        {
            System.Windows.Controls.Primitives.TextBoxBase or PasswordBox => "input",
            System.Windows.Controls.Primitives.ButtonBase => "button",
            System.Windows.Controls.Image => "img",
            TextBlock => "span",
            Selector => "select",
            _ => "div"
        };
    }

    private static string TagFor(WinForms.Control control)
    {
        return control switch
        {
            WinForms.TextBoxBase or WinForms.ComboBox => "input",
            WinForms.ButtonBase => "button",
            WinForms.Label => "span",
            _ => "div"
        };
    }
#endif

    private static SessionReplayNode? MapByReflection(object root, object element, SessionReplayPrivacyOverrides overrides, RumSessionReplayConfig config, ResolvedPrivacy inherited, MapBudget budget, int depth)
    {
        var privacy = overrides.Resolve(element, inherited, config);
        if (!IsReflectionElementVisible(element))
        {
            return null;
        }

        var bounds = ReadBoundsRelativeToRoot(root, element);
        var width = bounds?.Width ?? ReadDouble(element, "ActualWidth") ?? ReadDouble(element, "Width") ?? ReadDouble(element, "RenderSize.Width") ?? ReadDouble(element, "DesiredSize.Width") ?? 0;
        var height = bounds?.Height ?? ReadDouble(element, "ActualHeight") ?? ReadDouble(element, "Height") ?? ReadDouble(element, "RenderSize.Height") ?? ReadDouble(element, "DesiredSize.Height") ?? 0;
        var x = bounds?.X ?? ReadDouble(element, "X") ?? ReadDouble(element, "Left") ?? ReadDouble(element, "Canvas.Left") ?? ReadDouble(element, "Margin.Left") ?? 0;
        var y = bounds?.Y ?? ReadDouble(element, "Y") ?? ReadDouble(element, "Top") ?? ReadDouble(element, "Canvas.Top") ?? ReadDouble(element, "Margin.Top") ?? 0;
        if (!budget.TryEnter(depth))
        {
            return budget.TryEmitTruncation() ? TruncatedNode(x, y, width, height) : null;
        }

        var node = new SessionReplayNode(TagFor(element), x, y, width, height)
        {
            Hidden = privacy.Hidden,
            Text = LimitText(TextForReflection(element, privacy, config), config)
        };
        node.Attributes["data-control-type"] = element.GetType().Name;
        var isWebView = IsWebView(element);
        var customRendered = IsCustomRendered(element.GetType().Name, config);
        if (isWebView)
        {
            MarkWebView(node, element);
        }
        else if (customRendered)
        {
            MarkCustomRendered(node, element.GetType().Name);
        }
        AddReflectionAttributes(element, node, privacy);
        ApplyNodeLimits(node, config);

        if (node.Hidden || customRendered)
        {
            return node;
        }

        foreach (var child in ReflectChildren(element))
        {
            var childNode = MapByReflection(root, child, overrides, config, privacy, budget, depth + 1);
            if (childNode is not null)
            {
                node.Children.Add(childNode);
            }
        }

        RedactReflectionMaskedValueDescendants(element, node, privacy, config);

        return node;
    }

    private static IEnumerable<object> ReflectChildren(object element)
    {
        foreach (var name in new[] { "Children", "Items", "MenuItems", "Flyout.Items", "ContentTemplateRoot" })
        {
            var value = ReadPropertyPath(element, name);
            if (value is string)
            {
                continue;
            }

            if (value is not System.Collections.IEnumerable enumerable)
            {
                if (value is not null)
                {
                    yield return value;
                }
                continue;
            }

            foreach (var child in enumerable)
            {
                if (child is not null)
                {
                    yield return child;
                }
            }
        }

        foreach (var propertyName in new[] { "Content", "Header", "Footer", "PaneHeader", "PaneFooter", "SelectionBoxItem" })
        {
            var content = element.GetType().GetRuntimeProperty(propertyName)?.GetValue(element);
            if (content is not null && content is not string)
            {
                yield return content;
            }
        }

        foreach (var child in ReflectVisualChildren(element))
        {
            yield return child;
        }
    }

    private static IEnumerable<object> ReflectVisualChildren(object element)
    {
        var type = element.GetType();
        var countProperty = type.GetRuntimeProperty("VisualChildrenCount") ?? type.GetRuntimeProperty("ChildrenCount");
        var countValue = countProperty?.GetValue(element);
        var count = countValue switch
        {
            int i => i,
            _ => 0
        };
        if (count <= 0)
        {
            yield break;
        }

        var getChild = type.GetRuntimeMethods()
            .FirstOrDefault(method => (method.Name is "GetVisualChild" or "GetChild") && method.GetParameters().Length == 1);
        if (getChild is null)
        {
            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            object? child = null;
            try
            {
                child = getChild.Invoke(element, new object[] { i });
            }
            catch (TargetInvocationException)
            {
            }

            if (child is not null)
            {
                yield return child;
            }
        }
    }

    private static bool IsReflectionElementVisible(object element)
    {
        if (ReadBool(element, "IsVisible") == false || ReadDouble(element, "Opacity") == 0)
        {
            return false;
        }

        var visibility = element.GetType().GetRuntimeProperty("Visibility")?.GetValue(element);
        return visibility is null || string.Equals(visibility.ToString(), "Visible", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddReflectionAttributes(object element, SessionReplayNode node, ResolvedPrivacy privacy)
    {
        var name = ReadString(element, "Name") ?? ReadString(element, "AutomationId") ?? ReadString(element, "Uid");
        if (!string.IsNullOrWhiteSpace(name))
        {
            node.Attributes["data-control-name"] = name!;
        }

        var placeholder = ReadString(element, "PlaceholderText") ?? ReadString(element, "Placeholder");
        if (!string.IsNullOrWhiteSpace(placeholder))
        {
            node.Attributes["placeholder"] = placeholder!;
        }

        if (ReadBool(element, "IsEnabled") == false)
        {
            node.Attributes["aria-disabled"] = "true";
        }

        var masksInputState = privacy.TextAndInputPrivacy is SessionReplayTextAndInputPrivacy.MaskAllInputs or SessionReplayTextAndInputPrivacy.MaskAll;

        var isChecked = ReadBool(element, "IsChecked");
        if (!masksInputState && isChecked is not null)
        {
            node.Attributes["aria-checked"] = isChecked.Value ? "true" : "false";
        }

        var isSelected = ReadBool(element, "IsSelected");
        if (!masksInputState && isSelected is not null)
        {
            node.Attributes["aria-selected"] = isSelected.Value ? "true" : "false";
        }

        var selectedIndex = ReadInt(element, "SelectedIndex");
        if (!masksInputState && selectedIndex is not null && selectedIndex >= 0)
        {
            node.Attributes["data-selected-index"] = selectedIndex.Value.ToString();
        }

        var isOn = ReadBool(element, "IsOn");
        if (!masksInputState && isOn is not null)
        {
            node.Attributes["aria-checked"] = isOn.Value ? "true" : "false";
        }

        var selectionMode = ReadPropertyPath(element, "SelectionMode")?.ToString();
        if (!string.IsNullOrWhiteSpace(selectionMode))
        {
            node.Attributes["aria-multiselectable"] = selectionMode.Contains("Multiple", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        }

        var icon = ReadPropertyPath(element, "Icon")?.GetType().Name;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            node.Attributes["data-icon-type"] = icon;
        }

        var source = ReadString(element, "Source", "NavigateUri");
        if (!string.IsNullOrWhiteSpace(source))
        {
            node.Attributes["src"] = source!;
        }

        var value = ReadDouble(element, "Value");
        if (!masksInputState && value is not null)
        {
            node.Attributes["aria-valuenow"] = FormattableString.Invariant($"{value:0.##}");
        }

        var minimum = ReadDouble(element, "Minimum");
        if (minimum is not null)
        {
            node.Attributes["aria-valuemin"] = FormattableString.Invariant($"{minimum:0.##}");
        }

        var maximum = ReadDouble(element, "Maximum");
        if (maximum is not null)
        {
            node.Attributes["aria-valuemax"] = FormattableString.Invariant($"{maximum:0.##}");
        }
    }

    private static object? ReadPropertyPath(object element, string propertyPath)
    {
        object? current = element;
        foreach (var part in propertyPath.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            current = current.GetType().GetRuntimeProperty(part)?.GetValue(current);
        }

        return current;
    }

    private static string? ReadString(object element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadPropertyPath(element, propertyName);
            if (value is string text)
            {
                return text;
            }
        }

        return null;
    }

    private static bool? ReadBool(object element, string propertyName)
    {
        var value = ReadPropertyPath(element, propertyName);
        return value switch
        {
            bool b => b,
            _ => null
        };
    }

    private static int? ReadInt(object element, string propertyName)
    {
        var value = ReadPropertyPath(element, propertyName);
        return value switch
        {
            int i => i,
            _ => null
        };
    }

    private static string? TextForReflection(object element, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        var text = ReadReflectionText(element);

        if (text is null)
        {
            return null;
        }

        return ShouldMaskReflectionText(element, text, privacy, config) ? MaskText(text) : text;
    }

    private static string? ReadReflectionText(object element)
    {
        var text = ReadString(element, "Text", "Title", "Header", "Label");
        var content = element.GetType().GetRuntimeProperty("Content")?.GetValue(element);
        if (text is null && content is string contentText)
        {
            text = contentText;
        }

        if (text is null)
        {
            text = element.GetType().GetRuntimeProperty("SelectedItem")?.GetValue(element) as string;
        }

        return text;
    }

    private static bool ShouldMaskReflectionText(object element, string text, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.Allow)
        {
            return false;
        }

        var typeName = element.GetType().Name;
        return privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAll ||
               IsReflectionSecret(typeName) ||
               ShouldMaskSensitiveText(typeName, ReadString(element, "Name", "AutomationId", "Uid"), text, privacy, config) ||
               privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAllInputs &&
               (IsReflectionInput(typeName) || IsReflectionSelectionValue(typeName));
    }

    private static void RedactReflectionMaskedValueDescendants(object element, SessionReplayNode node, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        var value = ReadReflectionText(element);
        if (string.IsNullOrEmpty(value) || !ShouldMaskReflectionText(element, value, privacy, config))
        {
            return;
        }

        RedactDescendantText(node.Children, value);
    }

    private static (double X, double Y, double Width, double Height)? ReadBoundsRelativeToRoot(object root, object element)
    {
        var width = ReadDouble(element, "ActualWidth") ?? ReadDouble(element, "RenderSize.Width");
        var height = ReadDouble(element, "ActualHeight") ?? ReadDouble(element, "RenderSize.Height");
        if (width is null || height is null)
        {
            return null;
        }

        if (ReferenceEquals(root, element))
        {
            return (0, 0, width.Value, height.Value);
        }

        var transformToVisual = element.GetType().GetRuntimeMethods()
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "TransformToVisual", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(root);
            });
        if (transformToVisual is null)
        {
            return null;
        }

        try
        {
            var transform = transformToVisual.Invoke(element, new[] { root });
            var transformPoint = transform?.GetType().GetRuntimeMethods()
                .FirstOrDefault(method => string.Equals(method.Name, "TransformPoint", StringComparison.Ordinal) && method.GetParameters().Length == 1);
            var pointType = transformPoint?.GetParameters()[0].ParameterType;
            var origin = pointType is null ? null : Activator.CreateInstance(pointType, 0d, 0d);
            var point = origin is null ? null : transformPoint?.Invoke(transform, new[] { origin });
            var x = point is null ? null : ReadDouble(point, "X");
            var y = point is null ? null : ReadDouble(point, "Y");
            return x is null || y is null ? null : (x.Value, y.Value, width.Value, height.Value);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsReflectionInput(string typeName)
    {
        return typeName.Contains("TextBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("RichEditBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("AutoSuggestBox", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReflectionSecret(string typeName)
    {
        return typeName.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReflectionSelection(string typeName)
    {
        return typeName.Contains("CheckBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ComboBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("RadioButton", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ToggleSwitch", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ToggleButton", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Selector", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReflectionSelectionValue(string typeName)
    {
        return typeName.Contains("ComboBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ListBox", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ListView", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Selector", StringComparison.OrdinalIgnoreCase);
    }

    private static string TagFor(object element)
    {
        var name = element.GetType().Name;
        if (IsReflectionInput(name) || IsReflectionSecret(name))
        {
            return "input";
        }

        if (name.Contains("Button", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MenuItem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Hyperlink", StringComparison.OrdinalIgnoreCase))
        {
            return "button";
        }

        if (name.Contains("TextBlock", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Text", StringComparison.OrdinalIgnoreCase))
        {
            return "span";
        }

        if (name.Contains("List", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ComboBox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Selector", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NavigationView", StringComparison.OrdinalIgnoreCase))
        {
            return "select";
        }

        if (name.Contains("WebView", StringComparison.OrdinalIgnoreCase))
        {
            return "iframe";
        }

        return "div";
    }

    private static bool ShouldMaskSensitiveText(string typeName, string? elementName, string? text, ResolvedPrivacy privacy, RumSessionReplayConfig config)
    {
        if (privacy.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.Allow || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (ContainsSensitiveName(typeName) || ContainsSensitiveName(elementName))
        {
            return true;
        }

        foreach (var pattern in config.SensitiveTextPatterns)
        {
            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
        }

        return false;
    }

    private static bool ContainsSensitiveName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("creditcard", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("cardnumber", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCustomRendered(string typeName, RumSessionReplayConfig config)
    {
        foreach (var marker in config.CustomRenderedTypeNameMarkers)
        {
            if (typeName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWebView(object element)
    {
        return element.GetType().Name.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
               element.GetType().GetRuntimeProperty("CoreWebView2") is not null;
    }

    private static void MarkWebView(SessionReplayNode node, object webView)
    {
        node.Text = null;
        node.WebViewSlotId = WebViewSlotRegistry.GetOrCreate(webView);
        node.WebViewIsVisible = true;
        node.Attributes["data-guance-webview-slot-id"] = node.WebViewSlotId;
    }

    private static void MarkCustomRendered(SessionReplayNode node, string renderer)
    {
        node.Text ??= "Custom rendered content";
        node.Attributes["data-guance-custom-rendered"] = "true";
        node.Attributes["data-guance-renderer"] = renderer;
    }

    private static double? ReadDouble(object element, string propertyName)
    {
        var value = ReadPropertyPath(element, propertyName);
        if (value is null && (propertyName is "Canvas.Left" or "Canvas.Top"))
        {
            value = ReadAttachedCanvasValue(element, propertyName.EndsWith("Left", StringComparison.Ordinal) ? "GetLeft" : "GetTop");
        }

        return value switch
        {
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => d,
            float f when !float.IsNaN(f) && !float.IsInfinity(f) => f,
            int i => i,
            decimal m => (double)m,
            _ => null
        };
    }

    private static object? ReadAttachedCanvasValue(object element, string methodName)
    {
        var canvasType = Type.GetType("Microsoft.UI.Xaml.Controls.Canvas, Microsoft.WinUI")
            ?? Type.GetType("Windows.UI.Xaml.Controls.Canvas, Windows");
        var method = canvasType?.GetRuntimeMethods()
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return string.Equals(candidate.Name, methodName, StringComparison.Ordinal) &&
                       parameters.Length == 1 &&
                       parameters[0].ParameterType.IsInstanceOfType(element);
            });
        return method?.Invoke(null, new[] { element });
    }

    private static string? MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return new string('*', Math.Min(text.Length, 8));
    }

    private static void RedactDescendantText(IEnumerable<SessionReplayNode> descendants, string sensitiveValue)
    {
        foreach (var descendant in descendants)
        {
            if (!string.IsNullOrEmpty(descendant.Text) &&
                descendant.Text.Contains(sensitiveValue, StringComparison.Ordinal))
            {
                descendant.Text = MaskText(descendant.Text);
            }

            RedactDescendantText(descendant.Children, sensitiveValue);
        }
    }

    private static string? LimitText(string? text, RumSessionReplayConfig config)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= config.MaxTextLength)
        {
            return text;
        }

        return text[..config.MaxTextLength] + "...";
    }

    private static void ApplyNodeLimits(SessionReplayNode node, RumSessionReplayConfig config)
    {
        if (node.Text is not null)
        {
            node.Text = LimitText(node.Text, config);
        }

        foreach (var key in node.Attributes.Keys.ToArray())
        {
            var value = node.Attributes[key];
            if (value.Length > config.MaxAttributeValueLength)
            {
                node.Attributes[key] = value[..config.MaxAttributeValueLength] + "...";
            }
        }
    }

    private static SessionReplayNode TruncatedNode(double x, double y, double width, double height)
    {
        var node = new SessionReplayNode("div", x, y, Math.Max(0, width), Math.Max(0, height))
        {
            Text = "Truncated"
        };
        node.Attributes["data-guance-truncated"] = "true";
        return node;
    }

    private sealed class MapBudget
    {
        private readonly int maxNodes;
        private readonly int maxDepth;
        private int nodes;
        private bool truncationEmitted;

        public MapBudget(RumSessionReplayConfig config)
        {
            maxNodes = Math.Max(1, config.MaxNodeCount);
            maxDepth = Math.Max(1, config.MaxTreeDepth);
        }

        public bool TryEnter(int depth)
        {
            if (depth > maxDepth || nodes >= maxNodes)
            {
                return false;
            }

            nodes++;
            return true;
        }

        public bool TryEmitTruncation()
        {
            if (truncationEmitted)
            {
                return false;
            }

            truncationEmitted = true;
            return true;
        }
    }
}
