namespace Guance.Windows;

/// <summary>Specifies a predefined Guance log status.</summary>
public enum LogStatus
{
    /// <summary>Debug diagnostic information.</summary>
    Debug,
    /// <summary>Informational application output.</summary>
    Info,
    /// <summary>A recoverable warning.</summary>
    Warning,
    /// <summary>An application error.</summary>
    Error,
    /// <summary>A critical or fatal condition.</summary>
    Critical,
    /// <summary>A successful or healthy operation.</summary>
    Ok
}
