using System.Runtime.CompilerServices;
using Guance.Windows.Queue;

namespace Guance.Windows.Tests;

internal static class TestQueueLease
{
    public static Task<QueueBatch<T>?> AcquireAsync<T>(
        List<T> items,
        Func<T, long> getPayloadBytes,
        CancellationToken cancellationToken,
        int maximumItems = int.MaxValue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State<T>.States.GetOrCreateValue(items);
        lock (state)
        {
            state.Lease ??= items.Count == 0
                ? null
                : new QueueBatch<T>(
                    Guid.NewGuid().ToString("N"),
                    items.Take(maximumItems).ToArray(),
                    items.Take(maximumItems).Sum(getPayloadBytes),
                    DateTimeOffset.UtcNow);
            return Task.FromResult(state.Lease);
        }
    }

    public static Task CompleteAsync<T>(List<T> items, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State<T>.States.GetOrCreateValue(items);
        lock (state)
        {
            if (state.Lease?.LeaseId == leaseId)
            {
                items.RemoveRange(0, Math.Min(items.Count, state.Lease.Items.Count));
                state.Lease = null;
            }
        }
        return Task.CompletedTask;
    }

    public static Task AbandonAsync<T>(List<T> items, string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State<T>.States.GetOrCreateValue(items);
        lock (state)
        {
            if (state.Lease?.LeaseId == leaseId) state.Lease = null;
        }
        return Task.CompletedTask;
    }

    private static class State<T>
    {
        public static readonly ConditionalWeakTable<List<T>, Holder<T>> States = new();
    }

    private sealed class Holder<T>
    {
        public QueueBatch<T>? Lease { get; set; }
    }
}
