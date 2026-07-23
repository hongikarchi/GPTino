using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.WireifyAdapter;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class DeterministicScriptFailureTests
{
    private const string InitialFingerprint = "python-f0";

    [Fact]
    public async Task PythonRuntimeErrorFailsDeterministicallyWithAppliedView()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.execute" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { solved = false },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "NameError: name 'pt' is not defined")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Runtime error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "execute-script",
                    componentId = harness.CanvasObjectId,
                    expireUpstream = false,
                    recomputeDocument = false
                }
            });
        // acceptancePredicates deliberately empty: the server must attach the default
        // runtimeErrorAbsent predicate for a script write.
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(resource, InitialFingerprint)],
            [
                new TypedOperation(
                    "execute-script",
                    OperationKind.ExecutePython,
                    AdapterOwner.Wireify,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "runtime-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // Deterministic failure: writes landed, loop completed — Failed, never RecoveryRequired.
        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Null, jobView.GetProperty("committed").ValueKind);
        var applied = jobView.GetProperty("applied");
        Assert.Equal(JsonValueKind.Object, applied.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(applied.GetProperty("snapshotId").GetString()));
        var appliedResource = Assert.Single(applied.GetProperty("resources").EnumerateArray());
        Assert.Equal(harness.CanvasObjectId.ToString("D"), appliedResource.GetProperty("id").GetString());
        var diagnostic = Assert.Single(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("severity").GetString() == "error");
        Assert.Equal("execute-script", diagnostic.GetProperty("operationId").GetString());
        Assert.Equal("python_error", diagnostic.GetProperty("code").GetString());
        var message = jobView.GetProperty("message").GetString();
        Assert.Contains("execute-script", message, StringComparison.Ordinal);
        // The server-attached default predicate also reports, proving predicate defaulting ran.
        Assert.Contains("gptino:default", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileErrorContinuesRemainingScriptOperations()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.setSource" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "SyntaxError: invalid syntax")
                ]),
            "python.execute" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { solved = true },
                beforeFingerprint: "python-f1",
                afterFingerprint: "python-f2"),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Compile error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var valueResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "set-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "source-v0",
                    source = "def broken(:",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var executeArtifact = await harness.WritePayloadAsync(
            session,
            "execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "execute-script",
                    componentId = harness.CanvasObjectId,
                    expireUpstream = false,
                    recomputeDocument = false
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
                new ResourceExpectation(sourceResource, InitialFingerprint),
                new ResourceExpectation(valueResource, InitialFingerprint)
            ],
            [
                new TypedOperation(
                    "set-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Wireify,
                    [],
                    [sourceResource],
                    Reversible: true,
                    sourceArtifact),
                new TypedOperation(
                    "execute-script",
                    OperationKind.ExecutePython,
                    AdapterOwner.Wireify,
                    [],
                    [valueResource],
                    Reversible: true,
                    executeArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "compile-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // The compile error did NOT abort the loop: both script operations were dispatched, so
        // the after-snapshot reflects the complete application.
        Assert.Equal(["set-source", "execute-script"], responder.WriteOperationIds);
        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Object, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("operationId").GetString() == "set-source" &&
                item.GetProperty("code").GetString() == "python_error");
    }

    [Fact]
    public async Task SchemaCompileErrorFailsDeterministicallyWithAppliedView()
    {
        // Live round R3: a staged compile error surfaces on the setComponentIo response because
        // the schema write triggers the solve. It must be an iterable Failed, not RecoveryRequired.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.setSchema" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "The name 'missingOffset' does not exist in the current context")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Schema error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "set-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "spacing", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "pts", access = "list", typeHint = "point3d" } },
                    preserveIncidentWires = true
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
            [new ResourceExpectation(resource, InitialFingerprint)],
            [
                new TypedOperation(
                    "set-schema",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Wireify,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "schema-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Object, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("operationId").GetString() == "set-schema" &&
                item.GetProperty("code").GetString() == "python_error");
    }

    [Fact]
    public async Task NonScriptErrorDiagnosticStillAborts()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "canvas.setNumberSlider" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = false },
                beforeFingerprint: harness.ObjectFingerprint,
                afterFingerprint: "slider-after",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "slider_error",
                        "Value could not be applied.")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Slider error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "bad-slider",
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
                "bad-slider",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "bad-slider"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // A non-script Error diagnostic means the operation itself failed: the loop aborts and
        // the outcome stays RecoveryRequired (a live write may have landed in unknown shape).
        Assert.Equal("recoveryrequired", state);
        Assert.Equal(JsonValueKind.Null, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            "slider_error",
            jobView.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedPredicatesGetServerDefaultsAndCommit()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Default predicates"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "default-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "default-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 30m,
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
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [
                new TypedOperation(
                    "default-slider",
                    OperationKind.SetValue,
                    AdapterOwner.Cordyceps,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "default-predicates"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
    }

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey) =>
        JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = snapshotId,
                idempotencyKey,
                summary = "Deterministic script failure regression"
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
