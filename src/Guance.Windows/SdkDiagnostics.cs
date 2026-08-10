using System.Diagnostics;
using System.Reflection;

namespace Guance.Windows;

internal static class SdkDiagnostics
{
    internal static bool IsEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            if (Debugger.IsAttached)
            {
                return true;
            }

            var debuggable = Assembly.GetEntryAssembly()?.GetCustomAttribute<DebuggableAttribute>();
            return debuggable?.IsJITTrackingEnabled == true || debuggable?.IsJITOptimizerDisabled == true;
#endif
        }
    }

    internal static void Write(RumDiagnosticEvent item)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var status = item.StatusCode is null ? string.Empty : $" status={item.StatusCode}";
            WriteLine($"[{item.Timestamp:O}] [Guance.RUM] {item.Level} {item.Source}{status}: {item.Message}");

            if (item.Exception is not null)
            {
                WriteLine(item.Exception.ToString());
            }
        }
        catch
        {
            // Local diagnostics must never affect telemetry collection or upload.
        }
    }

    internal static void WriteLine(string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            Console.WriteLine(message);
            Debug.WriteLine(message);
        }
        catch
        {
            // Local diagnostics must never affect telemetry collection or upload.
        }
    }
}
