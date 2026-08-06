namespace Microsoft.UI.Xaml.Controls;

internal class ButtonBase
{
    public string? Name { get; init; }

    public event EventHandler? Click;

    public event EventHandler? KeyDown;

    public event EventHandler? KeyUp;

    public event EventHandler? PointerPressed;

    public void RaiseClick() => Click?.Invoke(this, EventArgs.Empty);

    public void RaiseKeyDown() => KeyDown?.Invoke(this, EventArgs.Empty);

    public void RaiseKeyUp() => KeyUp?.Invoke(this, EventArgs.Empty);

    public void RaisePointerPressed() => PointerPressed?.Invoke(this, EventArgs.Empty);
}

internal sealed class Button : ButtonBase;
