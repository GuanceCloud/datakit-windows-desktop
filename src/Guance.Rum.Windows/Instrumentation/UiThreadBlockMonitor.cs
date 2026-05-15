using System.Diagnostics;
using System.Threading;

namespace Guance.Rum.Windows;

internal sealed class UiThreadBlockMonitor : IDisposable
{
    private readonly RumClient client;
    private readonly SynchronizationContext context;
    private readonly TimeSpan interval;
    private readonly TimeSpan threshold;
    private readonly TimeSpan cooldown;
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private DateTimeOffset lastReportedAt = DateTimeOffset.MinValue;
    private int suppressedCount;

    public UiThreadBlockMonitor(RumClient client, SynchronizationContext context, TimeSpan interval, TimeSpan threshold, TimeSpan cooldown)
    {
        this.client = client;
        this.context = context;
        this.interval = interval;
        this.threshold = threshold;
        this.cooldown = cooldown <= TimeSpan.Zero ? TimeSpan.Zero : cooldown;
    }

    public void Start()
    {
        timer = new System.Threading.Timer(_ => Probe(), null, interval, interval);
    }

    public void Dispose()
    {
        timer?.Dispose();
    }

    private void Probe()
    {
        var postedAt = Stopwatch.GetTimestamp();
        context.Post(_ =>
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - postedAt;
            var elapsed = TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
            if (elapsed >= threshold)
            {
                var now = DateTimeOffset.UtcNow;
                int suppressed;
                lock (gate)
                {
                    if (cooldown > TimeSpan.Zero && now - lastReportedAt < cooldown)
                    {
                        suppressedCount++;
                        return;
                    }

                    suppressed = suppressedCount;
                    suppressedCount = 0;
                    lastReportedAt = now;
                }

                client.AddLongTask(
                    elapsed,
                    $"UI thread message loop delay: delayed_ms={elapsed.TotalMilliseconds:F0}; threshold_ms={threshold.TotalMilliseconds:F0}",
                    new Dictionary<string, object?>
                    {
                        [RumConstants.LongTaskSource] = "ui_thread_block_monitor",
                        [RumConstants.LongTaskDelay] = (long)(elapsed.TotalMilliseconds * 1_000_000),
                        [RumConstants.LongTaskThreshold] = (long)(threshold.TotalMilliseconds * 1_000_000),
                        [RumConstants.LongTaskCooldown] = (long)(cooldown.TotalMilliseconds * 1_000_000),
                        [RumConstants.LongTaskSuppressedCount] = suppressed
                    });
            }
        }, null);
    }
}
