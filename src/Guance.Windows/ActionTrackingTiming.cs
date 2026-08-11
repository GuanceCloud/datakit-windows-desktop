namespace Guance.Windows;

internal sealed record ActionTrackingTiming(TimeSpan FrequentProtection, TimeSpan MaxDuration)
{
    public static ActionTrackingTiming Default { get; } = new(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(5));

    public ActionTrackingTiming Validate()
    {
        if (FrequentProtection <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FrequentProtection));
        }

        if (MaxDuration <= FrequentProtection)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDuration));
        }

        return this;
    }
}
