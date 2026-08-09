namespace Guance.Windows;

/// <summary>Represents one SDK diagnostic message.</summary>
/// <param name="Timestamp">Time at which the diagnostic occurred.</param>
/// <param name="Level">Diagnostic severity.</param>
/// <param name="Source">SDK component that emitted the diagnostic.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="StatusCode">Related HTTP status code, if any.</param>
/// <param name="Exception">Related exception, if any.</param>
public sealed record RumDiagnosticEvent(
    DateTimeOffset Timestamp,
    RumDiagnosticLevel Level,
    string Source,
    string Message,
    int? StatusCode = null,
    Exception? Exception = null);

/// <summary>Specifies the severity of an SDK diagnostic event.</summary>
public enum RumDiagnosticLevel
{
    /// <summary>Detailed diagnostic output.</summary>
    Debug,
    /// <summary>Informational SDK output.</summary>
    Info,
    /// <summary>A recoverable SDK warning.</summary>
    Warning,
    /// <summary>An SDK operation failed.</summary>
    Error
}
