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

public sealed class MessageRoutingPolicyTests
{
    private readonly MessageRoutingPolicy _policy = new();

    [Theory]
    [InlineData("Inspect the current Grasshopper canvas status.")]
    [InlineData("현재 캔버스 상태를 확인해줘.")]
    public void AutoRoutesInspectionToReadFast(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.ReadOnly, route.TaskClass);
        Assert.Equal(ModelProfile.ReadFast, route.EffectiveProfile);
    }

    [Theory]
    [InlineData("Move component 5 by 20 pixels.")]
    [InlineData("Wire component A output to component B input.")]
    [InlineData("컴포넌트 A를 오른쪽으로 이동해줘.")]
    public void AutoRoutesDeterministicCanvasEditsToFastSafe(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.SimpleDeterministicWrite, route.TaskClass);
        Assert.Equal(ModelProfile.FastSafe, route.EffectiveProfile);
    }

    [Theory]
    [InlineData("Update the Python script and change its output schema.")]
    [InlineData("Delete the baked Brep geometry.")]
    [InlineData("Move that thing somewhere roughly over there.")]
    [InlineData("복잡한 메시 토폴로지를 수정해줘.")]
    public void ComplexOrAmbiguousWorkRequiresHighAssurance(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.ComplexWrite, route.TaskClass);
        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
    }

    [Theory]
    [InlineData("Create an adjustable Grasshopper cylinder controlled by diameter and height.")]
    [InlineData("그래스호퍼에 지름과 높이를 조정할 수 있는 실린더 만들어줘.")]
    public void StructuralParametricBuildRequiresHighAssurance(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.ComplexWrite, route.TaskClass);
        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
    }

    [Theory]
    [InlineData("Move a Grasshopper component 20 pixels to the right.")]
    [InlineData("그래스호퍼 컴포넌트를 오른쪽으로 이동해줘.")]
    public void ExplicitGrasshopperContextDoesNotEscalateSimpleMove(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.SimpleDeterministicWrite, route.TaskClass);
        Assert.Equal(ModelProfile.FastSafe, route.EffectiveProfile);
    }

    [Fact]
    public void LargeContextRequiresHighAssurance()
    {
        var route = _policy.Route($"Analyze this modeling request: {new string('x', 4_000)}", "auto");

        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
    }

    [Theory]
    [InlineData("Recover from the runtime failure and fix the broken component.")]
    [InlineData("이 에러를 디버그하고 복구해줘.")]
    public void FailureLanguageRequiresRecovery(string message)
    {
        var route = _policy.Route(message, "auto");

        Assert.Equal(TaskClass.Recovery, route.TaskClass);
        Assert.Equal(ModelProfile.Recovery, route.EffectiveProfile);
    }

    [Fact]
    public void ManualDeepPreferenceNeverDowngradesForSimpleRead()
    {
        var route = _policy.Route("Inspect the canvas.", "high-assurance");

        Assert.Equal(ModelProfile.ReadFast, route.ClassifiedProfile);
        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
        Assert.True(route.Escalated);
    }

    [Fact]
    public void ManualFastPreferenceIsEscalatedForPythonWork()
    {
        var route = _policy.Route("Edit the Python script.", "fast-safe");

        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
        Assert.True(route.Escalated);
    }

    [Theory]
    [InlineData("Continue with it.")]
    [InlineData("그대로 계속 진행해줘.")]
    public void ShortContextDependentFollowUpRetainsPriorStrongFloor(string message)
    {
        var route = _policy.Route(message, "auto", ModelProfile.HighAssurance);

        Assert.Equal(ModelProfile.HighAssurance, route.EffectiveProfile);
        Assert.True(route.Escalated);
        Assert.Contains("prior", route.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitUnrelatedReadMayDowngradeAfterPriorStrongTurn()
    {
        var route = _policy.Route(
            "List the current Rhino objects.",
            "auto",
            ModelProfile.HighAssurance);

        Assert.Equal(ModelProfile.ReadFast, route.EffectiveProfile);
    }

    [Fact]
    public void UnknownPersistedPreferenceDoesNotFailOpen()
    {
        Assert.Throws<ArgumentException>(() => _policy.Route("Inspect the canvas.", "mystery"));
    }
}

public sealed class ModelSelectorTests
{
    private static readonly ModelView Nano = new(
        "gpt-nano", "gpt-nano", "GPT Nano", "", false, ["minimal", "low"]);
    private static readonly ModelView Mini = new(
        "gpt-mini", "gpt-mini", "GPT Mini", "", false, ["low", "medium"]);
    private static readonly ModelView Strong = new(
        "gpt-codex", "gpt-codex", "GPT Codex", "", true, ["low", "medium", "high", "xhigh"]);

    private readonly MessageRoutingPolicy _policy = new();

    [Fact]
    public async Task FastSafeSelectsAQualifiedCompactModel()
    {
        var selector = CreateSelector([Nano, Mini, Strong]);
        var route = _policy.Route("Move component A.", "auto");

        var selection = await selector.SelectAsync(route, null, CancellationToken.None);

        Assert.Equal("gpt-mini", selection.Model);
        Assert.Equal("low", selection.Effort);
    }

    [Fact]
    public async Task HighAssuranceSelectsStrongCatalogModelAndHighEffort()
    {
        var selector = CreateSelector([Nano, Mini, Strong]);
        var route = _policy.Route("Edit the Python schema.", "auto");

        var selection = await selector.SelectAsync(route, null, CancellationToken.None);

        Assert.Equal("gpt-codex", selection.Model);
        Assert.Equal("xhigh", selection.Effort);
        Assert.Equal(ModelProfile.HighAssurance, selection.EffectiveProfile);
    }

    [Fact]
    public async Task WeakPinnedModelIsRejectedWhenTaskEscalates()
    {
        var selector = CreateSelector([Mini, Strong]);
        var route = _policy.Route("Delete the geometry topology.", "fast-safe");

        var exception = await Assert.ThrowsAsync<ModelRoutingException>(
            () => selector.SelectAsync(route, "gpt-mini", CancellationToken.None));

        Assert.Contains("below", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Edit the Python code.")]
    [InlineData("Recover from this runtime error.")]
    public async Task CatalogFailureFailsClosedForStrongRoutes(string message)
    {
        var selector = new ModelSelector(
            new StubModelCatalog(_ => throw new IOException("catalog offline")),
            NullLogger<ModelSelector>.Instance);
        var route = _policy.Route(message, "auto");

        var exception = await Assert.ThrowsAsync<ModelRoutingException>(
            () => selector.SelectAsync(route, null, CancellationToken.None));

        Assert.Contains("fails closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonStrongRouteMayUseAppServerDefaultWhenCatalogIsUnavailable()
    {
        var selector = new ModelSelector(
            new StubModelCatalog(_ => throw new IOException("catalog offline")),
            NullLogger<ModelSelector>.Instance);
        var route = _policy.Route("Inspect the canvas.", "auto");

        var selection = await selector.SelectAsync(route, null, CancellationToken.None);

        Assert.Null(selection.Model);
        Assert.Equal("minimal", selection.Effort);
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

    [Fact]
    public async Task UnknownPinnedModelIsRejectedWhenCatalogIsAvailable()
    {
        var selector = CreateSelector([Strong]);
        var route = _policy.Route("Inspect the canvas.", "auto");

        await Assert.ThrowsAsync<ModelRoutingException>(
            () => selector.SelectAsync(route, "not-in-catalog", CancellationToken.None));
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
    public void ConcurrentUpdatesPublishWholeSnapshots()
    {
        var sessionId = Guid.NewGuid();
        var state = new EffectiveModelState();
        var route = new MessageRoutingPolicy().Route("Inspect the canvas.", "auto");

        Parallel.For(0, 1_000, index =>
            state.RecordSuccess(
                sessionId,
                route,
                new ModelSelection($"model-{index}", "low", route.EffectiveProfile, route.Rationale)));

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
    public async Task ProjectionShowsActualEffectiveSelectionInsteadOfPersistedPin()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest(
            "Routing session", ModelProfile: "auto", Model: "old-preference"));
        var routing = new MessageRoutingPolicy();
        var route = routing.Route("Inspect the canvas.", "auto");
        var effective = new EffectiveModelState();
        effective.RecordSuccess(
            session.Id,
            route,
            new ModelSelection("actual-model", "minimal", route.EffectiveProfile, "selected for inspection"));
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
        Assert.Equal("minimal", projectedSession.GetProperty("reasoning").GetString());
        Assert.Equal("ReadFast", projectedSession.GetProperty("effectiveProfile").GetString());
        Assert.NotEqual("old-preference", projectedSession.GetProperty("effectiveModel").GetString());
    }
}
