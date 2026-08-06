using System.Diagnostics;
using System.Reflection;
using Guance.Windows.SessionReplay;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class ClockTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(22)]
    [InlineData(31)]
    public void DurationNanoseconds_DoesNotOverflowForLongRunningStopwatch(int minutes)
    {
        var stopwatch = new Stopwatch();
        var elapsedField = typeof(Stopwatch).GetField("_elapsed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate Stopwatch elapsed field.");
        elapsedField.SetValue(stopwatch, checked(Stopwatch.Frequency * minutes * 60L));

        var duration = Clock.DurationNanoseconds(stopwatch);

        Assert.Equal(minutes * 60L * 1_000_000_000L, duration);
    }

    [Fact]
    public void DurationNanoseconds_ClampsNegativeTimeSpanToZero()
    {
        Assert.Equal(0, Clock.DurationNanoseconds(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(1_000, 900, true)]
    [InlineData(900, 1_000, false)]
    [InlineData(1_201, 1_000, false)]
    public void SessionReplayCoalesceWindow_RequiresForwardTime(long timestamp, long previousTimestamp, bool expected)
    {
        Assert.Equal(expected, SessionReplayManager.IsWithinCoalesceWindow(timestamp, previousTimestamp));
    }
}
