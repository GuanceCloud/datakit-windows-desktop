using System.Diagnostics;

namespace Guance.Rum.Windows;

internal static class Clock
{
    private static readonly long UnixEpochNanoseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public static long UnixTimeNanoseconds()
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - StartTimestamp;
        var elapsedNanoseconds = elapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
        return UnixEpochNanoseconds + elapsedNanoseconds;
    }

    public static long DurationNanoseconds(Stopwatch stopwatch)
    {
        return stopwatch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
    }
}
