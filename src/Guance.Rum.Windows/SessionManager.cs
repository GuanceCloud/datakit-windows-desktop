namespace Guance.Rum.Windows;

internal sealed class SessionManager
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);
    private readonly object gate = new();
    private readonly SamplingController sampling;
    private DateTimeOffset lastActivity = DateTimeOffset.UtcNow;

    public SessionManager(SamplingController sampling)
    {
        this.sampling = sampling;
        SessionId = Guid.NewGuid().ToString("N");
    }

    public string SessionId { get; private set; }

    public bool IsSampled => sampling.SessionSampled;
    public bool IsErrorSampled => sampling.SessionErrorSampled;

    public void Touch()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastActivity >= SessionTimeout)
            {
                SessionId = Guid.NewGuid().ToString("N");
                sampling.Refresh();
            }

            lastActivity = now;
        }
    }
}
