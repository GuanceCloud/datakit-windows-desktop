namespace Guance.Windows;

internal sealed class SessionManager
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly SamplingController sampling;
    private long lastActivity = Clock.Timestamp();

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
            var now = Clock.Timestamp();
            if (Clock.ElapsedSince(lastActivity) >= SessionTimeout)
            {
                SessionId = Guid.NewGuid().ToString("N");
                sampling.Refresh();
            }

            lastActivity = now;
        }
    }
}
