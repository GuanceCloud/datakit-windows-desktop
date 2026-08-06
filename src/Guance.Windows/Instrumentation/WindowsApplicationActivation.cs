using System.Runtime.InteropServices;

namespace Guance.Windows;

internal static class WindowsApplicationActivation
{
    public static bool IsCurrentProcessForeground()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
