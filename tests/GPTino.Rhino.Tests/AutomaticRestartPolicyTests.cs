using GPTino.Rhino;
using Xunit;

namespace GPTino.Rhino.Tests;

public sealed class AutomaticRestartPolicyTests
{
    [Fact]
    public void TryReserve_BoundsCrossGenerationRestartsWithBackoff()
    {
        var policy = new AutomaticRestartPolicy();

        Assert.True(policy.TryReserve(out var first));
        Assert.True(policy.TryReserve(out var second));
        Assert.True(policy.TryReserve(out var third));
        Assert.False(policy.TryReserve(out var suppressed));

        Assert.Equal(TimeSpan.FromSeconds(1), first);
        Assert.Equal(TimeSpan.FromSeconds(2), second);
        Assert.Equal(TimeSpan.FromSeconds(4), third);
        Assert.Equal(Timeout.InfiniteTimeSpan, suppressed);
        Assert.Equal(AutomaticRestartPolicy.MaximumAttempts, policy.AttemptCount);
        Assert.True(policy.IsSuppressed);
    }

    [Fact]
    public void Reset_ReplenishesBudgetOnlyAtExplicitLifecycleBoundary()
    {
        var policy = new AutomaticRestartPolicy();
        for (var attempt = 0; attempt < AutomaticRestartPolicy.MaximumAttempts + 1; attempt++)
        {
            _ = policy.TryReserve(out _);
        }

        policy.Reset();

        Assert.False(policy.IsSuppressed);
        Assert.Equal(0, policy.AttemptCount);
        Assert.True(policy.TryReserve(out var delay));
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }
}
