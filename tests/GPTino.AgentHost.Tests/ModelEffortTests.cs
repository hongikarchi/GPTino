using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.Contracts;
using GPTino.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GPTino.AgentHost.Tests;

// Manual reasoning-effort selection (ModelSelector.ResolveDirectAsync) replaced adaptive routing:
// the session's effort is used directly, only clamped to what the chosen model advertises.
public sealed class ModelSelectorTests
{
    private static readonly ModelView Mini = new(
        "gpt-mini", "gpt-mini", "GPT Mini", "", false, ["low", "medium"]);
    private static readonly ModelView Strong = new(
        "gpt-codex", "gpt-codex", "GPT Codex", "", true, ["low", "medium", "high", "xhigh"]);

    [Fact]
    public async Task DirectUsesRequestedEffortOnTheDefaultModel()
    {
        var selector = CreateSelector([Mini, Strong]);

        var selection = await selector.ResolveDirectAsync("high", null, CancellationToken.None);

        Assert.Equal("gpt-codex", selection.Model); // Strong is the catalog default
        Assert.Equal("high", selection.Effort);
    }

    [Fact]
    public async Task DirectHonorsThePinnedModel()
    {
        var selector = CreateSelector([Mini, Strong]);

        var selection = await selector.ResolveDirectAsync("low", "gpt-mini", CancellationToken.None);

        Assert.Equal("gpt-mini", selection.Model);
        Assert.Equal("low", selection.Effort);
    }

    [Fact]
    public async Task DirectClampsEffortToTheModelsAdvertisedRange()
    {
        var selector = CreateSelector([Mini, Strong]);

        // Mini advertises only [low, medium]; xhigh clamps down to the model's highest supported.
        var selection = await selector.ResolveDirectAsync("xhigh", "gpt-mini", CancellationToken.None);

        Assert.Equal("gpt-mini", selection.Model);
        Assert.Equal("medium", selection.Effort);
    }

    [Fact]
    public async Task DirectFallsBackToRequestedEffortWhenCatalogIsUnavailable()
    {
        var selector = new ModelSelector(
            new StubModelCatalog(_ => throw new IOException("catalog offline")),
            NullLogger<ModelSelector>.Instance);

        var selection = await selector.ResolveDirectAsync("high", null, CancellationToken.None);

        Assert.Null(selection.Model);
        Assert.Equal("high", selection.Effort);
    }

    [Fact]
    public async Task CatalogFailureIsRetriedInsteadOfCachedAsEmpty()
    {
        var attempts = 0;
        var catalog = new StubModelCatalog(_ =>
        {
            attempts++;
            return attempts == 1
                ? throw new IOException("temporary failure")
                : Task.FromResult<IReadOnlyList<ModelView>>([Strong]);
        });
        var selector = new ModelSelector(catalog, NullLogger<ModelSelector>.Instance);

        Assert.Empty(await selector.ReadModelsAsync(CancellationToken.None));
        Assert.Single(await selector.ReadModelsAsync(CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    private static ModelSelector CreateSelector(IReadOnlyList<ModelView> models) =>
        new(new StubModelCatalog(_ => Task.FromResult(models)), NullLogger<ModelSelector>.Instance);

    private sealed class StubModelCatalog(
        Func<CancellationToken, Task<IReadOnlyList<ModelView>>> callback) : IModelCatalog
    {
        public Task<IReadOnlyList<ModelView>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            callback(cancellationToken);
    }
}

public sealed class EffectiveModelStateTests
{
    [Fact]
    public void ConcurrentDirectUpdatesPublishWholeSnapshots()
    {
        var sessionId = Guid.NewGuid();
        var state = new EffectiveModelState();

        Parallel.For(0, 1_000, index =>
            state.RecordDirect(
                sessionId,
                new ModelSelection($"model-{index}", "low", ModelProfile.Standard, "direct")));

        Assert.True(state.TryGet(sessionId, out var snapshot));
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.NotNull(snapshot.Model);
        Assert.Equal("low", snapshot.Reasoning);
        Assert.Null(snapshot.Error);
    }
}

public sealed class RuntimeStateProjectorModelTests
{
    [Fact]
    public async Task ProjectionShowsTheActualEffectiveSelection()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest(
            "Effort session", ModelProfile: "high", Model: "old-preference"));
        var effective = new EffectiveModelState();
        effective.RecordDirect(
            session.Id,
            new ModelSelection("actual-model", "high", ModelProfile.Standard, "selected"));
        var options = new AgentHostOptions
        {
            ProjectId = Guid.NewGuid(),
            ProjectDirectory = directory.Path,
            DataDirectory = directory.GetPath("data")
        };
        var projector = new RuntimeStateProjector(
            store,
            options,
            new RuntimeIdentity(options.ProjectId, null, null, directory.Path, DateTimeOffset.UtcNow),
            new RuntimeControl(),
            new DisconnectedDocumentBackend(),
            effective,
            new EventHub());

        var projection = JsonSerializer.SerializeToElement(await projector.BuildAsync());
        var projectedSession = projection.GetProperty("sessions")[0];

        Assert.Equal("actual-model", projectedSession.GetProperty("effectiveModel").GetString());
        Assert.Equal("high", projectedSession.GetProperty("reasoning").GetString());
        Assert.NotEqual("old-preference", projectedSession.GetProperty("effectiveModel").GetString());
    }
}
