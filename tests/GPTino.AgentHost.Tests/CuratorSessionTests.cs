using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The resident curator session's lifecycle contract: exactly one per project, never deletable,
/// outside the draggable priority order.
/// </summary>
public sealed class CuratorSessionTests
{
    [Fact]
    public async Task CuratorCannotBeSoftDeletedOrPurged()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("sessions.db"));
        await store.InitializeAsync();
        var curator = await store.CreateSessionAsync(new CreateSessionRequest("Document care", "curator"));
        var modeler = await store.CreateSessionAsync(new CreateSessionRequest("Modeling"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetSessionDeletedAsync(curator.Id, deleted: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PurgeSessionAsync(curator.Id));

        // Modeler sessions keep their normal lifecycle.
        await store.SetSessionDeletedAsync(modeler.Id, deleted: true);
        await store.SetSessionDeletedAsync(modeler.Id, deleted: false);
        var (sessions, _) = await store.ReadStateAsync();
        Assert.Contains(sessions, session => session.Id == curator.Id);
    }

    [Fact]
    public async Task ReorderExcludesTheCuratorFromExactMembership()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("sessions.db"));
        await store.InitializeAsync();
        var a = await store.CreateSessionAsync(new CreateSessionRequest("A"));
        var curator = await store.CreateSessionAsync(new CreateSessionRequest("Document care", "curator"));
        var b = await store.CreateSessionAsync(new CreateSessionRequest("B"));
        var (_, version) = await store.ReadStateAsync();

        // The panel's Model tab never sends the curator; the exact-membership check must accept
        // a curator-less order instead of rejecting every reorder with a 409.
        await store.ReorderAsync(new[] { b.Id, a.Id }, version);

        var (sessions, _) = await store.ReadStateAsync();
        Assert.Equal(b.Id, sessions[0].Id);
        Assert.Equal(a.Id, sessions[1].Id);
        Assert.Contains(sessions, session => session.Id == curator.Id);
    }
}
