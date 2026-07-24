using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;
using GPTino.WireifyAdapter;
using Microsoft.Extensions.Logging.Abstractions;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class LiveDocumentBackendTests
{
    [Fact]
    public async Task AuthenticatedClientRegistersExactDocumentPair()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            connect: false,
            register: false);
        var invalidClient = new DocumentPipeClient(harness.Endpoint, BridgeSecret.Generate());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await using var rejected = await invalidClient.ConnectAsync(
                "wrong-secret",
                TimeSpan.FromSeconds(5));
        });

        await harness.ConnectAsync();
        var registration = await harness.RegisterAsync();

        Assert.Equal(BridgeMessageKind.Response, registration.Kind);
        Assert.Equal(BridgeMessageTypes.DocumentRegistered, registration.PayloadType);
        Assert.Equal(harness.LastRegistrationMessageId, registration.CorrelationId);
        var payload = registration.DeserializePayload<DocumentRegisteredResponse>();
        Assert.Equal(harness.Target.StableTargetKey(), payload.TargetKey);
        Assert.Equal(harness.Target.Generation, payload.Generation);
        Assert.True(harness.Backend.IsConnected);
        Assert.Equal(harness.Target.Identity, harness.Backend.CurrentTarget?.Identity);
    }

    [Fact]
    public async Task RegistrationRejectsMismatchedProjectTarget()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(register: false);
        var mismatched = harness.Target with { ProjectId = Guid.NewGuid() };

        var response = await harness.RegisterAsync(mismatched);

        Assert.Equal(BridgeMessageKind.Error, response.Kind);
        Assert.Equal("project_mismatch", response.ErrorCode);
        Assert.Equal(harness.LastRegistrationMessageId, response.CorrelationId);
        Assert.False(harness.Backend.IsConnected);
        Assert.Null(harness.Backend.CurrentTarget);
    }

    [Fact]
    public async Task ReRegistrationWithChangedPathsIsAcceptedForSamePair()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        // Simulate a Save As / rename: same live pair (ProjectId, RhinoDoc serial, GH DocumentID), only the
        // file paths change. The AgentHost must accept the re-registration in place instead of rejecting it,
        // so the binding — and all live session/codex state — survives the rename.
        var renamed = harness.Target with
        {
            RhinoPath = Path.Combine(Path.GetTempPath(), "renamed", "TEST 1.3dm"),
            GrasshopperPath = Path.Combine(Path.GetTempPath(), "renamed", "TEST 1.gh"),
        };

        var response = await harness.RegisterAsync(renamed);

        Assert.Equal(BridgeMessageKind.Response, response.Kind);
        Assert.Equal(BridgeMessageTypes.DocumentRegistered, response.PayloadType);
        Assert.True(harness.Backend.IsConnected);
        Assert.Equal(harness.Target.Identity, harness.Backend.CurrentTarget?.Identity);
        Assert.Equal(renamed.RhinoPath, harness.Backend.CurrentTarget?.RhinoPath);
    }

    [Fact]
    public async Task SnapshotRequestCompletesOnlyForMatchingCorrelation()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var read = harness.Backend.ReadSnapshotAsync(EmptyArguments(), CancellationToken.None);
        var requestFrame = await harness.ReceiveAsync();
        var request = requestFrame.DeserializePayload<BridgeOperationRequest>();
        Assert.Equal("canvas.snapshot", request.Operation);

        await harness.SendOperationResponseAsync(
            requestFrame,
            request,
            harness.CreateSnapshot(),
            correlationId: Guid.NewGuid());
        await Assert.ThrowsAsync<TimeoutException>(
            () => read.WaitAsync(TimeSpan.FromMilliseconds(100)));

        await harness.SendOperationResponseAsync(
            requestFrame,
            request,
            harness.CreateSnapshot(),
            correlationId: requestFrame.MessageId);
        var snapshot = ToElement(await read.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, snapshot.GetProperty("revision").GetInt64());
        Assert.Equal(
            harness.Target.GrasshopperDocumentId,
            snapshot.GetProperty("canvas").GetProperty("grasshopperDocumentId").GetGuid());
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyReturnsOriginalQueuedJob()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "same-operation", snapshot.Revision);
        var arguments = Submission(changeSet, snapshot.Id, "request-17", "Move a component");

        var first = ToElement(await harness.Backend.SubmitChangeAsync(session, arguments, CancellationToken.None));
        var replay = ToElement(await harness.Backend.SubmitChangeAsync(session, arguments, CancellationToken.None));

        Assert.Equal(first.GetProperty("jobId").GetGuid(), replay.GetProperty("jobId").GetGuid());
        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
        Assert.Single(harness.Backend.ReadQueue());
    }

    [Fact]
    public async Task EquivalentJsonNumberSpellingsShareTheIdempotencyHash()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Canonical numbers"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "numeric-operation", snapshot.Revision);
        var submission = Submission(changeSet, snapshot.Id, "numeric-key", "Move a component");
        var first = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            submission,
            CancellationToken.None));
        var payloadPath = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"),
            "operation.json");
        var payload = await File.ReadAllTextAsync(payloadPath);
        Assert.Contains("\"x\":10", payload, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            payloadPath,
            payload.Replace("\"x\":10", "\"x\":1e1", StringComparison.Ordinal));

        var replay = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            submission,
            CancellationToken.None));

        Assert.Equal(first.GetProperty("jobId").GetGuid(), replay.GetProperty("jobId").GetGuid());
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task IdempotencyKeyRejectsAChangedAcceptedRequest()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "same-key", snapshot.Revision);
        _ = await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "bound-key", "Original summary"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "bound-key", "Changed summary"),
                CancellationToken.None));

        Assert.Contains("different accepted request", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Backend.ReadQueue());
    }

    [Fact]
    public async Task AcceptedPayloadIsFrozenInJobOwnedStorage()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "freeze-operation", snapshot.Revision);
        _ = await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "freeze-key", "Freeze payload"),
            CancellationToken.None);
        var sessionRoot = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"));
        var frozenPath = Assert.Single(Directory.GetFiles(
            Path.Combine(sessionRoot, ".gptino-reserved", "jobs"),
            "0000.json",
            SearchOption.AllDirectories));
        var before = await File.ReadAllTextAsync(frozenPath);

        await File.WriteAllTextAsync(
            Path.Combine(sessionRoot, "operation.json"),
            "{\"bridgeOperation\":\"canvas.delete\",\"arguments\":{}}");
        var after = await File.ReadAllTextAsync(frozenPath);

        Assert.Equal(before, after);
        Assert.Contains("freeze-operation", after, StringComparison.Ordinal);
        Assert.Contains("canvas.move", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSecondPayloadIsRejectedBeforeAnyOperationCanQueueOrWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var first = await harness.CreateChangeSetAsync(session, "valid-first", snapshot.Revision);
        var sessionRoot = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"));
        await File.WriteAllTextAsync(
            Path.Combine(sessionRoot, "invalid-second.json"),
            "{\"arguments\":{\"operationId\":\"invalid-second\"}}");
        var invalidSecond = first.Operations[0] with
        {
            OperationId = "invalid-second",
            PayloadArtifact = "invalid-second.json"
        };
        var changeSet = first with { Operations = [first.Operations[0], invalidSecond] };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "invalid-batch", "Invalid batch"),
                CancellationToken.None));

        Assert.Contains("bridgeOperation", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task UnsupportedAcceptancePredicateIsRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var original = await harness.CreateChangeSetAsync(session, "unsupported-predicate", snapshot.Revision);
        var changeSet = original with
        {
            AcceptancePredicates =
            [
                new VerificationPredicate(
                    "Reserved output predicate",
                    PredicateKind.OutputEquals,
                    original.WriteSet[0].Resource,
                    "value")
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "unsupported-predicate", "Unsupported predicate"),
                CancellationToken.None));

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task CreateComponentRequiresAndAcceptsExactAbsenceExpectation()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Creator"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var objectId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "create.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = "create-component",
                    objectId,
                    componentTypeId = Guid.NewGuid(),
                    pivot = new { x = 10, y = 20 },
                    nickName = "Created"
                }
            });
        var accepted = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "create-component",
                OperationKind.CreateComponent,
                AdapterOwner.Cordyceps,
                Array.Empty<ResourceAddress>(),
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(accepted, snapshot.Id, "create-absent", "Create component"),
            CancellationToken.None));
        Assert.False(submitted.GetProperty("duplicate").GetBoolean());

        var unsafeExpectation = accepted with
        {
            ChangeSetId = Guid.NewGuid(),
            WriteSet = [new ResourceExpectation(resource, "invented-existing-fingerprint")]
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(unsafeExpectation, snapshot.Id, "create-without-absent", "Unsafe create"),
                CancellationToken.None));
        Assert.Contains("gptino:absent", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperationKind.CreateRhinoObject)]
    [InlineData(OperationKind.BakeGeometry)]
    public async Task ExactGenericRhinoCreatesAcceptAbsenceExpectation(OperationKind kind)
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Rhino creator"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var objectId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.RhinoObject, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "rhino-create.json",
            new
            {
                bridgeOperation = "rhino.upsert",
                arguments = new
                {
                    operationId = "create-rhino-object",
                    objectId,
                    logicalEntityId = "created-object",
                    geometryType = "Point",
                    geometryJson = "{}",
                    attributesJson = "{}",
                    expectedFingerprint = (string?)null
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "create-rhino-object",
                kind,
                AdapterOwner.RhinoBridge,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, $"create-{kind}", "Create Rhino object"),
            CancellationToken.None));

        Assert.False(submitted.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task PayloadTargetMustMatchDeclaredConflictResource()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Mismatch"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var declared = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            Guid.NewGuid().ToString("D"));
        var changeSet = await harness.CreateChangeSetAsync(
            session,
            "mismatched-target",
            snapshot.Revision,
            [new ResourceExpectation(declared, "invented-fingerprint")],
            [declared]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "mismatched-target", "Mismatch"),
                CancellationToken.None));

        Assert.Contains("payload targets", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task PayloadFingerprintMustMatchDeclaredWriteExpectation()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Fingerprint mismatch"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(
            session,
            "mismatched-fingerprint",
            snapshot.Revision,
            payloadExpectedFingerprint: "different-fingerprint");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "mismatched-fingerprint", "Mismatch"),
                CancellationToken.None));

        Assert.Contains("payload fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task NumberSliderValueUsesExactValueResourceAndTypedBridgePayload()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Number Slider"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "number-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "set-diameter",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 10m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "set-diameter",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "set-diameter", "Set diameter to 10"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        var jobView = await harness.ReadJobViewAsync(submitted.GetProperty("jobId").GetGuid());
        Assert.True(
            state == "committed",
            jobView.GetProperty("message").GetString());
        var bridgeRequest = Assert.Single(
            responder.Requests,
            item => string.Equals(item.Operation, "canvas.setNumberSlider", StringComparison.Ordinal));
        Assert.Equal(BridgeOperationAccess.Write, bridgeRequest.Access);
        Assert.Equal(harness.ObjectFingerprint, bridgeRequest.ExpectedFingerprint);
        Assert.Equal(harness.CanvasObjectId, bridgeRequest.Arguments.GetProperty("objectId").GetGuid());
        Assert.Equal(10m, bridgeRequest.Arguments.GetProperty("value").GetDecimal());
        Assert.Equal(0m, bridgeRequest.Arguments.GetProperty("minimum").GetDecimal());
        Assert.Equal(100m, bridgeRequest.Arguments.GetProperty("maximum").GetDecimal());
        Assert.Equal(0, bridgeRequest.Arguments.GetProperty("decimalPlaces").GetInt32());

        // A committed job carries chaining data so the next ChangeSet needs no snapshot_read.
        var committed = jobView.GetProperty("committed");
        Assert.Equal(JsonValueKind.Object, committed.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(committed.GetProperty("snapshotId").GetString()));
        Assert.True(committed.GetProperty("revision").GetInt64() >= snapshot.Revision);
        var committedResource = Assert.Single(committed.GetProperty("resources").EnumerateArray());
        Assert.Equal(
            harness.CanvasObjectId.ToString("D"),
            committedResource.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(committedResource.GetProperty("fingerprint").GetString()));
    }

    [Fact]
    public async Task DistinctNumberSliderWritesAreNotMisclassifiedAsPythonState()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Two sliders"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var firstResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var secondResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.SecondCanvasObjectId.ToString("D"));
        var firstArtifact = await harness.WritePayloadAsync(
            session,
            "first-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "first-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 10m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var secondArtifact = await harness.WritePayloadAsync(
            session,
            "second-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "second-slider",
                    objectId = harness.SecondCanvasObjectId,
                    expectedFingerprint = harness.SecondObjectFingerprint,
                    value = 20m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(firstResource, harness.ObjectFingerprint),
                new ResourceExpectation(secondResource, harness.SecondObjectFingerprint)
            ],
            [
                new TypedOperation("first-slider", OperationKind.SetValue, AdapterOwner.Cordyceps, [], [firstResource], true, firstArtifact),
                new TypedOperation("second-slider", OperationKind.SetValue, AdapterOwner.Cordyceps, [], [secondResource], true, secondArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "two-sliders", "Set two sliders"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        Assert.Equal(["first-slider", "second-slider"], responder.WriteOperationIds);
        Assert.Equal(1, responder.MaximumConcurrentWrites);
    }

    [Fact]
    public async Task OperationCannotDeclareAdditionalUnrelatedWriteTargets()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Extra write"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var target = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var unrelated = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            Guid.NewGuid().ToString("D"));
        var changeSet = await harness.CreateChangeSetAsync(
            session,
            "extra-write",
            snapshot.Revision,
            [
                new ResourceExpectation(target, harness.ObjectFingerprint),
                new ResourceExpectation(unrelated, "invented-fingerprint")
            ],
            [target, unrelated]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "extra-write", "Extra write"),
                CancellationToken.None));

        Assert.Contains("payload targets", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task InvalidNestedTransformShapeIsRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Bad matrix"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var objectId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.RhinoObject, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "bad-transform.json",
            new
            {
                bridgeOperation = "rhino.transform",
                arguments = new
                {
                    operationId = "bad-transform",
                    objectId,
                    expectedFingerprint = "object-fingerprint",
                    matrix = new
                    {
                        m00 = 1,
                        m01 = 0,
                        m02 = 0,
                        m03 = 0,
                        m10 = 0,
                        m11 = 1,
                        m12 = 0,
                        m13 = 0,
                        m20 = 0,
                        m21 = 0,
                        m22 = 1,
                        m23 = 0,
                        m30 = 0,
                        m31 = 0,
                        m32 = 0
                    }
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "bad-transform",
                OperationKind.TransformRhinoObject,
                AdapterOwner.RhinoBridge,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, "object-fingerprint")]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "bad-transform", "Bad matrix"),
                CancellationToken.None));

        Assert.Contains("missing or unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task MissingCanvasPivotCoordinateIsRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Bad pivot"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var objectId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "bad-pivot.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = "bad-pivot",
                    objectId,
                    componentTypeId = Guid.NewGuid(),
                    pivot = new { x = 10 }
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "bad-pivot",
                OperationKind.CreateComponent,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "bad-pivot", "Bad pivot"),
                CancellationToken.None));

        Assert.Contains("missing or unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task MissingRhinoPointCoordinateIsRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Bad point"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var objectId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.RhinoObject, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "bad-point.json",
            new
            {
                bridgeOperation = "rhino.createPrimitive",
                arguments = new
                {
                    operationId = "bad-point",
                    objectId,
                    logicalEntityId = "bad-point",
                    kind = RhinoPrimitiveKind.Point,
                    point = new { location = new { x = 1, y = 2 } }
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "bad-point",
                OperationKind.CreateRhinoPrimitive,
                AdapterOwner.RhinoBridge,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "bad-point", "Bad point"),
                CancellationToken.None));

        Assert.Contains("missing or unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ReadOperationRequiresActualReadSetFingerprint()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reader"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "read.json",
            new
            {
                bridgeOperation = "canvas.inspect",
                arguments = new { objectId = harness.CanvasObjectId }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            Array.Empty<ResourceExpectation>(),
            [new TypedOperation("read-component", OperationKind.Read, AdapterOwner.Cordyceps, [resource], [], true, artifact)],
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "missing-read-expectation", "Read component"),
                CancellationToken.None));

        Assert.Contains("readSet", exception.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ReadOperationCannotDeclareWritesOrWriteSet()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reader"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "read-with-write.json",
            new
            {
                bridgeOperation = "canvas.inspect",
                arguments = new { objectId = harness.CanvasObjectId }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [new TypedOperation("read-with-write", OperationKind.Read, AdapterOwner.Cordyceps, [resource], [resource], true, artifact)],
            [],
            [],
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "read-with-write", "Read component"),
                CancellationToken.None));

        Assert.Contains("cannot declare write", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ReadOnlyChangeSetCannotDeclareAStrayWriteSet()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reader"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "read-with-stray-write-set.json",
            new
            {
                bridgeOperation = "canvas.inspect",
                arguments = new { objectId = harness.CanvasObjectId }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [new TypedOperation(
                "read-with-stray-write-set",
                OperationKind.Read,
                AdapterOwner.Cordyceps,
                [resource],
                [],
                true,
                artifact)],
            [],
            [],
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "read-with-stray-write-set", "Read component"),
                CancellationToken.None));

        Assert.Contains("writeSet contains", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task UpdateRhinoLayerIsReservedUntilLayerInspectionExists()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Layer"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var layerId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.RhinoLayer, layerId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "layer.json",
            new
            {
                bridgeOperation = "rhino.ensureLayer",
                arguments = new
                {
                    operationId = "create-layer",
                    layerId,
                    fullPath = "GPTino::Layer",
                    argbColor = -1,
                    parentLayerId = (Guid?)null
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "create-layer",
                OperationKind.UpdateRhinoLayer,
                AdapterOwner.RhinoBridge,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "reserved-layer", "Create layer"),
                CancellationToken.None));

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Theory]
    [InlineData(OperationKind.Rename)]
    [InlineData(OperationKind.SetSolverState)]
    [InlineData(OperationKind.DocumentGlobal)]
    public async Task ReservedOperationKindsAreRejectedBeforeQueueing(OperationKind kind)
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reserved"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "reserved.json",
            new
            {
                bridgeOperation = "canvas.reserved",
                arguments = new { operationId = "reserved-op", objectId = harness.CanvasObjectId }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "reserved-op",
                kind,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "reserved-op", "Reserved kind"),
                CancellationToken.None));

        Assert.Contains("no safe bridge mapping", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task RestartMarksInterruptedJobRecoveryRequiredWithoutReplayingIt()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "restart-operation", snapshot.Revision);
        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "restart-key", "Interrupted work"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();

        var restarted = await harness.StartRecoveryReaderAsync();
        try
        {
            var recovered = await LiveDocumentBackendHarness.ReadJobViewAsync(restarted, jobId);

            Assert.Equal("recoveryrequired", recovered.GetProperty("state").GetString());
            Assert.Contains(
                "restarted",
                recovered.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(restarted.ReadQueue());
            var problem = Assert.Single(restarted.ReadRecentProblems());
            Assert.Equal(jobId, problem.JobId);
            Assert.Equal(JobState.RecoveryRequired, problem.State);
            Assert.Empty(responder.WriteOperationIds);
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
            restarted.Dispose();
        }
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyAfterRestartReturnsOriginalRecoveryJob()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "durable-duplicate", snapshot.Revision);
        var arguments = Submission(changeSet, snapshot.Id, "durable-key", "Durable work");
        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            arguments,
            CancellationToken.None));

        var restarted = await harness.StartRecoveryReaderAsync();
        try
        {
            var duplicate = ToElement(await restarted.SubmitChangeAsync(
                session,
                arguments,
                CancellationToken.None));

            Assert.Equal(submitted.GetProperty("jobId").GetGuid(), duplicate.GetProperty("jobId").GetGuid());
            Assert.True(duplicate.GetProperty("duplicate").GetBoolean());
            Assert.Equal("recoveryrequired", duplicate.GetProperty("state").GetString());
            Assert.Contains(
                "No operations were replayed",
                duplicate.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.Empty(responder.WriteOperationIds);
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
            restarted.Dispose();
        }
    }

    [Fact]
    public async Task QueuedJobsFollowUserSessionOrderWithOnlyOneActiveWriter()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(automaticSnapshotResponses: 3);
        harness.Backend.SetPaused(true);
        var preferred = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Preferred"));
        var later = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Later"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        _ = await responder.WaitForSnapshotRequestAsync();
        var laterChange = await harness.CreateChangeSetAsync(later, "later-operation", snapshot.Revision);
        var preferredChange = await harness.CreateChangeSetAsync(preferred, "preferred-operation", snapshot.Revision);

        var laterJob = ToElement(await harness.Backend.SubmitChangeAsync(
            later,
            Submission(laterChange, snapshot.Id, "later-key", "Later work"),
            CancellationToken.None)).GetProperty("jobId").GetGuid();
        _ = await responder.WaitForSnapshotRequestAsync();
        var preferredJob = ToElement(await harness.Backend.SubmitChangeAsync(
            preferred,
            Submission(preferredChange, snapshot.Id, "preferred-key", "Preferred work"),
            CancellationToken.None)).GetProperty("jobId").GetGuid();
        _ = await responder.WaitForSnapshotRequestAsync();

        harness.Backend.SetPaused(false);
        var preferredSnapshotRequest = await responder.WaitForSnapshotRequestAsync();
        Assert.Equal("canvas.snapshot", preferredSnapshotRequest.Request.Operation);
        Assert.Equal(preferred.Id.ToString("D"), harness.Backend.WriterSessionId);
        var firstQueue = harness.Backend.ReadQueue();
        Assert.Equal(JobState.Validating, Assert.Single(firstQueue, item => item.SessionId == preferred.Id).State);
        Assert.Equal(JobState.Queued, Assert.Single(firstQueue, item => item.SessionId == later.Id).State);

        await responder.FailAsync(preferredSnapshotRequest, "test_snapshot_stop");
        Assert.Equal("failed", await harness.WaitForJobStateAsync(preferredJob));
        var laterSnapshotRequest = await responder.WaitForSnapshotRequestAsync();
        Assert.Equal("canvas.snapshot", laterSnapshotRequest.Request.Operation);
        Assert.Equal(later.Id.ToString("D"), harness.Backend.WriterSessionId);
        Assert.NotEqual(preferred.Id.ToString("D"), harness.Backend.WriterSessionId);

        await responder.FailAsync(laterSnapshotRequest, "test_snapshot_stop");
        Assert.Equal("failed", await harness.WaitForJobStateAsync(laterJob));
    }

    [Fact]
    public async Task StaleSnapshotIdIsRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "stale-operation", snapshot.Revision);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, "stale-snapshot", "stale-key", "Stale work"),
                CancellationToken.None));

        Assert.Contains("Snapshot changed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task StaleTouchedFingerprintBlocksJobBeforeBridgeWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var changeSet = await harness.CreateChangeSetAsync(
            session,
            "conflicted-operation",
            snapshot.Revision,
            [new ResourceExpectation(resource, "obsolete-fingerprint")],
            [resource],
            payloadExpectedFingerprint: "obsolete-fingerprint");

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "conflict-key", "Conflicted work"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("blocked", state);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task OverlappingQueuedChangesExposeConflictMetadata()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var firstSession = await harness.Store.CreateSessionAsync(new CreateSessionRequest("First"));
        var secondSession = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Second"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var expectations = new[] { new ResourceExpectation(resource, harness.ObjectFingerprint) };
        var firstChange = await harness.CreateChangeSetAsync(
            firstSession,
            "first-conflict",
            snapshot.Revision,
            expectations,
            [resource]);
        var secondChange = await harness.CreateChangeSetAsync(
            secondSession,
            "second-conflict",
            snapshot.Revision,
            expectations,
            [resource]);

        var first = ToElement(await harness.Backend.SubmitChangeAsync(
            firstSession,
            Submission(firstChange, snapshot.Id, "first-conflict-key", "First"),
            CancellationToken.None));
        var second = ToElement(await harness.Backend.SubmitChangeAsync(
            secondSession,
            Submission(secondChange, snapshot.Id, "second-conflict-key", "Second"),
            CancellationToken.None));

        var conflict = Assert.Single(second.GetProperty("conflictsWith").EnumerateArray());
        Assert.Equal(first.GetProperty("jobId").GetGuid(), conflict.GetProperty("jobId").GetGuid());
        Assert.Equal("writewrite", conflict.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ConsecutiveWireifyMutationsUseRollingComponentFingerprints()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        const string f0 = "python-f0";
        const string f1 = "python-f1";
        const string f2 = "python-f2";
        const string f3 = "python-f3";
        var afterByOperation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["python-source"] = f1,
            ["python-schema"] = f2,
            ["python-execute"] = f3,
        };
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Operation == "python.inspect")
            {
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                    afterFingerprint: f0);
            }
            if (afterByOperation.TryGetValue(request.OperationId, out var after))
            {
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: true,
                    new { applied = true },
                    beforeFingerprint: request.ExpectedFingerprint,
                    afterFingerprint: after);
            }
            return null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Python chain"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var componentId = Guid.NewGuid();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            componentId.ToString("D"));
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            componentId.ToString("D"));
        var valueResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            componentId.ToString("D"));
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "python-source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "python-source",
                    componentId,
                    expectedSourceSha256 = "source-v0",
                    source = "print('GPTino')",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var schemaArtifact = await harness.WritePayloadAsync(
            session,
            "python-schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "python-schema",
                    componentId,
                    inputs = Array.Empty<PythonParameter>(),
                    outputs = Array.Empty<PythonParameter>(),
                    preserveIncidentWires = true
                }
            });
        var executeArtifact = await harness.WritePayloadAsync(
            session,
            "python-execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "python-execute",
                    componentId,
                    expireUpstream = false,
                    recomputeDocument = true
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(sourceResource, f0),
                new ResourceExpectation(ioResource, f0),
                new ResourceExpectation(valueResource, f0)
            ],
            [
                new TypedOperation("python-source", OperationKind.UpdatePythonSource, AdapterOwner.Wireify, [], [sourceResource], true, sourceArtifact),
                new TypedOperation("python-schema", OperationKind.SetComponentIo, AdapterOwner.Wireify, [], [ioResource], true, schemaArtifact),
                new TypedOperation("python-execute", OperationKind.ExecutePython, AdapterOwner.Wireify, [], [valueResource], true, executeArtifact)
            ],
            [
                new VerificationPredicate("Final Python fingerprint", PredicateKind.FingerprintEquals, sourceResource, f3),
                new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)
            ],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "python-chain", "Update and execute Python"),
            CancellationToken.None));

        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.True(
            state == "committed",
            jobView.GetProperty("message").GetString());
        var writes = responder.Requests
            .Where(request => request.Access == BridgeOperationAccess.Write)
            .ToArray();
        Assert.Equal(["python-source", "python-schema", "python-execute"], writes.Select(item => item.OperationId));
        Assert.Equal([f0, f1, f2], writes.Select(item => item.ExpectedFingerprint));
    }

    [Fact]
    public async Task FailedFrozenRhinoUpsertPreflightPreventsEarlierBatchWrites()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.CordycepsRhino
            ]);
        var objectId = Guid.NewGuid();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Operation == "rhino.list")
            {
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new RhinoSceneListResult(1, 0, false, null, [], "empty"));
            }
            if (request.Operation == "rhino.validateUpsert")
            {
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new RhinoUpsertValidationResult(
                        request.OperationId,
                        objectId,
                        "Point",
                        ExistingObject: false,
                        ExistingFingerprint: null,
                        IsValid: false));
            }
            return null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Rhino preflight"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var layoutResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var rhinoResource = new ResourceAddress(ResourceKind.RhinoObject, objectId.ToString("D"));
        var moveArtifact = await harness.WritePayloadAsync(
            session,
            "preflight-move.json",
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = "preflight-move",
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.CanvasObjectId] = new { x = 30, y = 40 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.CanvasObjectId] = harness.ObjectFingerprint
                    }
                }
            });
        var rhinoArtifact = await harness.WritePayloadAsync(
            session,
            "preflight-rhino.json",
            new
            {
                bridgeOperation = "rhino.upsert",
                arguments = new
                {
                    operationId = "preflight-rhino",
                    objectId,
                    logicalEntityId = "preflight-rhino",
                    geometryType = "Point",
                    geometryJson = "{}",
                    attributesJson = "{}",
                    expectedFingerprint = (string?)null
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(layoutResource, harness.ObjectFingerprint),
                new ResourceExpectation(rhinoResource, ResourceExpectation.AbsentFingerprint)
            ],
            [
                new TypedOperation("preflight-move", OperationKind.MoveComponent, AdapterOwner.Cordyceps, [], [layoutResource], true, moveArtifact),
                new TypedOperation("preflight-rhino", OperationKind.CreateRhinoObject, AdapterOwner.RhinoBridge, [], [rhinoResource], true, rhinoArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "rhino-preflight", "Reject invalid Rhino JSON"),
            CancellationToken.None));

        Assert.Equal(
            "failed",
            await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid()));
        Assert.Empty(responder.WriteOperationIds);
        var validation = Assert.Single(
            responder.Requests,
            request => request.Operation == "rhino.validateUpsert");
        Assert.Equal(BridgeOperationAccess.Read, validation.Access);
        Assert.Equal("{}", validation.Arguments.GetProperty("geometryJson").GetString());
    }

    [Fact]
    public async Task MismatchedResponseTargetFailsPendingSnapshot()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var read = harness.Backend.ReadSnapshotAsync(EmptyArguments(), CancellationToken.None);
        var requestFrame = await harness.ReceiveAsync();
        var request = requestFrame.DeserializePayload<BridgeOperationRequest>();

        await harness.SendOperationResponseAsync(
            requestFrame,
            request,
            harness.CreateSnapshot(),
            correlationId: requestFrame.MessageId,
            target: harness.Target.NextGeneration());

        await Assert.ThrowsAsync<DocumentTargetMismatchException>(
            () => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisconnectFailsPendingRequestAndClearsTarget()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var read = harness.Backend.ReadSnapshotAsync(EmptyArguments(), CancellationToken.None);
        _ = await harness.ReceiveAsync();

        await harness.DisconnectClientAsync();

        await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        await harness.WaitUntilAsync(() => !harness.Backend.IsConnected);
        Assert.Null(harness.Backend.CurrentTarget);
    }

    [Fact]
    public async Task RegistrationAcceptsSiblingTargetsSharingOneProject()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sibling = harness.CreateSiblingTarget();

        var response = await harness.RegisterAsync(sibling);

        Assert.Equal(BridgeMessageKind.Response, response.Kind);
        Assert.Equal(BridgeMessageTypes.DocumentRegistered, response.PayloadType);
        Assert.True(harness.Backend.IsConnected);
        // The DEFAULT target stays the first registered document, so every legacy single-document
        // consumer (CurrentTarget/CurrentRevision/projector fallback) keeps today's behavior.
        Assert.Equal(harness.Target.Identity, harness.Backend.CurrentTarget?.Identity);
        var docs = harness.Backend.RegisteredGrasshopperDocuments;
        Assert.Equal(2, docs.Count);
        Assert.Equal(harness.Target.GrasshopperPath, docs[0].File);
        Assert.Equal(sibling.GrasshopperPath, docs[1].File);
        Assert.Equal(AgentHostOptions.ComputeDocumentKey(harness.Target.GrasshopperPath), docs[0].Id);
        Assert.Equal(AgentHostOptions.ComputeDocumentKey(sibling.GrasshopperPath), docs[1].Id);
        Assert.NotEqual(docs[0].Id, docs[1].Id);
    }

    [Fact]
    public async Task UnboundSessionSubmitFailsListingDocsWhenTwoDocumentsRegistered()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sibling = harness.CreateSiblingTarget();
        _ = await harness.RegisterAsync(sibling);
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Unbound"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "unbound-operation", snapshot.Revision);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "unbound-key", "Unbound work"),
                CancellationToken.None));

        // The failure is actionable: it lists every registered document by file name AND docKey.
        Assert.Contains("definition.gh", exception.Message, StringComparison.Ordinal);
        Assert.Contains("definition-b.gh", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            AgentHostOptions.ComputeDocumentKey(harness.Target.GrasshopperPath),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            AgentHostOptions.ComputeDocumentKey(sibling.GrasshopperPath),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task SessionBoundToUnregisteredDocumentFailsListingRegisteredDocs()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(
            new CreateSessionRequest("Ghost", GrasshopperDoc: "deadbeefdeadbeef"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "ghost-operation", snapshot.Revision);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "ghost-key", "Ghost work"),
                CancellationToken.None));

        Assert.Contains("deadbeefdeadbeef", exception.Message, StringComparison.Ordinal);
        Assert.Contains("definition.gh", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            AgentHostOptions.ComputeDocumentKey(harness.Target.GrasshopperPath),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task SessionBoundToSecondDocumentRoutesReadsAndWritesToIt()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sibling = harness.CreateSiblingTarget();
        _ = await harness.RegisterAsync(sibling);
        var siblingDocKey = AgentHostOptions.ComputeDocumentKey(sibling.GrasshopperPath);
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(
            new CreateSessionRequest("Bound", GrasshopperDoc: siblingDocKey));

        // Reads route by the binding: snapshot state is per document and independent of the default.
        var boundRead = ToElement(await harness.Backend.ReadSnapshotAsync(
            session,
            EmptyArguments(),
            CancellationToken.None));
        Assert.Equal(
            sibling.GrasshopperDocumentId,
            boundRead.GetProperty("canvas").GetProperty("grasshopperDocumentId").GetGuid());
        Assert.Equal(1, boundRead.GetProperty("revision").GetInt64());
        var defaultSnapshot = await harness.CaptureSnapshotViewAsync();
        Assert.Equal(1, defaultSnapshot.Revision);
        Assert.NotEqual(defaultSnapshot.Id, boundRead.GetProperty("snapshotId").GetString());

        var changeSet = await harness.CreateChangeSetAsync(
            session,
            "bound-operation",
            boundRead.GetProperty("revision").GetInt64());
        harness.Backend.SetPaused(true);
        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(
                changeSet,
                boundRead.GetProperty("snapshotId").GetString()!,
                "bound-key",
                "Bound work"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var queued = Assert.Single(harness.Backend.ReadQueue());
        Assert.Equal(siblingDocKey, queued.TargetDoc);

        harness.Backend.SetPaused(false);
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        var writes = responder.Observed
            .Where(item => item.Request.Access == BridgeOperationAccess.Write)
            .ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, item =>
            Assert.Equal(sibling.GrasshopperDocumentId, item.Frame.Target!.GrasshopperDocumentId));
    }

    [Fact]
    public async Task SelectionSurfacesMostRecentTargetAndRoutesPerDoc()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sibling = harness.CreateSiblingTarget();
        _ = await harness.RegisterAsync(sibling);
        var defaultDocKey = AgentHostOptions.ComputeDocumentKey(harness.Target.GrasshopperPath);
        var siblingDocKey = AgentHostOptions.ComputeDocumentKey(sibling.GrasshopperPath);
        await using var responder = harness.StartResponder();
        var rhinoId = Guid.NewGuid();
        var canvasObject = new GrasshopperSelectedObject(Guid.NewGuid(), "Python 3 Script", "Grid");

        // One plugin fan-out burst: the sibling's event names canvas objects; the default
        // target's echo shares the Rhino ids, has no canvas objects, and arrives LAST.
        await harness.SendSelectionChangedAsync(
            sibling,
            new SelectionChangedEvent([rhinoId], null, DateTimeOffset.UtcNow, [canvasObject]));
        await harness.SendSelectionChangedAsync(
            harness.Target,
            new SelectionChangedEvent([rhinoId], null, DateTimeOffset.UtcNow));
        await harness.WaitUntilAsync(() =>
            harness.Backend.SelectionFor(siblingDocKey) is not null &&
            harness.Backend.SelectionFor(defaultDocKey) is not null);

        // The surfaced selection is the burst's canvas-bearing event, attributed to its doc —
        // not the default target's Rhino-only echo that arrived later.
        Assert.Equal(siblingDocKey, harness.Backend.CurrentSelectionDocId);
        var surfaced = harness.Backend.CurrentSelection;
        Assert.NotNull(surfaced);
        Assert.Equal(canvasObject.ObjectId, Assert.Single(surfaced!.GrasshopperObjects!).ObjectId);
        Assert.Equal(rhinoId, Assert.Single(surfaced.RhinoObjectIds));

        // Per-doc lookups route by docKey and never throw: unknown or ambiguous yields null.
        Assert.Single(harness.Backend.SelectionFor(siblingDocKey)!.GrasshopperObjects!);
        Assert.Null(harness.Backend.SelectionFor(defaultDocKey)!.GrasshopperObjects);
        Assert.Null(harness.Backend.SelectionFor(null));
        Assert.Null(harness.Backend.SelectionFor("deadbeefdeadbeef"));

        // The canvas digest routes the same way: captured for the default doc only, and a null
        // docKey stays null while two documents are registered.
        _ = await harness.CaptureSnapshotViewAsync();
        var digest = harness.Backend.CanvasDigestFor(defaultDocKey);
        Assert.NotNull(digest);
        Assert.Equal(1, digest!.ComponentCount);
        Assert.Null(harness.Backend.CanvasDigestFor(siblingDocKey));
        Assert.Null(harness.Backend.CanvasDigestFor(null));
    }

    [Fact]
    public async Task SaveAsRemapsBindingsQueuedJobsAndHistoryToTheNewDocKey()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var oldDocKey = AgentHostOptions.ComputeDocumentKey(harness.Target.GrasshopperPath);
        var session = await harness.Store.CreateSessionAsync(
            new CreateSessionRequest("Bound", GrasshopperDoc: oldDocKey));
        string? firstCommit;
        Guid queuedJobId;
        var firstResponder = harness.StartResponder();
        try
        {
            // Commit once so managed history exists under the OLD docKey…
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var firstChange = await harness.CreateChangeSetAsync(session, "before-rename", snapshot.Revision);
            var first = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(firstChange, snapshot.Id, "before-rename-key", "Before rename"),
                CancellationToken.None));
            Assert.Equal("committed", await harness.WaitForJobStateAsync(first.GetProperty("jobId").GetGuid()));
            firstCommit = ToElement(await harness.Backend.ReadSnapshotAsync(EmptyArguments(), CancellationToken.None))
                .GetProperty("gitCommit").GetString();
            Assert.NotNull(firstCommit);

            // …and freeze a second job to the OLD docKey while the writer is paused.
            harness.Backend.SetPaused(true);
            var beforeRename = await harness.CaptureSnapshotViewAsync();
            var secondChange = await harness.CreateChangeSetAsync(session, "across-rename", beforeRename.Revision);
            var second = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(secondChange, beforeRename.Id, "across-rename-key", "Across rename"),
                CancellationToken.None));
            queuedJobId = second.GetProperty("jobId").GetGuid();
            Assert.Equal(oldDocKey, Assert.Single(harness.Backend.ReadQueue()).TargetDoc);
        }
        finally
        {
            await firstResponder.DisposeAsync();
        }

        // Save As: the SAME StableTargetKey re-registers with a changed .gh path.
        var renamed = harness.Target with
        {
            GrasshopperPath = Path.Combine(Path.GetTempPath(), "renamed", "definition (renamed).gh"),
        };
        var response = await harness.RegisterAsync(renamed);
        Assert.Equal(BridgeMessageTypes.DocumentRegistered, response.PayloadType);
        var newDocKey = AgentHostOptions.ComputeDocumentKey(renamed.GrasshopperPath);
        Assert.NotEqual(oldDocKey, newDocKey);

        // The persisted session binding followed the rename and resolves to the renamed doc…
        var rebound = await harness.Store.FindSessionAsync(session.Id);
        Assert.Equal(newDocKey, rebound!.GrasshopperDoc);
        // …the queued job was re-keyed in memory…
        Assert.Equal(newDocKey, Assert.Single(harness.Backend.ReadQueue()).TargetDoc);
        // …and the history folder moved with the rename instead of forking.
        var dataRoot = harness.Options.ResolveDataDirectory();
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "histories", oldDocKey)));
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "histories", newDocKey)));

        // The pre-rename queued job still executes, and its commit lands in the moved history.
        await using var secondResponder = harness.StartResponder();
        harness.Backend.SetPaused(false);
        var state = await harness.WaitForJobStateAsync(queuedJobId);
        var jobView = await harness.ReadJobViewAsync(queuedJobId);
        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        var afterRename = ToElement(await harness.Backend.ReadSnapshotAsync(
            rebound,
            EmptyArguments(),
            CancellationToken.None));
        var secondCommit = afterRename.GetProperty("gitCommit").GetString();
        Assert.NotNull(secondCommit);
        Assert.NotEqual(firstCommit, secondCommit);
    }

    private static JsonElement EmptyArguments() =>
        JsonSerializer.SerializeToElement(new { }, BridgeProtocol.JsonOptions);

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
}

internal sealed class LiveDocumentBackendHarness : IAsyncDisposable
{
    private readonly TestDirectory _directory;
    private DocumentPipeConnection? _connection;
    private bool _disposed;

    private LiveDocumentBackendHarness(
        TestDirectory directory,
        SessionStore store,
        LiveDocumentBackend backend,
        AgentHostOptions options,
        PipeEndpoint endpoint,
        BridgeSecret secret,
        DocumentRuntime target)
    {
        _directory = directory;
        Store = store;
        Backend = backend;
        Options = options;
        Endpoint = endpoint;
        Secret = secret;
        Target = target;
    }

    public SessionStore Store { get; }

    public LiveDocumentBackend Backend { get; }

    public AgentHostOptions Options { get; }

    public PipeEndpoint Endpoint { get; }

    public BridgeSecret Secret { get; }

    public DocumentRuntime Target { get; }

    public Guid CanvasObjectId { get; } = Guid.Parse("9a2c86f6-f2a7-4ee0-b1a0-43e981e70d03");

    public Guid SecondCanvasObjectId { get; } = Guid.Parse("63ef5e65-a239-4b02-89ea-d959fbd68404");

    public string ObjectFingerprint => "object-v1";

    public string SecondObjectFingerprint => "object-v2";

    public bool IncludeNumberSliderValue { get; set; }

    public Guid? LastRegistrationMessageId { get; private set; }

    public static async Task<LiveDocumentBackendHarness> CreateAsync(
        bool connect = true,
        bool register = true,
        IReadOnlyList<BridgeAdapterOwner>? availableAdapters = null)
    {
        var directory = new TestDirectory();
        try
        {
            var projectId = Guid.NewGuid();
            var endpoint = PipeEndpoint.ForProject(projectId.ToString("N"), Environment.ProcessId);
            var secret = BridgeSecret.Generate();
            var options = new AgentHostOptions
            {
                ProjectId = projectId,
                ProjectDirectory = directory.Path,
                DataDirectory = directory.GetPath("data"),
                RhinoPath = directory.GetPath("model.3dm"),
                GrasshopperPath = directory.GetPath("definition.gh"),
                BridgePipe = endpoint.Name
            };
            var target = DocumentRuntimeTarget.Create(
                projectId,
                Environment.ProcessId,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                17,
                Guid.NewGuid(),
                options.RhinoPath,
                options.GrasshopperPath);
            var store = new SessionStore(directory.GetPath("sessions.db"));
            await store.InitializeAsync();
            Environment.SetEnvironmentVariable("GPTINO_BRIDGE_SECRET", secret.ExportBase64());
            LiveDocumentBackend backend;
            try
            {
                backend = new LiveDocumentBackend(
                    store,
                    options,
                    new EventHub(),
                    NullLogger<LiveDocumentBackend>.Instance);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPTINO_BRIDGE_SECRET", null);
            }

            var harness = new LiveDocumentBackendHarness(
                directory,
                store,
                backend,
                options,
                endpoint,
                secret,
                target);
            await backend.StartAsync(CancellationToken.None);
            if (connect)
            {
                await harness.ConnectAsync();
            }
            if (register)
            {
                var response = await harness.RegisterAsync(availableAdapters: availableAdapters);
                if (response.Kind != BridgeMessageKind.Response)
                {
                    throw new InvalidOperationException(
                        $"Test bridge registration failed: {response.ErrorCode ?? response.PayloadType}");
                }
            }
            return harness;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public async Task ConnectAsync()
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("Test client is already connected.");
        }
        _connection = await new DocumentPipeClient(Endpoint, Secret).ConnectAsync(
            "test-rhino",
            TimeSpan.FromSeconds(5));
    }

    public async Task<BridgeFrame> RegisterAsync(
        DocumentRuntime? target = null,
        IReadOnlyList<BridgeAdapterOwner>? availableAdapters = null)
    {
        var connection = RequireConnection();
        var registration = BridgeFrame.Create(
            BridgeMessageKind.Event,
            BridgeMessageTypes.RegisterDocument,
            new RegisterDocumentRequest(
                "test-rhino",
                "1.0.0",
                availableAdapters ?? [BridgeAdapterOwner.CordycepsCanvas]),
            target ?? Target);
        LastRegistrationMessageId = registration.MessageId;
        await connection.SendAsync(registration);
        return await connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task<BridgeFrame> ReceiveAsync() =>
        await RequireConnection().ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Pushes one plugin-style SelectionChanged event for the given target.</summary>
    public Task SendSelectionChangedAsync(DocumentRuntime target, SelectionChangedEvent selection) =>
        RequireConnection().SendAsync(
            BridgeFrame.Create(
                BridgeMessageKind.Event,
                BridgeMessageTypes.SelectionChanged,
                selection,
                target)).AsTask();

    public Task SendOperationResponseAsync(
        BridgeFrame requestFrame,
        BridgeOperationRequest request,
        object result,
        Guid correlationId,
        DocumentRuntime? target = null)
    {
        var response = BridgeFrame.Create(
            BridgeMessageKind.Response,
            BridgeMessageTypes.OperationResponse,
            BridgeOperationResponse.Create(request.OperationId, changed: false, result),
            target ?? Target,
            correlationId);
        return RequireConnection().SendAsync(response).AsTask();
    }

    /// <summary>
    /// A second Grasshopper document of the SAME Rhino document: identical ProjectId, process and
    /// Rhino serial, but its own GH DocumentID and file path — exactly what the plugin registers
    /// when two saved definitions are open against one model.
    /// </summary>
    public DocumentRuntime CreateSiblingTarget(string fileName = "definition-b.gh") =>
        Target with
        {
            GrasshopperDocumentId = Guid.NewGuid(),
            GrasshopperPath = _directory.GetPath(fileName)
        };

    public CanvasSnapshot CreateSnapshot() => CreateSnapshotFor(Target);

    public CanvasSnapshot CreateSnapshotFor(DocumentRuntime target)
    {
        var state = new CanvasObjectState(
            CanvasObjectId,
            Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
            "Point",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            ObjectFingerprint)
        {
            ValueJson = IncludeNumberSliderValue
                ? "{\"kind\":\"numberSlider\",\"value\":5,\"minimum\":0,\"maximum\":100,\"decimalPlaces\":0}"
                : null
        };
        var objects = IncludeNumberSliderValue
            ? new[]
            {
                state,
                new CanvasObjectState(
                    SecondCanvasObjectId,
                    Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
                    "Height",
                    new CanvasPoint(10, 80),
                    new CanvasSize(90, 40),
                    SecondObjectFingerprint)
                {
                    ValueJson = "{\"kind\":\"numberSlider\",\"value\":5,\"minimum\":0,\"maximum\":100,\"decimalPlaces\":0}"
                }
            }
            : [state];
        return new(
            target.GrasshopperDocumentId,
            "document-v1",
            objects,
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    public FakeBridgeResponder StartResponder(
        TimeSpan? writeDelay = null,
        int automaticSnapshotResponses = int.MaxValue,
        Func<BridgeOperationRequest, BridgeOperationResponse?>? responseFactory = null) =>
        FakeBridgeResponder.Start(
            RequireConnection(),
            CreateSnapshotFor,
            writeDelay,
            automaticSnapshotResponses,
            responseFactory);

    public async Task<LiveDocumentBackend> StartRecoveryReaderAsync()
    {
        var recoveryOptions = new AgentHostOptions
        {
            ProjectId = Options.ProjectId,
            ProjectDirectory = Options.ProjectDirectory,
            DataDirectory = Options.DataDirectory,
            RhinoPath = Options.RhinoPath,
            GrasshopperPath = Options.GrasshopperPath,
            BridgePipe = null
        };
        var backend = new LiveDocumentBackend(
            Store,
            recoveryOptions,
            new EventHub(),
            NullLogger<LiveDocumentBackend>.Instance);
        await backend.StartAsync(CancellationToken.None);
        return backend;
    }

    public async Task<(string Id, long Revision)> CaptureSnapshotViewAsync()
    {
        var value = await Backend.ReadSnapshotAsync(
            JsonSerializer.SerializeToElement(new { }, BridgeProtocol.JsonOptions),
            CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
        return (json.GetProperty("snapshotId").GetString()!, json.GetProperty("revision").GetInt64());
    }

    public async Task<ChangeSet> CreateChangeSetAsync(
        SessionRecord session,
        string operationId,
        long revision,
        IReadOnlyList<ResourceExpectation>? writeSet = null,
        IReadOnlyList<ResourceAddress>? operationWrites = null,
        string? payloadExpectedFingerprint = null)
    {
        const string artifactName = "operation.json";
        var defaultResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            CanvasObjectId.ToString("D"));
        var effectiveWrites = operationWrites ?? new[] { defaultResource };
        var effectiveWriteSet = writeSet ??
            new[] { new ResourceExpectation(defaultResource, ObjectFingerprint) };
        var sessionArtifacts = Path.Combine(
            Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"));
        Directory.CreateDirectory(sessionArtifacts);
        var payload = JsonSerializer.Serialize(
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId,
                    pivots = new Dictionary<Guid, object>
                    {
                        [CanvasObjectId] = new { x = 10, y = 20 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [CanvasObjectId] = payloadExpectedFingerprint ?? ObjectFingerprint
                    }
                }
            },
            BridgeProtocol.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(sessionArtifacts, artifactName), payload);
        return new ChangeSet(
            Guid.NewGuid(),
            Target.ProjectId,
            session.Id,
            revision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            effectiveWriteSet,
            [
                new TypedOperation(
                    operationId,
                    OperationKind.MoveComponent,
                    AdapterOwner.Cordyceps,
                    Array.Empty<ResourceAddress>(),
                    effectiveWrites,
                    Reversible: true,
                    artifactName)
            ],
            [
                new VerificationPredicate(
                    "No runtime errors",
                    PredicateKind.RuntimeErrorAbsent,
                    null,
                    null)
            ],
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);
    }

    public async Task<string> WritePayloadAsync(
        SessionRecord session,
        string artifactName,
        object payload)
    {
        var sessionArtifacts = Path.Combine(
            Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"));
        Directory.CreateDirectory(sessionArtifacts);
        await File.WriteAllTextAsync(
            Path.Combine(sessionArtifacts, artifactName),
            JsonSerializer.Serialize(payload, payload.GetType(), BridgeProtocol.JsonOptions));
        return artifactName;
    }

    public ChangeSet CreateCustomChangeSet(
        SessionRecord session,
        long revision,
        TypedOperation operation,
        IReadOnlyList<ResourceExpectation> writeSet) =>
        new(
            Guid.NewGuid(),
            Target.ProjectId,
            session.Id,
            revision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            writeSet,
            [operation],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);

    public async Task<string> WaitForJobStateAsync(Guid jobId)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var json = await ReadJobViewAsync(jobId);
            var state = json.GetProperty("state").GetString()!;
            if (state is "committed" or "blocked" or "failed" or "cancelled" or "recoveryrequired")
            {
                return state;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Job {jobId:D} did not reach a terminal state.");
    }

    public async Task<JsonElement> ReadJobViewAsync(Guid jobId)
        => await ReadJobViewAsync(Backend, jobId);

    public static async Task<JsonElement> ReadJobViewAsync(
        LiveDocumentBackend backend,
        Guid jobId)
    {
        var value = await backend.ReadJobAsync(
            JsonSerializer.SerializeToElement(
                new { jobId = jobId.ToString("D") },
                BridgeProtocol.JsonOptions),
            CancellationToken.None);
        return JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
    }

    public async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition did not become true.");
    }

    public async Task DisconnectClientAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await DisconnectClientAsync();
            await Backend.StopAsync(CancellationToken.None);
            Backend.Dispose();
        }
        finally
        {
            _directory.Dispose();
        }
    }

    private DocumentPipeConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("Test bridge client is not connected.");
}

internal sealed class FakeBridgeResponder : IAsyncDisposable
{
    private readonly DocumentPipeConnection _connection;
    private readonly Func<DocumentRuntime, CanvasSnapshot> _snapshotProvider;
    private readonly TimeSpan _writeDelay;
    private readonly int _automaticSnapshotResponses;
    private readonly Func<BridgeOperationRequest, BridgeOperationResponse?>? _responseFactory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Channel<ObservedBridgeRequest> _snapshotRequests =
        Channel.CreateUnbounded<ObservedBridgeRequest>();
    private readonly object _writesGate = new();
    private readonly List<string> _writeOperationIds = [];
    private readonly List<BridgeOperationRequest> _requests = [];
    private readonly List<ObservedBridgeRequest> _observed = [];
    private readonly Task _loop;
    private int _activeWrites;
    private int _maximumConcurrentWrites;
    private int _snapshotCount;

    private FakeBridgeResponder(
        DocumentPipeConnection connection,
        Func<DocumentRuntime, CanvasSnapshot> snapshotProvider,
        TimeSpan writeDelay,
        int automaticSnapshotResponses,
        Func<BridgeOperationRequest, BridgeOperationResponse?>? responseFactory)
    {
        _connection = connection;
        _snapshotProvider = snapshotProvider;
        _writeDelay = writeDelay;
        _automaticSnapshotResponses = automaticSnapshotResponses;
        _responseFactory = responseFactory;
        _loop = Task.Run(ReceiveLoopAsync);
    }

    public IReadOnlyList<string> WriteOperationIds
    {
        get
        {
            lock (_writesGate)
            {
                return _writeOperationIds.ToArray();
            }
        }
    }

    public int MaximumConcurrentWrites => Volatile.Read(ref _maximumConcurrentWrites);

    public IReadOnlyList<BridgeOperationRequest> Requests
    {
        get
        {
            lock (_writesGate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Every observed operation request with its frame, so tests can assert per-frame targets.</summary>
    public IReadOnlyList<ObservedBridgeRequest> Observed
    {
        get
        {
            lock (_writesGate)
            {
                return _observed.ToArray();
            }
        }
    }

    public static FakeBridgeResponder Start(
        DocumentPipeConnection connection,
        Func<DocumentRuntime, CanvasSnapshot> snapshotProvider,
        TimeSpan? writeDelay = null,
        int automaticSnapshotResponses = int.MaxValue,
        Func<BridgeOperationRequest, BridgeOperationResponse?>? responseFactory = null) =>
        new(
            connection,
            snapshotProvider,
            writeDelay ?? TimeSpan.Zero,
            automaticSnapshotResponses,
            responseFactory);

    public Task<ObservedBridgeRequest> WaitForSnapshotRequestAsync() =>
        _snapshotRequests.Reader.ReadAsync(_stopping.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

    public Task FailAsync(ObservedBridgeRequest observed, string code)
    {
        var failure = new BridgeFailure(code, "Test responder rejected the snapshot.", Retryable: false);
        var response = BridgeFrame.Create(
            BridgeMessageKind.Error,
            "bridge.failure",
            failure,
            observed.Frame.Target!,
            observed.Frame.MessageId) with
        {
            ErrorCode = code
        };
        return _connection.SendAsync(response, _stopping.Token).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or TimeoutException)
        {
        }

        var handlers = _handlers.ToArray();
        if (handlers.Length > 0)
        {
            try
            {
                await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or TimeoutException)
            {
            }
        }
        _stopping.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stopping.IsCancellationRequested && _connection.IsConnected)
        {
            BridgeFrame frame;
            try
            {
                frame = await _connection.ReceiveAsync(_stopping.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException && _stopping.IsCancellationRequested)
            {
                return;
            }

            if (frame.Kind != BridgeMessageKind.Request ||
                !string.Equals(frame.PayloadType, BridgeMessageTypes.OperationRequest, StringComparison.Ordinal))
            {
                continue;
            }
            _handlers.Add(HandleRequestAsync(frame));
        }
    }

    private async Task HandleRequestAsync(BridgeFrame frame)
    {
        var request = frame.DeserializePayload<BridgeOperationRequest>();
        lock (_writesGate)
        {
            _requests.Add(request);
            _observed.Add(new ObservedBridgeRequest(frame, request));
        }
        if (string.Equals(request.Operation, "canvas.snapshot", StringComparison.Ordinal))
        {
            _snapshotRequests.Writer.TryWrite(new ObservedBridgeRequest(frame, request));
            if (Interlocked.Increment(ref _snapshotCount) > _automaticSnapshotResponses)
            {
                return;
            }
        }
        var isWrite = request.Access == BridgeOperationAccess.Write;
        if (isWrite)
        {
            var active = Interlocked.Increment(ref _activeWrites);
            UpdateMaximum(ref _maximumConcurrentWrites, active);
            lock (_writesGate)
            {
                _writeOperationIds.Add(request.OperationId);
            }
        }

        try
        {
            if (isWrite && _writeDelay > TimeSpan.Zero)
            {
                await Task.Delay(_writeDelay, _stopping.Token);
            }
            var customResponse = _responseFactory?.Invoke(request);
            var operationResponse = customResponse ?? (string.Equals(
                request.Operation,
                "canvas.snapshot",
                StringComparison.Ordinal)
                ? BridgeOperationResponse.Create(request.OperationId, changed: false, _snapshotProvider(frame.Target!))
                : BridgeOperationResponse.Create(request.OperationId, changed: false, new { applied = true }));
            // Echo the frame's target, exactly like the plugin: it resolves each frame against its
            // registered-target map and stamps that registration on the response. This is what
            // makes the responder serve sibling Grasshopper documents over the one pipe.
            var response = BridgeFrame.Create(
                BridgeMessageKind.Response,
                BridgeMessageTypes.OperationResponse,
                operationResponse,
                frame.Target!,
                frame.MessageId);
            await _connection.SendAsync(response, _stopping.Token);
        }
        finally
        {
            if (isWrite)
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        var observed = Volatile.Read(ref location);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref location, candidate, observed);
            if (previous == observed)
            {
                return;
            }
            observed = previous;
        }
    }
}

internal sealed record ObservedBridgeRequest(
    BridgeFrame Frame,
    BridgeOperationRequest Request);
