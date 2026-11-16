namespace DshRemote.Core;

/// <summary>
/// Exponential reconnect backoff (defaults: 500 ms → 10 s, factor 2, ±20 % jitter).
/// A successful connection resets the attempt counter.
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>Delay of the first retry.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for any single delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Multiplier applied per attempt.</summary>
    public double Factor { get; init; } = 2.0;

    /// <summary>Maximum consecutive failed attempts; 0 means retry forever.</summary>
    public int MaxAttempts { get; init; } = 12;

    /// <summary>Jitter source returning [0,1). Defaults to <see cref="Random.Shared"/>.</summary>
    public Func<double> Jitter { get; init; } = () => Random.Shared.NextDouble();

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (1-based).
    /// Jitter is applied multiplicatively in [0.8, 1.2) and the result is capped at <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }

        var rawMs = InitialDelay.TotalMilliseconds * Math.Pow(Factor, attempt - 1);
        if (double.IsNaN(rawMs) || rawMs > MaxDelay.TotalMilliseconds)
        {
            rawMs = MaxDelay.TotalMilliseconds;
        }

        var jitter = Jitter();
        if (double.IsNaN(jitter))
        {
            jitter = 0.5;
        }

        jitter = Math.Clamp(jitter, 0.0, 1.0);
        var jittered = rawMs * (0.8 + 0.4 * jitter);
        var capped = Math.Min(jittered, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(1, capped));
    }

    /// <summary>True when another attempt is allowed after <paramref name="failedAttempts"/> consecutive failures.</summary>
    public bool CanRetry(int failedAttempts) => MaxAttempts <= 0 || failedAttempts <= MaxAttempts;
}
