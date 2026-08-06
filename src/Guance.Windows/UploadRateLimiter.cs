using System.Diagnostics;

namespace Guance.Windows;

internal sealed class UploadRateLimiter
{
    private readonly object gate = new();
    private readonly UploadOptions options;
    private readonly double maximumRequestTokens;
    private long lastTimestamp;
    private double byteTokens;
    private double requestTokens;

    public UploadRateLimiter(UploadOptions options)
    {
        this.options = options;
        maximumRequestTokens = options.MaxRequestsPerSecond <= 0
            ? double.PositiveInfinity
            : Math.Max(1, options.MaxRequestsPerSecond);
        byteTokens = options.MaxBytesPerSecond <= 0 ? double.PositiveInfinity : options.BurstBytes;
        requestTokens = maximumRequestTokens;
        lastTimestamp = Stopwatch.GetTimestamp();
    }

    public async Task WaitAsync(long payloadBytes, CancellationToken cancellationToken)
    {
        if (payloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        }

        if (options.MaxBytesPerSecond > 0 && payloadBytes > options.BurstBytes)
        {
            throw new InvalidOperationException("An upload batch exceeds Upload.BurstBytes and can never acquire the byte budget.");
        }

        while (true)
        {
            TimeSpan delay;
            lock (gate)
            {
                RefillNoLock();
                var hasBytes = options.MaxBytesPerSecond <= 0 || byteTokens >= payloadBytes;
                var hasRequest = options.MaxRequestsPerSecond <= 0 || requestTokens >= 1;
                if (hasBytes && hasRequest)
                {
                    if (options.MaxBytesPerSecond > 0) byteTokens -= payloadBytes;
                    if (options.MaxRequestsPerSecond > 0) requestTokens -= 1;
                    return;
                }

                var byteDelaySeconds = hasBytes || options.MaxBytesPerSecond <= 0
                    ? 0
                    : (payloadBytes - byteTokens) / options.MaxBytesPerSecond;
                var requestDelaySeconds = hasRequest || options.MaxRequestsPerSecond <= 0
                    ? 0
                    : (1 - requestTokens) / options.MaxRequestsPerSecond;
                delay = TimeSpan.FromSeconds(Math.Max(0.001, Math.Max(byteDelaySeconds, requestDelaySeconds)));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefillNoLock()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (double)(now - lastTimestamp) / Stopwatch.Frequency;
        if (elapsedSeconds <= 0) return;
        lastTimestamp = now;
        if (options.MaxBytesPerSecond > 0)
        {
            byteTokens = Math.Min(options.BurstBytes, byteTokens + elapsedSeconds * options.MaxBytesPerSecond);
        }
        if (options.MaxRequestsPerSecond > 0)
        {
            requestTokens = Math.Min(maximumRequestTokens, requestTokens + elapsedSeconds * options.MaxRequestsPerSecond);
        }
    }
}
