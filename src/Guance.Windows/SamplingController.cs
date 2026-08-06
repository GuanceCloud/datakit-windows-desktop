namespace Guance.Windows;

internal sealed class SamplingController
{
    private readonly GuanceConfig config;
    private readonly Random random = new();
    private readonly object gate = new();

    public SamplingController(GuanceConfig config)
    {
        this.config = config;
        Refresh();
    }

    public bool SessionSampled { get; private set; }
    public bool SessionErrorSampled { get; private set; }

    public void Refresh()
    {
        lock (gate)
        {
            SessionSampled = Hit(config.SampleRate);
            SessionErrorSampled = !SessionSampled && Hit(config.SessionErrorSampleRate);
        }
    }

    public bool ShouldCollect(string measurement)
    {
        if (SessionSampled)
        {
            return true;
        }

        return measurement == RumConstants.MeasurementError && SessionErrorSampled;
    }

    private bool Hit(double rate)
    {
        if (rate <= 0)
        {
            return false;
        }

        if (rate >= 1)
        {
            return true;
        }

        lock (gate)
        {
            return random.NextDouble() <= rate;
        }
    }
}
