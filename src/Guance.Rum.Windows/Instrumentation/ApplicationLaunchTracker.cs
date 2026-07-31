using System.Diagnostics;
using System.Text.Json;

namespace Guance.Rum.Windows;

internal readonly record struct ApplicationLaunchMoment(
    long UnixNanoseconds,
    long MonotonicTimestamp);

internal sealed record ApplicationLaunchRecord(
    string Name,
    string Type,
    long StartTimeNanoseconds,
    long DurationNanoseconds,
    IReadOnlyDictionary<string, object?> Properties);

internal interface IApplicationLaunchClock
{
    long ProcessStartUnixNanoseconds { get; }

    ApplicationLaunchMoment Now();

    long ElapsedNanoseconds(long startTimestamp, long endTimestamp);
}

internal sealed class SystemApplicationLaunchClock : IApplicationLaunchClock
{
    public SystemApplicationLaunchClock()
    {
        ProcessStartUnixNanoseconds = ReadProcessStartUnixNanoseconds();
    }

    public long ProcessStartUnixNanoseconds { get; }

    public ApplicationLaunchMoment Now() =>
        new(Clock.UnixTimeNanoseconds(), Clock.Timestamp());

    public long ElapsedNanoseconds(long startTimestamp, long endTimestamp) =>
        Clock.ElapsedNanoseconds(startTimestamp, endTimestamp);

    private static long ReadProcessStartUnixNanoseconds()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var start = process.StartTime.ToUniversalTime();
            var ticksSinceUnixEpoch = start.Ticks - DateTime.UnixEpoch.Ticks;
            return ticksSinceUnixEpoch <= 0 || ticksSinceUnixEpoch > long.MaxValue / 100
                ? 0
                : ticksSinceUnixEpoch * 100;
        }
        catch
        {
            return 0;
        }
    }
}

internal sealed class ApplicationLaunchTracker
{
    private static readonly long BackgroundThresholdNanoseconds =
        Clock.DurationNanoseconds(TimeSpan.FromSeconds(10));
    private readonly object gate = new();
    private readonly IApplicationLaunchClock clock;
    private readonly Action<ApplicationLaunchRecord> emit;
    private readonly ApplicationLaunchMoment sdkInitialization;
    private ApplicationLaunchMoment? firstWindowCreated;
    private ApplicationLaunchMoment? hotStart;
    private long? backgroundTimestamp;
    private bool enabled;
    private bool coldCompleted;

    public ApplicationLaunchTracker(
        Action<ApplicationLaunchRecord> emit,
        IApplicationLaunchClock? clock = null)
    {
        this.emit = emit ?? throw new ArgumentNullException(nameof(emit));
        this.clock = clock ?? new SystemApplicationLaunchClock();
        sdkInitialization = this.clock.Now();
    }

    public void Enable()
    {
        lock (gate)
        {
            enabled = true;
        }
    }

    public void MarkWindowCreated()
    {
        lock (gate)
        {
            if (enabled && !coldCompleted && firstWindowCreated is null)
            {
                firstWindowCreated = clock.Now();
            }
        }
    }

    public void EnterBackground()
    {
        lock (gate)
        {
            if (!enabled || !coldCompleted)
            {
                return;
            }

            backgroundTimestamp = clock.Now().MonotonicTimestamp;
            hotStart = null;
        }
    }

    public void BeginForeground()
    {
        lock (gate)
        {
            if (!enabled || !coldCompleted || backgroundTimestamp is not long background)
            {
                return;
            }

            var now = clock.Now();
            if (clock.ElapsedNanoseconds(background, now.MonotonicTimestamp) >= BackgroundThresholdNanoseconds)
            {
                hotStart = now;
            }

            backgroundTimestamp = null;
        }
    }

    public void CompleteFrame()
    {
        ApplicationLaunchRecord? record = null;
        lock (gate)
        {
            if (!enabled)
            {
                return;
            }

            var end = clock.Now();
            if (!coldCompleted)
            {
                coldCompleted = true;
                var processStart = NormalizeProcessStart(clock.ProcessStartUnixNanoseconds);
                var windowCreated = firstWindowCreated ?? sdkInitialization;
                var preApplicationDuration = PositiveDifference(
                    sdkInitialization.UnixNanoseconds,
                    processStart);
                var applicationDuration = clock.ElapsedNanoseconds(
                    sdkInitialization.MonotonicTimestamp,
                    windowCreated.MonotonicTimestamp);
                var firstFrameDuration = clock.ElapsedNanoseconds(
                    windowCreated.MonotonicTimestamp,
                    end.MonotonicTimestamp);
                var duration = SaturatingAdd(
                    preApplicationDuration,
                    SaturatingAdd(applicationDuration, firstFrameDuration));

                record = new ApplicationLaunchRecord(
                    RumConstants.ActionNameLaunchCold,
                    RumConstants.ActionTypeLaunchCold,
                    processStart,
                    duration,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [RumConstants.AppPreApplicationInitTime] = PhaseJson(0, preApplicationDuration),
                        [RumConstants.AppApplicationInitTime] = PhaseJson(preApplicationDuration, applicationDuration),
                        [RumConstants.AppFirstFrameInitTime] = PhaseJson(
                            SaturatingAdd(preApplicationDuration, applicationDuration),
                            firstFrameDuration)
                    });
            }
            else if (hotStart is ApplicationLaunchMoment foreground)
            {
                hotStart = null;
                record = new ApplicationLaunchRecord(
                    RumConstants.ActionNameLaunchHot,
                    RumConstants.ActionTypeLaunchHot,
                    foreground.UnixNanoseconds,
                    clock.ElapsedNanoseconds(
                        foreground.MonotonicTimestamp,
                        end.MonotonicTimestamp),
                    new Dictionary<string, object?>(StringComparer.Ordinal));
            }
        }

        if (record is not null)
        {
            emit(record);
        }
    }

    private long NormalizeProcessStart(long processStart)
    {
        return processStart > 0 && processStart <= sdkInitialization.UnixNanoseconds
            ? processStart
            : sdkInitialization.UnixNanoseconds;
    }

    private static long PositiveDifference(long end, long start) =>
        end <= start ? 0 : end - start;

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static string PhaseJson(long start, long duration) =>
        JsonSerializer.Serialize(new { start, duration });
}
