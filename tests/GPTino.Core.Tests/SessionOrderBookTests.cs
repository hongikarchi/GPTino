using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class SessionOrderBookTests
{
    [Fact]
    public void InitializeCopiesInputAndStartsAtVersionZero()
    {
        var first = Guid.NewGuid();
        var source = new List<Guid> { first };
        var book = new SessionOrderBook();

        var snapshot = book.Initialize(TestData.ProjectId, source);
        source.Clear();

        Assert.Equal(0, snapshot.Version);
        Assert.Equal([first], snapshot.OrderedSessionIds);
        Assert.Equal([first], book.Read(TestData.ProjectId)!.OrderedSessionIds);
    }

    [Fact]
    public void ReorderUsesCompareAndSwapAndIncrementsVersion()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var book = new SessionOrderBook();
        book.Initialize(TestData.ProjectId, [first, second]);

        var applied = book.TryReorder(new(TestData.ProjectId, 0, [second, first]));
        var stale = book.TryReorder(new(TestData.ProjectId, 0, [first, second]));

        Assert.True(applied.Applied);
        Assert.Equal(1, applied.Snapshot!.Version);
        Assert.Equal([second, first], applied.Snapshot.OrderedSessionIds);
        Assert.Equal(SessionOrderChangeStatus.VersionMismatch, stale.Status);
        Assert.Equal([second, first], stale.Snapshot!.OrderedSessionIds);
    }

    [Fact]
    public void ReorderRejectsDuplicatesAndMembershipChanges()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var book = new SessionOrderBook();
        book.Initialize(TestData.ProjectId, [first, second]);

        var duplicate = book.TryReorder(new(TestData.ProjectId, 0, [first, first]));
        var replacement = book.TryReorder(new(TestData.ProjectId, 0, [first, Guid.NewGuid()]));

        Assert.Equal(SessionOrderChangeStatus.DuplicateSession, duplicate.Status);
        Assert.Equal(SessionOrderChangeStatus.InvalidMembership, replacement.Status);
        Assert.Equal(0, book.Read(TestData.ProjectId)!.Version);
    }

    [Fact]
    public void AppendAndRemoveAreAlsoVersioned()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var book = new SessionOrderBook();
        book.Initialize(TestData.ProjectId, [first]);

        var appended = book.TryAppend(TestData.ProjectId, second, 0);
        var removed = book.TryRemove(TestData.ProjectId, first, 1);

        Assert.Equal([first, second], appended.Snapshot!.OrderedSessionIds);
        Assert.Equal([second], removed.Snapshot!.OrderedSessionIds);
        Assert.Equal(2, removed.Snapshot.Version);
    }
}
