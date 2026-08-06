namespace Guance.Windows;

internal static class LogStatusNames
{
    public static string ToProtocolValue(LogStatus status) => status switch
    {
        LogStatus.Debug => "debug",
        LogStatus.Info => "info",
        LogStatus.Warning => "warning",
        LogStatus.Error => "error",
        LogStatus.Critical => "critical",
        LogStatus.Ok => "ok",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
