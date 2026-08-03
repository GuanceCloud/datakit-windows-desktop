using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.Transport;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class AutomaticActionInstrumentationTests
{
    [Fact]
    public async Task AttachedWinUiWindow_ClassifiesKeyboardAndPointerActivations()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromHours(1)
            },
            rumQueue,
            new HoldRumTransport());
        var button = new Microsoft.UI.Xaml.Controls.Button { Name = "WinUiActionButton" };
        var window = new FakeWinUiWindow { Content = button };

        client.StartView("WinUI host");
        client.AttachWinUIWindow(window);

        button.RaiseKeyDown();
        button.RaiseClick();
        button.RaiseKeyUp();
        button.RaisePointerPressed();
        button.RaiseClick();

        var lines = (await rumQueue.PeekAsync(20, CancellationToken.None))
            .Select(item => item.Line)
            .Where(line => line.StartsWith("action,", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(lines, line =>
            line.Contains("action_name=WinUiActionButton", StringComparison.Ordinal) &&
            line.Contains("action_type=key", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("action_name=WinUiActionButton", StringComparison.Ordinal) &&
            line.Contains("action_type=click", StringComparison.Ordinal));
        Assert.All(
            lines,
            line => Assert.True(
                line.Contains(",action_type=click,", StringComparison.Ordinal) ||
                line.Contains(",action_type=key,", StringComparison.Ordinal),
                $"Unexpected automatic WinUI action type: {line}"));
    }

    private sealed class FakeWinUiWindow
    {
        public object? Content { get; init; }
    }

    private sealed class HoldRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));

        public void Dispose()
        {
        }
    }
}
