namespace Guance.Windows;

internal sealed class RetryBackoff
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private readonly TimeSpan baseDelay;
    private readonly object gate = new();
    private long retryScheduledAt;
    private TimeSpan retryDelay;
    private int attempt;

    public RetryBackoff(TimeSpan baseDelay)
    {
        this.baseDelay = baseDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : baseDelay;
    }

    public bool CanAttemptNow()
    {
        lock (gate)
        {
            return retryDelay <= TimeSpan.Zero || Clock.ElapsedSince(retryScheduledAt) >= retryDelay;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            attempt = 0;
            retryScheduledAt = 0;
            retryDelay = TimeSpan.Zero;
        }
    }

    public void ScheduleRetry(TimeSpan? minimumDelay = null)
    {
        lock (gate)
        {
            attempt = Math.Min(attempt + 1, 10);
            var delayMilliseconds = Math.Min(
                MaxDelay.TotalMilliseconds,
                baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
            retryScheduledAt = Clock.Timestamp();
            retryDelay = TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds * jitter));
            if (minimumDelay > retryDelay)
            {
                retryDelay = minimumDelay.Value;
            }
        }
    }
}
