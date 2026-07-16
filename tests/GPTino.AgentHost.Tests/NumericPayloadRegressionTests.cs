using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class NumericPayloadRegressionTests
{
    [Fact]
    public async Task SetGroupPreservesSignedArgbIntegerInFrozenPayload()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("ARGB regression"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.GrasshopperGroup, groupId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "signed-argb.json",
            new
            {
                bridgeOperation = "canvas.setGroup",
                arguments = new
                {
                    operationId = "signed-argb",
                    groupId,
                    name = "Regression group",
                    objectIds = new[] { harness.CanvasObjectId },
                    argbColor = -16_777_216
                }
            });
        var changeSet = CreateChangeSet(
            harness,
            session.Id,
            snapshot.Revision,
            new TypedOperation(
                "signed-argb",
                OperationKind.SetGroup,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                true,
                artifact),
            new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint));

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "signed-argb-key", "Preserve signed ARGB"),
            CancellationToken.None));

        Assert.Equal("queued", submitted.GetProperty("state").GetString());
        Assert.Single(harness.Backend.ReadQueue());
        var frozenPath = FindFrozenPayload(harness, session.Id);
        using var frozen = JsonDocument.Parse(await File.ReadAllBytesAsync(frozenPath));
        var color = frozen.RootElement
            .GetProperty("arguments")
            .GetProperty("argbColor");
        Assert.Equal("-16777216", color.GetRawText());
        Assert.Equal(-16_777_216, color.GetInt32());
    }

    [Fact]
    public async Task SetGroupRejectsExponentFormForIntegerBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Integer shape regression"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.GrasshopperGroup, groupId.ToString("D"));
        var payload = JsonSerializer.Serialize(
            new
            {
                bridgeOperation = "canvas.setGroup",
                arguments = new
                {
                    operationId = "exponent-argb",
                    groupId,
                    name = "Invalid integer group",
                    objectIds = new[] { harness.CanvasObjectId },
                    argbColor = 1
                }
            },
            BridgeProtocol.JsonOptions).Replace(
                "\"argbColor\":1",
                "\"argbColor\":1e0",
                StringComparison.Ordinal);
        const string artifact = "exponent-argb.json";
        await WriteRawPayloadAsync(harness, session.Id, artifact, payload);
        var changeSet = CreateChangeSet(
            harness,
            session.Id,
            snapshot.Revision,
            new TypedOperation(
                "exponent-argb",
                OperationKind.SetGroup,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                true,
                artifact),
            new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "exponent-argb-key", "Reject exponent ARGB"),
                CancellationToken.None));

        Assert.Contains("typed bridge schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task MovePreservesNegativeZeroAndPositiveZeroCannotReuseItsIdempotencyKey()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Negative zero regression"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var payload = JsonSerializer.Serialize(
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = "negative-zero-move",
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.CanvasObjectId] = new { x = 1d, y = 20d }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.CanvasObjectId] = harness.ObjectFingerprint
                    }
                }
            },
            BridgeProtocol.JsonOptions).Replace(
                "\"x\":1",
                "\"x\":-0.0",
                StringComparison.Ordinal);
        const string artifact = "negative-zero.json";
        await WriteRawPayloadAsync(harness, session.Id, artifact, payload);
        var changeSet = CreateChangeSet(
            harness,
            session.Id,
            snapshot.Revision,
            new TypedOperation(
                "negative-zero-move",
                OperationKind.MoveComponent,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                true,
                artifact),
            new ResourceExpectation(resource, harness.ObjectFingerprint));
        var submission = Submission(
            changeSet,
            snapshot.Id,
            "negative-zero-key",
            "Preserve negative zero");

        _ = await harness.Backend.SubmitChangeAsync(
            session,
            submission,
            CancellationToken.None);

        var frozenPath = FindFrozenPayload(harness, session.Id);
        using (var frozen = JsonDocument.Parse(await File.ReadAllBytesAsync(frozenPath)))
        {
            var frozenX = Assert.Single(
                    frozen.RootElement.GetProperty("arguments").GetProperty("pivots").EnumerateObject())
                .Value.GetProperty("x");
            Assert.Equal("-0.0", frozenX.GetRawText());
            Assert.True(IsNegativeZero(frozenX.GetDouble()));
        }

        await harness.WaitUntilAsync(() =>
            responder.Requests.Any(request => request.Operation == "canvas.move"));
        var bridgeMove = Assert.Single(
            responder.Requests,
            request => request.Operation == "canvas.move");
        var bridgeX = Assert.Single(
                bridgeMove.Arguments.GetProperty("pivots").EnumerateObject())
            .Value.GetProperty("x");
        Assert.Equal("-0.0", bridgeX.GetRawText());
        Assert.True(IsNegativeZero(bridgeX.GetDouble()));

        await WriteRawPayloadAsync(
            harness,
            session.Id,
            artifact,
            payload.Replace("\"x\":-0.0", "\"x\":0.0", StringComparison.Ordinal));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                submission,
                CancellationToken.None));

        Assert.Contains("different accepted request", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ChangeSet CreateChangeSet(
        LiveDocumentBackendHarness harness,
        Guid sessionId,
        long revision,
        TypedOperation operation,
        ResourceExpectation writeExpectation) =>
        new(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            sessionId,
            revision,
            null,
            [],
            [],
            [writeExpectation],
            [operation],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey,
        string summary) =>
        JsonSerializer.SerializeToElement(
            new { changeSet, expectedSnapshotId = snapshotId, idempotencyKey, summary },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);

    private static async Task WriteRawPayloadAsync(
        LiveDocumentBackendHarness harness,
        Guid sessionId,
        string artifact,
        string payload)
    {
        var sessionRoot = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            sessionId.ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        await File.WriteAllTextAsync(Path.Combine(sessionRoot, artifact), payload);
    }

    private static string FindFrozenPayload(LiveDocumentBackendHarness harness, Guid sessionId)
    {
        var jobsRoot = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            sessionId.ToString("N"),
            ".gptino-reserved",
            "jobs");
        return Assert.Single(Directory.GetFiles(jobsRoot, "0000.json", SearchOption.AllDirectories));
    }

    private static bool IsNegativeZero(double value) =>
        value == 0d && BitConverter.DoubleToInt64Bits(value) < 0;
}
