using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void DelayFor_GrowsExponentiallyAndCapsAtMax()
    {
        var policy = new ReconnectPolicy { Jitter = () => 0.5 };

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), policy.DelayFor(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), policy.DelayFor(3));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), policy.DelayFor(4));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), policy.DelayFor(5));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(6));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(20));
    }

    [Fact]
    public void DelayFor_AppliesJitterWithinTwentyPercent()
    {
        var low = new ReconnectPolicy { Jitter = () => 0.0 };
        var high = new ReconnectPolicy { Jitter = () => 0.999999 };
        var baseline = new ReconnectPolicy { Jitter = () => 0.5 };

        Assert.Equal(400, (int)low.DelayFor(1).TotalMilliseconds);
        Assert.Equal(500, (int)baseline.DelayFor(1).TotalMilliseconds);
        Assert.InRange((int)high.DelayFor(1).TotalMilliseconds, 599, 601);

        // Jitter never pushes the delay above the cap.
        Assert.True(low.DelayFor(10) <= TimeSpan.FromSeconds(10));
        Assert.True(high.DelayFor(10) <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CanRetry_RespectsMaxAttempts()
    {
        var bounded = new ReconnectPolicy { MaxAttempts = 3 };
        Assert.True(bounded.CanRetry(1));
        Assert.True(bounded.CanRetry(3));
        Assert.False(bounded.CanRetry(4));

        var unlimited = new ReconnectPolicy { MaxAttempts = 0 };
        Assert.True(unlimited.CanRetry(1));
        Assert.True(unlimited.CanRetry(10_000));
    }
}
