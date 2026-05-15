using System.Diagnostics;
using Guance.Rum.Windows.SessionReplay;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class PerformanceBudgetTests
{
    [Fact]
    public void SessionReplayTreeMapper_MapsLargeReflectionTreeWithinBudget()
    {
        var root = BuildTree(1_200);
        var stopwatch = Stopwatch.StartNew();

        var mapped = SessionReplayTreeMapper.Map(root, new SessionReplayPrivacyOverrides(), new RumSessionReplayConfig());

        stopwatch.Stop();
        Assert.NotNull(mapped);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Replay tree mapping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void LineProtocolFormatter_FormatsHighVolumeEventsWithinBudget()
    {
        var events = Enumerable.Range(0, 10_000)
            .Select(index => new RumEvent("action", index)
                .WithTag("app_id", "app")
                .WithTag("session_id", "session")
                .WithTag("action_name", "Button " + index)
                .WithField("duration", index)
                .WithField("message", "value " + index))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        var totalBytes = events.Sum(item => LineProtocolFormatter.Format(item).Length);

        stopwatch.Stop();
        Assert.True(totalBytes > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Line protocol formatting took {stopwatch.Elapsed}.");
    }

    private static PerfNode BuildTree(int children)
    {
        var root = new PerfNode
        {
            Name = "Root",
            ActualWidth = 1024,
            ActualHeight = 768
        };

        for (var i = 0; i < children; i++)
        {
            root.Children.Add(new PerfNode
            {
                Name = "Label" + i,
                Text = "Value " + i,
                ActualWidth = 80,
                ActualHeight = 20,
                X = i % 20 * 48,
                Y = i / 20 * 22
            });
        }

        return root;
    }

    private sealed class PerfNode
    {
        public string? Name { get; init; }
        public string? Text { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
        public List<object> Children { get; } = new();
    }
}
