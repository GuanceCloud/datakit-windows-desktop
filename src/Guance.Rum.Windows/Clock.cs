using System.Diagnostics;

namespace Guance.Rum.Windows;

internal static class Clock
{
    private const long NanosecondsPerSecond = 1_000_000_000L;
    private const long NanosecondsPerTimeSpanTick = 100L;
    private static readonly long UnixEpochNanoseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public static long UnixTimeNanoseconds()
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - StartTimestamp;
        var elapsedNanoseconds = StopwatchTicksToNanoseconds(elapsedTicks);
        return elapsedNanoseconds > long.MaxValue - UnixEpochNanoseconds
            ? long.MaxValue
            : UnixEpochNanoseconds + elapsedNanoseconds;
    }

    public static long DurationNanoseconds(Stopwatch stopwatch)
    {
        return StopwatchTicksToNanoseconds(stopwatch.ElapsedTicks);
    }

    public static long DurationNanoseconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return duration.Ticks > long.MaxValue / NanosecondsPerTimeSpanTick
            ? long.MaxValue
            : duration.Ticks * NanosecondsPerTimeSpanTick;
    }

    public static long UnixTimeNanosecondsBefore(TimeSpan duration)
    {
        var now = UnixTimeNanoseconds();
        var durationNanoseconds = DurationNanoseconds(duration);
        return durationNanoseconds >= now ? 0 : now - durationNanoseconds;
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static TimeSpan ElapsedSince(long timestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - timestamp;
        return elapsedTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
    }

    private static long StopwatchTicksToNanoseconds(long ticks)
    {
        if (ticks <= 0)
        {
            return 0;
        }

        var wholeSeconds = ticks / Stopwatch.Frequency;
        if (wholeSeconds > long.MaxValue / NanosecondsPerSecond)
        {
            return long.MaxValue;
        }

        var remainingTicks = ticks % Stopwatch.Frequency;
        var remainingNanoseconds = (long)(remainingTicks * (double)NanosecondsPerSecond / Stopwatch.Frequency);
        var wholeNanoseconds = wholeSeconds * NanosecondsPerSecond;
        return remainingNanoseconds > long.MaxValue - wholeNanoseconds
            ? long.MaxValue
            : wholeNanoseconds + remainingNanoseconds;
    }
}
