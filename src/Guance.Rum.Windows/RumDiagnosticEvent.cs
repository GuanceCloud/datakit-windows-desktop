namespace Guance.Rum.Windows;

public sealed record RumDiagnosticEvent(
    DateTimeOffset Timestamp,
    RumDiagnosticLevel Level,
    string Source,
    string Message,
    int? StatusCode = null,
    Exception? Exception = null);

public enum RumDiagnosticLevel
{
    Debug,
    Info,
    Warning,
    Error
}
