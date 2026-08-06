using Guance.Windows.Queue;

namespace Guance.Windows;

internal enum UploadAttemptStatus
{
    Empty,
    Consumed,
    RetryLater
}

internal sealed record UploadChannel(
    BatchStreamKind Kind,
    int Weight,
    Func<bool, CancellationToken, Task> SealAsync,
    Func<CancellationToken, Task<UploadAttemptStatus>> TryUploadAsync);

internal sealed class UploadScheduler : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private readonly UploadOptions options;
    private readonly UploadChannel[] channels;
    private readonly UploadChannel[] schedule;
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly CancellationTokenSource shutdown;
    private readonly Task worker;
    private int cursor;

    public UploadScheduler(UploadOptions options, IEnumerable<UploadChannel> channels, CancellationToken shutdownToken)
    {
        this.options = options;
        this.channels = channels.ToArray();
        schedule = this.channels.SelectMany(channel => Enumerable.Repeat(channel, channel.Weight)).ToArray();
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        worker = Task.Run(RunAsync);
    }

    public void Wake()
    {
        if (wakeSignal.CurrentCount == 0)
        {
            wakeSignal.Release();
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var channel in channels)
            {
                await channel.SealAsync(true, cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var consumed = await RunRoundNoLockAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
                if (consumed == 0) return;
            }
        }
        finally
        {
            cycleGate.Release();
        }
    }

    public async Task FlushCycleAsync(CancellationToken cancellationToken)
    {
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var channel in channels)
            {
                await channel.SealAsync(true, cancellationToken).ConfigureAwait(false);
            }
            await RunRoundNoLockAsync(options.MaxBatchesPerCycle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        Wake();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        shutdown.Dispose();
        wakeSignal.Dispose();
        cycleGate.Dispose();
    }

    private async Task RunAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await wakeSignal.WaitAsync(TickInterval, shutdown.Token).ConfigureAwait(false);
                await cycleGate.WaitAsync(shutdown.Token).ConfigureAwait(false);
                try
                {
                    foreach (var channel in channels)
                    {
                        await channel.SealAsync(false, shutdown.Token).ConfigureAwait(false);
                    }
                    await RunRoundNoLockAsync(options.MaxBatchesPerCycle, shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    cycleGate.Release();
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A channel reports its own diagnostics. Keep the shared scheduler alive.
            }
        }
    }

    private async Task<int> RunRoundNoLockAsync(int maximumBatches, CancellationToken cancellationToken)
    {
        if (schedule.Length == 0) return 0;
        var consumed = 0;
        var consecutiveWithoutProgress = 0;
        while (consumed < maximumBatches && consecutiveWithoutProgress < schedule.Length)
        {
            var channel = schedule[cursor++ % schedule.Length];
            var status = await channel.TryUploadAsync(cancellationToken).ConfigureAwait(false);
            if (status == UploadAttemptStatus.Consumed)
            {
                consumed++;
                consecutiveWithoutProgress = 0;
            }
            else
            {
                consecutiveWithoutProgress++;
            }
        }
        return consumed;
    }
}
