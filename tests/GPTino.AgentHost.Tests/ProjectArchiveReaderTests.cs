using System.Text;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class ProjectArchiveReaderTests
{
    private const string Fingerprint = "00AA11BB22CC33DD";

    [Fact]
    public async Task EmptyParentProducesEmptyListing()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        Directory.CreateDirectory(parent);
        var reader = new ProjectArchiveReader(parent, directory.GetPath("missing-current-root"));

        Assert.Empty(await reader.ListProjectsAsync());
    }

    [Fact]
    public async Task ListingReadsManifestSessionsAndMessageCounts()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        var root = Path.Combine(parent, Fingerprint);
        WriteManifest(root, "Tower Study");
        var store = new SessionStore(Path.Combine(root, "runtime.db"));
        await store.InitializeAsync();
        var facade = await store.CreateSessionAsync(new CreateSessionRequest("Facade"));
        var wires = await store.CreateSessionAsync(new CreateSessionRequest("Wires"));
        await store.AppendMessageAsync(facade.Id, "user", "Rationalize the facade.");
        await store.AppendMessageAsync(facade.Id, "assistant", "Reduced to four panel families.", phase: "commit");
        await store.AppendMessageAsync(wires.Id, "user", "Reconnect the staged sockets.");

        var reader = new ProjectArchiveReader(parent, directory.GetPath("elsewhere-current-root"));
        var projects = await reader.ListProjectsAsync();

        var project = Assert.Single(projects);
        Assert.Equal(Fingerprint, project.Fingerprint);
        Assert.Equal("Tower Study", project.ProjectName);
        Assert.Equal("C:\\models\\Tower.3dm", project.RhinoFile);
        Assert.Equal("C:\\models\\Facade.gh", project.GrasshopperFile);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T08:30:00+00:00"), project.CreatedAt);
        Assert.True(project.Available);
        Assert.False(project.Current);
        Assert.Equal(2, project.SessionCount);
        Assert.Collection(
            project.Sessions,
            session =>
            {
                Assert.Equal(facade.Id, session.Id);
                Assert.Equal("Facade", session.Name);
                Assert.Equal(SessionStates.Idle, session.State);
                Assert.Equal(2, session.MessageCount);
            },
            session =>
            {
                Assert.Equal(wires.Id, session.Id);
                Assert.Equal("Wires", session.Name);
                Assert.Equal(1, session.MessageCount);
            });
        Assert.Equal(project.Sessions.Max(session => session.UpdatedAt), project.LastActivityAt);
    }

    [Fact]
    public async Task ReadMessagesReturnsTheNewestWindowInAscendingOrder()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        var store = new SessionStore(Path.Combine(parent, Fingerprint, "runtime.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Chat"));
        await store.AppendMessageAsync(session.Id, "user", "first");
        await store.AppendMessageAsync(session.Id, "assistant", "second", phase: "draft");
        await store.AppendMessageAsync(session.Id, "system", "third");

        var reader = new ProjectArchiveReader(parent, directory.GetPath("elsewhere-current-root"));
        var window = await reader.ReadMessagesAsync(Fingerprint, session.Id, limit: 2);

        Assert.Equal(["second", "third"], window.Select(message => message.Content));
        Assert.Equal(["assistant", "system"], window.Select(message => message.Role));
        Assert.Equal(["draft", null], window.Select(message => message.Phase));
        Assert.True(window[0].Id < window[1].Id);
    }

    [Fact]
    public async Task CurrentRootOutsideTheDefaultParentIsIncludedFlaggedAndReadableByName()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        Directory.CreateDirectory(parent);
        var current = directory.GetPath("custom-data-root");
        var store = new SessionStore(Path.Combine(current, "runtime.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Live"));
        await store.AppendMessageAsync(session.Id, "user", "hello from the live root");

        var reader = new ProjectArchiveReader(parent, current);
        var project = Assert.Single(await reader.ListProjectsAsync());

        Assert.True(project.Current);
        Assert.True(project.Available);
        Assert.Equal("custom-data-root", project.Fingerprint);
        var message = Assert.Single(await reader.ReadMessagesAsync("custom-data-root", session.Id));
        Assert.Equal("hello from the live root", message.Content);
    }

    [Fact]
    public async Task GarbageRootIsReportedUnavailableWithoutFailingTheListing()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        var store = new SessionStore(Path.Combine(parent, Fingerprint, "runtime.db"));
        await store.InitializeAsync();
        await store.CreateSessionAsync(new CreateSessionRequest("Healthy"));
        var garbageRoot = Path.Combine(parent, "FFFFFFFFFFFFFFFF");
        WriteManifest(garbageRoot, "Damaged Project");
        File.WriteAllBytes(
            Path.Combine(garbageRoot, "runtime.db"),
            Encoding.UTF8.GetBytes("this is not a sqlite database and never will be"));

        var reader = new ProjectArchiveReader(parent, directory.GetPath("elsewhere-current-root"));
        var projects = await reader.ListProjectsAsync();

        Assert.Equal(2, projects.Count);
        var unavailable = Assert.Single(projects, project => project.Fingerprint == "FFFFFFFFFFFFFFFF");
        Assert.False(unavailable.Available);
        Assert.Equal("Damaged Project", unavailable.ProjectName);
        Assert.Null(unavailable.LastActivityAt);
        Assert.Empty(unavailable.Sessions);
        var healthy = Assert.Single(projects, project => project.Fingerprint == Fingerprint);
        Assert.True(healthy.Available);
        Assert.Equal(1, healthy.SessionCount);
    }

    [Fact]
    public async Task ReadMessagesValidatesFingerprintsAndReturnsNotFoundForUnknownTargets()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        var store = new SessionStore(Path.Combine(parent, Fingerprint, "runtime.db"));
        await store.InitializeAsync();
        await store.CreateSessionAsync(new CreateSessionRequest("Only"));

        var reader = new ProjectArchiveReader(parent, directory.GetPath("elsewhere-current-root"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.ReadMessagesAsync("..", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.ReadMessagesAsync("..\\..\\Windows\\System32", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.ReadMessagesAsync("not-a-fingerprint", Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => reader.ReadMessagesAsync("0123456789ABCDEF", Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => reader.ReadMessagesAsync(Fingerprint, Guid.NewGuid()));
    }

    private static void WriteManifest(string rootPath, string projectName)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, "context"));
        File.WriteAllText(
            Path.Combine(rootPath, "context", "project.json"),
            $$"""
            {
              "schema": "gptino-context-v1",
              "projectId": "8c9f3d7e-4a4b-4f6d-9a2e-0f4b1c2d3e4f",
              "projectName": "{{projectName}}",
              "rhinoFile": "C:\\models\\Tower.3dm",
              "grasshopperFile": "C:\\models\\Facade.gh",
              "createdAt": "2026-07-01T08:30:00+00:00"
            }
            """);
    }
}
