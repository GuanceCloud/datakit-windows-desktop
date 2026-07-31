using Guance.Rum.Windows;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class ApplicationLaunchTrackerTests
{
    [Fact]
    public void DisabledTracker_DoesNotEmitLaunchActions()
    {
        var records = new List<ApplicationLaunchRecord>();
        var tracker = new ApplicationLaunchTracker(
            records.Add,
            new FakeApplicationLaunchClock(100, 200, 20));

        tracker.MarkWindowCreated();
        tracker.CompleteFrame();

        Assert.Empty(records);
    }

    [Fact]
    public void CompleteFrame_EmitsAndroidCompatibleColdLaunchPhases()
    {
        var records = new List<ApplicationLaunchRecord>();
        var clock = new FakeApplicationLaunchClock(
            processStartUnixNanoseconds: 100,
            unixNanoseconds: 200,
            monotonicNanoseconds: 20);
        var tracker = new ApplicationLaunchTracker(records.Add, clock);

        tracker.Enable();
        clock.Set(unixNanoseconds: 350, monotonicNanoseconds: 170);
        tracker.MarkWindowCreated();
        clock.Set(unixNanoseconds: 500, monotonicNanoseconds: 320);
        tracker.CompleteFrame();
        tracker.CompleteFrame();

        var record = Assert.Single(records);
        Assert.Equal("app cold start", record.Name);
        Assert.Equal("launch_cold", record.Type);
        Assert.Equal(100, record.StartTimeNanoseconds);
        Assert.Equal(400, record.DurationNanoseconds);
        Assert.Equal(
            "{\"start\":0,\"duration\":100}",
            record.Properties[RumConstants.AppPreApplicationInitTime]);
        Assert.Equal(
            "{\"start\":100,\"duration\":150}",
            record.Properties[RumConstants.AppApplicationInitTime]);
        Assert.Equal(
            "{\"start\":250,\"duration\":150}",
            record.Properties[RumConstants.AppFirstFrameInitTime]);
    }

    [Fact]
    public void ForegroundAfterShortBackground_DoesNotEmitHotLaunch()
    {
        var records = new List<ApplicationLaunchRecord>();
        var clock = new FakeApplicationLaunchClock(100, 200, 20);
        var tracker = new ApplicationLaunchTracker(records.Add, clock);
        tracker.Enable();
        tracker.MarkWindowCreated();
        tracker.CompleteFrame();

        tracker.EnterBackground();
        clock.Advance(TimeSpan.FromSeconds(9));
        tracker.BeginForeground();
        clock.Advance(TimeSpan.FromMilliseconds(20));
        tracker.CompleteFrame();

        Assert.Single(records);
        Assert.Equal(RumConstants.ActionTypeLaunchCold, records[0].Type);
    }

    [Fact]
    public void ForegroundAfterTenSeconds_EmitsHotLaunchAtNextFrame()
    {
        var records = new List<ApplicationLaunchRecord>();
        var clock = new FakeApplicationLaunchClock(100, 200, 20);
        var tracker = new ApplicationLaunchTracker(records.Add, clock);
        tracker.Enable();
        tracker.MarkWindowCreated();
        tracker.CompleteFrame();

        tracker.EnterBackground();
        clock.Advance(TimeSpan.FromSeconds(10));
        tracker.BeginForeground();
        var expectedStart = clock.UnixNanoseconds;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        tracker.CompleteFrame();
        tracker.CompleteFrame();

        Assert.Equal(2, records.Count);
        var hot = records[1];
        Assert.Equal("app hot start", hot.Name);
        Assert.Equal("launch_hot", hot.Type);
        Assert.Equal(expectedStart, hot.StartTimeNanoseconds);
        Assert.Equal(50_000_000, hot.DurationNanoseconds);
        Assert.Empty(hot.Properties);
    }

    private sealed class FakeApplicationLaunchClock : IApplicationLaunchClock
    {
        public FakeApplicationLaunchClock(
            long processStartUnixNanoseconds,
            long unixNanoseconds,
            long monotonicNanoseconds)
        {
            ProcessStartUnixNanoseconds = processStartUnixNanoseconds;
            UnixNanoseconds = unixNanoseconds;
            MonotonicNanoseconds = monotonicNanoseconds;
        }

        public long ProcessStartUnixNanoseconds { get; }

        public long UnixNanoseconds { get; private set; }

        public long MonotonicNanoseconds { get; private set; }

        public ApplicationLaunchMoment Now() =>
            new(UnixNanoseconds, MonotonicNanoseconds);

        public long ElapsedNanoseconds(long startTimestamp, long endTimestamp) =>
            Math.Max(0, endTimestamp - startTimestamp);

        public void Set(long unixNanoseconds, long monotonicNanoseconds)
        {
            UnixNanoseconds = unixNanoseconds;
            MonotonicNanoseconds = monotonicNanoseconds;
        }

        public void Advance(TimeSpan duration)
        {
            var nanoseconds = duration.Ticks * 100;
            UnixNanoseconds += nanoseconds;
            MonotonicNanoseconds += nanoseconds;
        }
    }
}
