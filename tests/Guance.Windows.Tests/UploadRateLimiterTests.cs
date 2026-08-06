using System.Diagnostics;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class UploadRateLimiterTests
{
    [Fact]
    public async Task WaitAsync_RefillsTheSharedByteBudgetAtTheConfiguredRate()
    {
        var limiter = new UploadRateLimiter(new UploadOptions
        {
            MaxBytesPerSecond = 1_000,
            BurstBytes = 1_000,
            MaxRequestsPerSecond = 0
        });

        await limiter.WaitAsync(1_000, CancellationToken.None);
        var started = Stopwatch.GetTimestamp();
        await limiter.WaitAsync(100, CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(70));
    }

    [Fact]
    public async Task WaitAsync_CanBeCancelledWhileWaitingForBudget()
    {
        var limiter = new UploadRateLimiter(new UploadOptions
        {
            MaxBytesPerSecond = 100,
            BurstBytes = 100,
            MaxRequestsPerSecond = 0
        });
        await limiter.WaitAsync(100, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.WaitAsync(100, cancellation.Token));
    }
}
