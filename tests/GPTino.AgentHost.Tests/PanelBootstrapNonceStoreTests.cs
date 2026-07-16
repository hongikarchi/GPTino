using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class PanelBootstrapNonceStoreTests
{
    [Fact]
    public void BoundNonceCanBeConsumedOnlyOnce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromMinutes(1));

        Assert.True(store.TryIssue(store.ParentCredential, 42, out var nonce));

        Assert.True(store.TryConsume(nonce, 42));
        Assert.False(store.TryConsume(nonce, 42));
    }

    [Fact]
    public void WrongDocumentDoesNotConsumeNonceForBoundDocument()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromMinutes(1));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out var nonce));

        Assert.False(store.TryConsume(nonce, 43));
        Assert.True(store.TryConsume(nonce, 42));
    }

    [Fact]
    public void ExpiredNonceCannotBeConsumed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromSeconds(30));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out var nonce));

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(store.TryConsume(nonce, 42));
    }

    [Fact]
    public void UnboundStoreNeverAcceptsPanelBootstrap()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(null, clock, TimeSpan.FromMinutes(1));

        Assert.False(store.TryIssue(store.ParentCredential, 42, out _));
    }

    [Fact]
    public void MultipleOutstandingNoncesRemainIndependentlySingleUse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromMinutes(1));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out var first));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out var second));

        Assert.NotEqual(first, second);
        Assert.True(store.TryConsume(first, 42));
        Assert.True(store.TryConsume(second, 42));
        Assert.False(store.TryConsume(first, 42));
        Assert.False(store.TryConsume(second, 42));
    }

    [Fact]
    public void SecondPanelOpenCanRequestAndConsumeFreshNonce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromMinutes(1));

        Assert.True(store.TryIssue(store.ParentCredential, 42, out var firstOpen));
        Assert.True(store.TryConsume(firstOpen, 42));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out var reopened));
        Assert.True(store.TryConsume(reopened, 42));
    }

    [Fact]
    public void ParentCredentialAndDocumentBindingAreRequiredToIssueNonce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));
        var store = new PanelBootstrapNonceStore(42, clock, TimeSpan.FromMinutes(1));

        Assert.False(store.TryIssue(new string('A', 64), 42, out _));
        Assert.False(store.TryIssue(store.ParentCredential, 43, out _));
        Assert.True(store.TryIssue(store.ParentCredential, 42, out _));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
