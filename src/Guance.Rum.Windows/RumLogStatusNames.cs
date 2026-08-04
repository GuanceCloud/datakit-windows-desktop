namespace Guance.Rum.Windows;

internal static class RumLogStatusNames
{
    public static string ToProtocolValue(RumLogStatus status) => status switch
    {
        RumLogStatus.Debug => "debug",
        RumLogStatus.Info => "info",
        RumLogStatus.Warning => "warning",
        RumLogStatus.Error => "error",
        RumLogStatus.Critical => "critical",
        RumLogStatus.Ok => "ok",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
