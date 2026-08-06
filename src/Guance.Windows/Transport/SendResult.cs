namespace Guance.Windows.Transport;

internal sealed record SendResult(
    bool DeleteFromQueue,
    bool RetryLater,
    int? StatusCode,
    string? ErrorMessage,
    TimeSpan? RetryAfter = null)
{
    public static SendResult Success(int statusCode) => new(true, false, statusCode, null);
    public static SendResult TerminalFailure(int statusCode, string? message) => new(true, false, statusCode, message);
    public static SendResult Retry(int? statusCode, string? message, TimeSpan? retryAfter = null) =>
        new(false, true, statusCode, message, retryAfter);
}
