using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Pre-write preflights for deterministic adapter rejections (append-only schema naming, wire
/// endpoints, typing targets, socket-name safety) and the RecoveryRequired applied/unknown/
/// not-dispatched manifest.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class PreflightAndRecoveryManifestTests
{
    private const string InitialFingerprint = "python-f0";

    private static readonly Guid Cpython3TypeId = Guid.Parse("719467e6-7cf5-4848-99b0-c5dd57e5442c");
    private static readonly Guid CSharpTypeId = Guid.Parse("b6ba1144-02d6-4a2d-b53c-ec62e290eeb7");

    [Fact]
    public async Task AppendOnlySchemaRejectionNamesGenuinelyRemovedSockets()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    // 'out' (console) is auto-preserved; 'Rail' is a real socket the declaration drops.
                    CSharpScriptSnapshot(harness, inputs: ["x"], outputs: ["out", "Ceiling", "Rail"]))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Console out"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "omit-out-schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "omit-out-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "Ceiling", access = "list", typeHint = "brep" } },
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
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "omit-out-schema",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Wireify,
                    [],
                    [ioResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "omit-out-schema"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        var message = jobView.GetProperty("message").GetString();

        Assert.Equal("failed", state);
        Assert.Contains("append-only", message, StringComparison.Ordinal);
        // The rejection lists the live sockets and names the genuinely removed socket...
        Assert.Contains("Live outputs: 'out', 'Ceiling', 'Rail'", message, StringComparison.Ordinal);
        Assert.Contains("Undeclared existing output(s): 'Rail'", message, StringComparison.Ordinal);
        // ...but never nags about the console 'out': it is preserved automatically.
        Assert.DoesNotContain("Undeclared existing output(s): 'out'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("console_log", message, StringComparison.Ordinal);
        Assert.Contains("preserved automatically", message, StringComparison.Ordinal);
        lock (writeOps)
        {
            Assert.Empty(writeOps);
        }
    }

    [Fact]
    public async Task ConsoleOutOmissionIsAutoPreservedAndReachesTheWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    // Live component carries the managed console 'out'; the declaration omits it.
                    CSharpScriptSnapshot(harness, inputs: ["x"], outputs: ["out", "Ceiling"]))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Console preserve"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "keep-out-schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "keep-out-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "Ceiling", access = "list", typeHint = "brep" } },
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
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "keep-out-schema",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Wireify,
                    [],
                    [ioResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "keep-out-schema"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        _ = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        var message = jobView.GetProperty("message").GetString() ?? string.Empty;

        // Omitting the console 'out' is no longer an append-only rejection: preflight accepts it and
        // the setSchema is dispatched to the adapter (which preserves the console socket on its
        // side). The write reaching the bridge proves it cleared preflight; this mock cannot return
        // a real fingerprint chain, so we assert on dispatch + absence of the append-only rejection
        // rather than a full commit.
        lock (writeOps)
        {
            Assert.Contains("python.setSchema", writeOps);
        }
        Assert.DoesNotContain("append-only", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceRhinoObjectsDispatchesAsAGrasshopperCreateWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    CSharpScriptSnapshot(harness, inputs: ["x"], outputs: ["out"]))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reference"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var newParamId = Guid.NewGuid();
        var componentResource = new ResourceAddress(ResourceKind.GrasshopperComponent, newParamId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "reference-rhino.json",
            new
            {
                bridgeOperation = "canvas.referenceRhinoObjects",
                arguments = new
                {
                    operationId = "reference-rhino",
                    objectId = newParamId,
                    rhinoObjectIds = new[] { Guid.NewGuid() },
                    paramType = "curve",
                    pivot = new { x = 0.0, y = 0.0 }
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
            [new ResourceExpectation(componentResource, ResourceExpectation.AbsentFingerprint)],
            [
                new TypedOperation(
                    "reference-rhino",
                    OperationKind.ReferenceRhinoObjects,
                    AdapterOwner.Cordyceps,
                    [],
                    [componentResource],
                    Reversible: false,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "reference-rhino"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        _ = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString() ?? string.Empty;

        // The new op maps, validates (gptino:absent create), and dispatches to the canvas adapter — the
        // write reaching the bridge proves the whole AgentHost plumbing is wired. (The mock cannot run
        // the real GH reference, so we assert dispatch + no pre-write rejection, not a full commit.)
        lock (writeOps)
        {
            Assert.Contains("canvas.referenceRhinoObjects", writeOps);
        }
        Assert.DoesNotContain("Rejected before any write", message, StringComparison.Ordinal);
        Assert.DoesNotContain("no safe bridge mapping", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WireTargetParameterMissingFailsBeforeAnyWriteWithSocketListing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sourceParameterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var targetParameterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var wrongParameterId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    WiredPairSnapshot(harness, sourceParameterId, targetParameterId))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Bad wire"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(
                await WireChangeSetAsync(
                    harness,
                    session,
                    snapshot.Revision,
                    sourceParameterId,
                    wrongParameterId,
                    "bad-wire"),
                snapshot.Id,
                "bad-wire"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        Assert.Equal("failed", state);
        Assert.Contains(
            $"target parameter {wrongParameterId:D}",
            message,
            StringComparison.Ordinal);
        // The listing carries name=id pairs so one retry can fix the wire.
        Assert.Contains(
            $"Available input sockets: x={targetParameterId:D}",
            message,
            StringComparison.Ordinal);
        Assert.Contains("Rejected before any write", message, StringComparison.Ordinal);
        lock (writeOps)
        {
            Assert.Empty(writeOps);
        }
    }

    [Fact]
    public async Task ValidWireStillPassesPreflightAndCommits()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var sourceParameterId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var targetParameterId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    WiredPairSnapshot(harness, sourceParameterId, targetParameterId))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Good wire"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(
                await WireChangeSetAsync(
                    harness,
                    session,
                    snapshot.Revision,
                    sourceParameterId,
                    targetParameterId,
                    "good-wire"),
                snapshot.Id,
                "good-wire"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        lock (writeOps)
        {
            Assert.Contains("canvas.setWire", writeOps);
        }
    }

    [Fact]
    public async Task TypingInputParameterMissingFailsBeforeAnyWriteWithSocketListing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var inputParameterId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var wrongParameterId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    PythonScriptSnapshot(harness, inputParameterId))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Bad typing"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "bad-typing.json",
            new
            {
                bridgeOperation = "python.setTyping",
                arguments = new
                {
                    operationId = "bad-typing",
                    componentId = harness.CanvasObjectId,
                    inputParameterId = wrongParameterId,
                    typeHint = "curve",
                    access = "item"
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
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "bad-typing",
                    OperationKind.ConvertSocket,
                    AdapterOwner.Wireify,
                    [],
                    [ioResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "bad-typing"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        Assert.Equal("failed", state);
        Assert.Contains($"Python input {wrongParameterId:D}", message, StringComparison.Ordinal);
        Assert.Contains(
            $"Available input sockets: x={inputParameterId:D}",
            message,
            StringComparison.Ordinal);
        Assert.Contains("Rejected before any write", message, StringComparison.Ordinal);
        lock (writeOps)
        {
            Assert.Empty(writeOps);
        }
    }

    [Fact]
    public async Task CSharpReservedKeywordSocketNameFailsBeforeAnyWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    CSharpScriptSnapshot(harness, inputs: ["x"], outputs: ["a"]))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reserved out"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "reserved-out.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "reserved-out",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "double" } },
                    outputs = new[]
                    {
                        new { name = "a", access = "item", typeHint = "double" },
                        new { name = "out", access = "item", typeHint = "object" }
                    },
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
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "reserved-out",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Wireify,
                    [],
                    [ioResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "reserved-out"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        Assert.Equal("failed", state);
        Assert.Contains("C# reserved keyword", message, StringComparison.Ordinal);
        Assert.Contains("console_log", message, StringComparison.Ordinal);
        Assert.Contains("Rejected before any write", message, StringComparison.Ordinal);
        lock (writeOps)
        {
            Assert.Empty(writeOps);
        }
    }

    [Fact]
    public async Task UnsafeSocketNameFailsBeforeAnyWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    PythonScriptSnapshot(harness, Guid.NewGuid()))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Unsafe name"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "unsafe-name.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "unsafe-name",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "object" } },
                    outputs = new[]
                    {
                        new { name = "a", access = "item", typeHint = "object" },
                        new { name = "선택 갱신", access = "list", typeHint = "object" }
                    },
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
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "unsafe-name",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Wireify,
                    [],
                    [ioResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "unsafe-name"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        Assert.Equal("failed", state);
        Assert.Contains(
            "'선택 갱신' is not a safe Python variable name",
            message,
            StringComparison.Ordinal);
        Assert.Contains("Rejected before any write", message, StringComparison.Ordinal);
        lock (writeOps)
        {
            Assert.Empty(writeOps);
        }
    }

    [Fact]
    public async Task MidChangeSetFailureReportsAppliedUnknownAndNotDispatchedManifest()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder(responseFactory: request =>
            request.OperationId == "second-slider"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: true,
                    new { applied = false },
                    beforeFingerprint: harness.SecondObjectFingerprint,
                    afterFingerprint: "slider-after",
                    diagnostics:
                    [
                        new BridgeDiagnostic(
                            BridgeDiagnosticSeverity.Error,
                            "slider_error",
                            "Value could not be applied.")
                    ])
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Manifest"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var firstResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var secondResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.SecondCanvasObjectId.ToString("D"));
        var layoutResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
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
        var thirdArtifact = await harness.WritePayloadAsync(
            session,
            "third-move.json",
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = "third-move",
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.CanvasObjectId] = new { x = 42, y = 24 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.CanvasObjectId] = harness.ObjectFingerprint
                    }
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
                new ResourceExpectation(secondResource, harness.SecondObjectFingerprint),
                new ResourceExpectation(layoutResource, harness.ObjectFingerprint)
            ],
            [
                new TypedOperation(
                    "first-slider",
                    OperationKind.SetValue,
                    AdapterOwner.Cordyceps,
                    [],
                    [firstResource],
                    Reversible: true,
                    firstArtifact),
                new TypedOperation(
                    "second-slider",
                    OperationKind.SetValue,
                    AdapterOwner.Cordyceps,
                    [],
                    [secondResource],
                    Reversible: true,
                    secondArtifact),
                new TypedOperation(
                    "third-move",
                    OperationKind.MoveComponent,
                    AdapterOwner.Cordyceps,
                    [],
                    [layoutResource],
                    Reversible: true,
                    thirdArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "manifest"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        var message = jobView.GetProperty("message").GetString();

        Assert.Equal("recoveryrequired", state);
        // The first op verifiably applied; the second is honestly unknown (never claimed failed);
        // the third never reached the bridge.
        Assert.Contains(
            "Applied: first-slider. Unknown outcome: second-slider (in flight at failure). " +
            "Not dispatched: third-move.",
            message,
            StringComparison.Ordinal);
        Assert.Equal(["first-slider", "second-slider"], responder.WriteOperationIds);
        var diagnostics = jobView.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Contains(diagnostics, item =>
            item.GetProperty("code").GetString() == "recovery_applied" &&
            item.GetProperty("message").GetString() == "first-slider");
        Assert.Contains(diagnostics, item =>
            item.GetProperty("code").GetString() == "recovery_unknown" &&
            item.GetProperty("message").GetString() == "second-slider (in flight at failure)");
        Assert.Contains(diagnostics, item =>
            item.GetProperty("code").GetString() == "recovery_not_dispatched" &&
            item.GetProperty("message").GetString() == "third-move");
    }

    [Fact]
    public void BridgeTimeoutMessageCarriesOperationIdBudgetAndGuidance()
    {
        var message = LiveDocumentBackend.BridgeTimeoutMessage(
            "execute-heavy-ceiling",
            "python.execute",
            TimeSpan.FromSeconds(45));

        Assert.Contains("'execute-heavy-ceiling'", message, StringComparison.Ordinal);
        Assert.Contains("python.execute", message, StringComparison.Ordinal);
        Assert.Contains("45s budget", message, StringComparison.Ordinal);
        Assert.Contains("still solving on the UI thread", message, StringComparison.Ordinal);
        Assert.Contains("Do NOT resubmit the same heavy solve", message, StringComparison.Ordinal);
        Assert.Contains("sampling/segment counts", message, StringComparison.Ordinal);
        Assert.Contains("staged components", message, StringComparison.Ordinal);
        Assert.Contains("native Grasshopper components", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryManifestSeparatesAppliedUnknownAndNotDispatched()
    {
        TypedOperation Operation(string id) => new(
            id,
            OperationKind.SetValue,
            AdapterOwner.Cordyceps,
            [],
            [],
            Reversible: true,
            "artifact.json");
        var operations = new[] { Operation("a"), Operation("b"), Operation("c"), Operation("d"), Operation("e") };

        var (message, diagnostics) = LiveDocumentBackend.BuildRecoveryManifest(
            operations,
            ["a", "b"],
            "c");

        Assert.Equal(
            "Applied: a, b. Unknown outcome: c (in flight at failure). Not dispatched: d, e.",
            message);
        Assert.Contains(diagnostics, item =>
            item.Code == "recovery_unknown" && item.Message == "c (in flight at failure)");

        var (noneMessage, _) = LiveDocumentBackend.BuildRecoveryManifest(operations[..1], ["a"], null);
        Assert.Equal("Applied: a. Unknown outcome: none. Not dispatched: none.", noneMessage);
    }

    [Fact]
    public void CommitQualityCountsWarningsAndEmptyOutputsExcludingConsole()
    {
        var diagnostics = new[]
        {
            new LiveDocumentBackend.JobDiagnostic(
                "execute-script",
                BridgeDiagnosticSeverity.Warning,
                "python_warning",
                "37 panel curve(s) could not be mapped"),
            new LiveDocumentBackend.JobDiagnostic(
                "execute-script",
                BridgeDiagnosticSeverity.Information,
                "op_duration",
                "python.execute: 6100 ms of the 45s bridge budget.")
        };
        var outputs = new[]
        {
            new LiveDocumentBackend.JobComponentOutputs(
                Guid.NewGuid(),
                JsonSerializer.SerializeToElement(
                    new
                    {
                        outputs = new object[]
                        {
                            new { name = "Ceiling", dataCount = 0 },
                            new { name = "out", dataCount = 0 },
                            new { name = "N", dataCount = 3 }
                        }
                    },
                    BridgeProtocol.JsonOptions))
        };

        Assert.Equal(
            "1 runtime warning(s); output(s) 'Ceiling' empty.",
            LiveDocumentBackend.DescribeCommitQuality(diagnostics, outputs));
        Assert.Null(LiveDocumentBackend.DescribeCommitQuality(
            Array.Empty<LiveDocumentBackend.JobDiagnostic>(),
            null));
    }

    private static async Task<ChangeSet> WireChangeSetAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        Guid sourceParameterId,
        Guid targetParameterId,
        string operationId)
    {
        var wireId =
            $"{harness.CanvasObjectId:N}/{sourceParameterId:N}>" +
            $"{harness.SecondCanvasObjectId:N}/{targetParameterId:N}";
        var wireResource = new ResourceAddress(ResourceKind.GrasshopperWire, wireId);
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{operationId}.json",
            new
            {
                bridgeOperation = "canvas.setWire",
                arguments = new
                {
                    operationId,
                    wire = new
                    {
                        sourceObjectId = harness.CanvasObjectId,
                        sourceParameterId,
                        targetObjectId = harness.SecondCanvasObjectId,
                        targetParameterId
                    },
                    action = "connect",
                    rejectCycles = true
                }
            });
        return new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            revision,
            null,
            [],
            [],
            [new ResourceExpectation(wireResource, ResourceExpectation.AbsentFingerprint)],
            [
                new TypedOperation(
                    operationId,
                    OperationKind.ConnectWire,
                    AdapterOwner.Cordyceps,
                    [],
                    [wireResource],
                    Reversible: true,
                    artifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);
    }

    /// <summary>Source component (one output 'a') wired toward target component (one input 'x').</summary>
    private static CanvasSnapshot WiredPairSnapshot(
        LiveDocumentBackendHarness harness,
        Guid sourceParameterId,
        Guid targetParameterId)
    {
        var source = new CanvasObjectState(
            harness.CanvasObjectId,
            Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
            "Source",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            harness.ObjectFingerprint)
        {
            Outputs = [Parameter(harness.CanvasObjectId, sourceParameterId, "a", CanvasParameterDirection.Output)],
            StructureFingerprint = harness.ObjectFingerprint,
        };
        var target = new CanvasObjectState(
            harness.SecondCanvasObjectId,
            Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
            "Target",
            new CanvasPoint(200, 20),
            new CanvasSize(90, 40),
            harness.SecondObjectFingerprint)
        {
            Inputs = [Parameter(harness.SecondCanvasObjectId, targetParameterId, "x", CanvasParameterDirection.Input)],
            StructureFingerprint = harness.SecondObjectFingerprint,
        };
        return new CanvasSnapshot(
            harness.Target.GrasshopperDocumentId!.Value,
            "wired-pair-document-v1",
            [source, target],
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    /// <summary>CPython3 script component with one known input 'x' and one output 'a'.</summary>
    private static CanvasSnapshot PythonScriptSnapshot(
        LiveDocumentBackendHarness harness,
        Guid inputParameterId)
    {
        var component = new CanvasObjectState(
            harness.CanvasObjectId,
            Cpython3TypeId,
            "Script",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            InitialFingerprint)
        {
            Inputs = [Parameter(harness.CanvasObjectId, inputParameterId, "x", CanvasParameterDirection.Input)],
            Outputs = [Parameter(harness.CanvasObjectId, Guid.NewGuid(), "a", CanvasParameterDirection.Output)],
            StructureFingerprint = InitialFingerprint,
        };
        return new CanvasSnapshot(
            harness.Target.GrasshopperDocumentId!.Value,
            "python-script-document-v1",
            [component],
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    /// <summary>Rhino 8 C# Script component with the given live socket names.</summary>
    private static CanvasSnapshot CSharpScriptSnapshot(
        LiveDocumentBackendHarness harness,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> outputs)
    {
        var component = new CanvasObjectState(
            harness.CanvasObjectId,
            CSharpTypeId,
            "C# Script",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            InitialFingerprint)
        {
            Inputs = inputs
                .Select(name => Parameter(
                    harness.CanvasObjectId,
                    Guid.NewGuid(),
                    name,
                    CanvasParameterDirection.Input))
                .ToArray(),
            Outputs = outputs
                .Select(name => Parameter(
                    harness.CanvasObjectId,
                    Guid.NewGuid(),
                    name,
                    CanvasParameterDirection.Output))
                .ToArray(),
            StructureFingerprint = InitialFingerprint,
        };
        return new CanvasSnapshot(
            harness.Target.GrasshopperDocumentId!.Value,
            "csharp-script-document-v1",
            [component],
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    private static CanvasParameterState Parameter(
        Guid ownerObjectId,
        Guid parameterId,
        string name,
        CanvasParameterDirection direction) => new(
        ownerObjectId,
        parameterId,
        name,
        name,
        direction,
        "System.Object",
        "object",
        CanvasParameterAccess.Item,
        Optional: false,
        Array.Empty<CanvasParameterEndpoint>());

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
                summary = "Preflight and recovery manifest regression"
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
