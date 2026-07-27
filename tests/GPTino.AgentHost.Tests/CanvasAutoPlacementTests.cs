using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Pure-function tests for the deterministic component auto-placement algorithm, plus the
/// ResolveAutoPivots rewrite wrapper, plus broker acceptance of the pivot:"gptino:auto" sentinel.
/// </summary>
public sealed class CanvasAutoPlacementTests
{
    private const float EstimatedWidth = 160f;
    private const float EstimatedHeight = 80f;

    // ---- Pure algorithm: CanvasAutoPlacement.ComputePlacements --------------------------------

    [Fact]
    public void DownstreamColumnMath_PlacesRightOfUpstreamCenteredOnItsPivotY()
    {
        var upstream = Guid.NewGuid();
        var created = Guid.NewGuid();
        var existing = new[] { Existing(upstream, pivotX: 100, pivotY: 200, width: 80, height: 40) };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0, upstream) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            existing);

        // Upstream rect (pivot-centered) = (60,180,80,40) → rightEdge 140.
        // x = 140 + HGAP(90) + EstW/2(80) = 310; y = mean upstream pivot Y = 200.
        Assert.Equal(new CanvasPoint(310f, 200f), placements[created]);
    }

    [Fact]
    public void WireEdgeUpstream_IsHonoredLikeAutoUpstream()
    {
        var source = Guid.NewGuid();
        var created = Guid.NewGuid();
        var existing = new[] { Existing(source, pivotX: 100, pivotY: 100, width: 80, height: 40) };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0) }, // no declared autoUpstream
            new[] { new CanvasAutoPlacement.AutoWire(source, created) }, // topology arrives via a connect wire
            existing);

        // Source rect (60,80,80,40) → rightEdge 140 → x = 140+90+80 = 310; y = 100.
        Assert.Equal(new CanvasPoint(310f, 100f), placements[created]);
    }

    [Fact]
    public void TopologicalOrder_ResolvesAChainDeclaredInReverseOperationOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        // Operation order is C, B, A (reverse of the data-flow A -> B -> C). Correct topological
        // ordering must still place A before B before C so each derives its column from the previous.
        var placements = CanvasAutoPlacement.ComputePlacements(
            new[]
            {
                Create(c, order: 0, b),
                Create(b, order: 1, a),
                Create(a, order: 2),
            },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            Array.Empty<CanvasObjectState>());

        Assert.True(placements[a].X < placements[b].X, "A must be left of B");
        Assert.True(placements[b].X < placements[c].X, "B must be left of C");
    }

    [Fact]
    public void EmptyCanvasUnconstrainedCreate_AnchorsAtOrigin()
    {
        var created = Guid.NewGuid();

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            Array.Empty<CanvasObjectState>());

        Assert.Equal(new CanvasPoint(0f, 0f), placements[created]);
    }

    [Fact]
    public void NoUpstreamCreate_AnchorsBelowExistingContentAtItsLeftEdge()
    {
        var existingId = Guid.NewGuid();
        var created = Guid.NewGuid();
        var existing = new[] { Existing(existingId, pivotX: 100, pivotY: 100, width: 80, height: 40) };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            existing);

        // Existing rect (60,80,80,40): bbox min-X = 60, bottom = 120.
        // x = 60 + EstW/2(80) = 140; y = 120 + Padding(20) + EstH/2(40) = 180.
        var placed = placements[created];
        Assert.Equal(140f, placed.X);
        Assert.Equal(180f, placed.Y);
        Assert.True(placed.Y > 120f, "New content must land below the existing bounding box");
    }

    [Fact]
    public void BoundsOrigin_IsPreferredOverPivotForTheExistingRectangle()
    {
        // A panel whose pivot is nowhere near its bounding-box center: BoundsOrigin must drive the rect.
        var panel = Guid.NewGuid();
        var created = Guid.NewGuid();
        var existing = new[]
        {
            Existing(panel, pivotX: 0, pivotY: 0, width: 100, height: 60,
                boundsOrigin: new CanvasPoint(500, 500)),
        };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0, panel) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            existing);

        // Rect from BoundsOrigin = (500,500,100,60) → rightEdge 600 → x = 600+90+80 = 770.
        Assert.Equal(770f, placements[created].X);
    }

    [Fact]
    public void CollisionStepping_SeparatesTwoCreatesThatShareAnAnchor()
    {
        var source = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var existing = new[] { Existing(source, pivotX: 0, pivotY: 0, width: 0, height: 0) };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(first, order: 0, source), Create(second, order: 1, source) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            existing);

        Assert.NotEqual(placements[first], placements[second]);
        Assert.False(
            Overlaps(RectOf(placements[first]), RectOf(placements[second])),
            "Two auto-placed components must not overlap each other");
    }

    [Fact]
    public void NeverOverlapsAnExistingHumanPlacedRectangle()
    {
        var source = Guid.NewGuid();
        var obstacle = Guid.NewGuid();
        var created = Guid.NewGuid();
        var existing = new[]
        {
            Existing(source, pivotX: 0, pivotY: 0, width: 0, height: 0),
            // Sits exactly where the new component's first anchor box would land, forcing a step.
            Existing(obstacle, pivotX: 170, pivotY: 0, width: 160, height: 80),
        };

        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(created, order: 0, source) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            existing);

        var placed = RectOf(placements[created]);
        foreach (var item in existing)
        {
            Assert.False(
                Overlaps(placed, ExistingRectOf(item)),
                "Auto-placement must never overlap an existing (human-placed) object");
        }
    }

    [Fact]
    public void ComputePlacements_IsDeterministic()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var result = Guid.NewGuid();
        var existing = new[]
        {
            Existing(s1, pivotX: 10, pivotY: 10, width: 40, height: 20),
            Existing(s2, pivotX: 10, pivotY: 90, width: 40, height: 20),
        };
        var creates = new[]
        {
            Create(mid, order: 0, s1, s2),
            Create(result, order: 1, mid),
        };
        var wires = new[] { new CanvasAutoPlacement.AutoWire(s1, mid) };

        var first = CanvasAutoPlacement.ComputePlacements(creates, wires, existing);
        var second = CanvasAutoPlacement.ComputePlacements(creates, wires, existing);

        Assert.Equal(first.Count, second.Count);
        foreach (var pair in first)
        {
            Assert.Equal(pair.Value, second[pair.Key]);
        }
    }

    [Fact]
    public void CyclicAutoUpstream_FallsBackToOperationOrderWithoutThrowing()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();

        // x depends on y and y depends on x — a cycle. The pure function must still return a placement
        // for both (operation-order fallback); the real wire cycle is rejected later by RejectCycles.
        var placements = CanvasAutoPlacement.ComputePlacements(
            new[] { Create(x, order: 0, y), Create(y, order: 1, x) },
            Array.Empty<CanvasAutoPlacement.AutoWire>(),
            Array.Empty<CanvasObjectState>());

        Assert.True(placements.ContainsKey(x));
        Assert.True(placements.ContainsKey(y));
        Assert.NotEqual(placements[x], placements[y]);
    }

    // ---- Rewrite wrapper: CanvasAutoPlacement.ResolveAutoPivots -------------------------------

    [Fact]
    public void ResolveAutoPivots_RewritesSentinelToConcretePivotAndStripsAutoUpstream()
    {
        var objectId = Guid.NewGuid();
        var upstreamId = Guid.NewGuid();
        var sentinel = SentinelCreate("create-1", objectId, new[] { upstreamId });

        var rewritten = CanvasAutoPlacement.ResolveAutoPivots(new[] { sentinel }, EmptyCanvas());

        var arguments = rewritten[0].Arguments;
        Assert.Equal(JsonValueKind.Object, arguments.GetProperty("pivot").ValueKind);
        Assert.True(arguments.GetProperty("pivot").TryGetProperty("x", out _));
        Assert.True(arguments.GetProperty("pivot").TryGetProperty("y", out _));
        Assert.False(arguments.TryGetProperty("autoUpstream", out _), "autoUpstream must be stripped");
        Assert.Equal(objectId, arguments.GetProperty("objectId").GetGuid());

        // The rewritten arguments must deserialize CLEANLY into today's exact adapter contract —
        // BridgeProtocol.JsonOptions disallows unmapped members, so a leaked sentinel/autoUpstream throws.
        var request = arguments.Deserialize<CreateCanvasObjectRequest>(BridgeProtocol.JsonOptions);
        Assert.NotNull(request);
        Assert.Equal(objectId, request!.ObjectId);
    }

    [Fact]
    public void ResolveAutoPivots_ResolvesReferenceRhinoObjectsSentinelAndKeepsItsFields()
    {
        var objectId = Guid.NewGuid();
        var reference = SentinelReference("ref-1", objectId);

        var rewritten = CanvasAutoPlacement.ResolveAutoPivots(new[] { reference }, EmptyCanvas());

        var arguments = rewritten[0].Arguments;
        // The sentinel pivot is resolved to a concrete point (so the adapter's RequireFinite passes)...
        Assert.Equal(JsonValueKind.Object, arguments.GetProperty("pivot").ValueKind);
        Assert.True(arguments.GetProperty("pivot").TryGetProperty("x", out _));
        Assert.True(arguments.GetProperty("pivot").TryGetProperty("y", out _));
        // ...while every reference-specific field is preserved untouched.
        Assert.Equal("curve", arguments.GetProperty("paramType").GetString());
        Assert.Equal(1, arguments.GetProperty("rhinoObjectIds").GetArrayLength());
        Assert.Equal(objectId, arguments.GetProperty("objectId").GetGuid());
    }

    [Fact]
    public void ResolveAutoPivots_LeavesExplicitPivotAndNonCreateOperationsUntouched()
    {
        var explicitCreate = ExplicitCreate("create-explicit", Guid.NewGuid(), x: 42, y: 24);
        var sentinel = SentinelCreate("create-auto", Guid.NewGuid(), Array.Empty<Guid>());

        var rewritten = CanvasAutoPlacement.ResolveAutoPivots(new[] { explicitCreate, sentinel }, EmptyCanvas());

        // The explicit-coordinate create passes through by reference; only the sentinel is rebuilt.
        Assert.Same(explicitCreate, rewritten[0]);
        Assert.NotSame(sentinel, rewritten[1]);
        Assert.Equal(42, rewritten[0].Arguments.GetProperty("pivot").GetProperty("x").GetInt32());
    }

    [Fact]
    public void ResolveAutoPivots_ReturnsSameListWhenNoSentinelIsPresent()
    {
        var explicitCreate = ExplicitCreate("create-explicit", Guid.NewGuid(), x: 1, y: 2);
        var operations = new[] { explicitCreate };

        var rewritten = CanvasAutoPlacement.ResolveAutoPivots(operations, EmptyCanvas());

        Assert.Same(operations, rewritten);
    }

    // ---- Broker acceptance of the sentinel (validator, full submit path) ----------------------

    [Collection(LiveDocumentBackendCollection.Name)]
    public sealed class SentinelValidation
    {
        [Fact]
        public async Task SentinelPivotWithAutoUpstreamIsAccepted()
        {
            await using var harness = await LiveDocumentBackendHarness.CreateAsync();
            await using var responder = harness.StartResponder();
            harness.Backend.SetPaused(true);
            var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Sentinel creator"));
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var objectId = Guid.NewGuid();
            var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
            var artifact = await harness.WritePayloadAsync(
                session,
                "sentinel-create.json",
                new
                {
                    bridgeOperation = "canvas.create",
                    arguments = new
                    {
                        operationId = "create-auto",
                        objectId,
                        componentTypeId = Guid.NewGuid(),
                        pivot = "gptino:auto",
                        autoUpstream = new[] { Guid.NewGuid() },
                        nickName = "Auto placed"
                    }
                });
            var changeSet = harness.CreateCustomChangeSet(
                session,
                snapshot.Revision,
                new TypedOperation(
                    "create-auto",
                    OperationKind.CreateComponent,
                    AdapterOwner.Cordyceps,
                    Array.Empty<ResourceAddress>(),
                    [resource],
                    true,
                    artifact),
                [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "sentinel-accept", "Create with sentinel pivot"),
                CancellationToken.None));

            Assert.False(submitted.GetProperty("duplicate").GetBoolean());
        }

        [Fact]
        public async Task ExecutingASentinelCreateDispatchesAConcreteResolvedPivot()
        {
            await using var harness = await LiveDocumentBackendHarness.CreateAsync();
            var objectId = Guid.NewGuid();
            var dispatched = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var responder = harness.StartResponder(responseFactory: request =>
            {
                if (!string.Equals(request.Operation, "canvas.create", StringComparison.Ordinal))
                {
                    return null;
                }
                dispatched.TrySetResult(request.Arguments.Clone());
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: true,
                    new CanvasMutationResult(request.OperationId, true, string.Empty, "after-fp", new[] { objectId }));
            });
            var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Sentinel executor"));
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
            var artifact = await harness.WritePayloadAsync(
                session,
                "sentinel-exec.json",
                new
                {
                    bridgeOperation = "canvas.create",
                    arguments = new
                    {
                        operationId = "create-auto-exec",
                        objectId,
                        componentTypeId = Guid.NewGuid(),
                        pivot = "gptino:auto",
                        autoUpstream = new[] { harness.CanvasObjectId }, // an existing snapshot object
                        nickName = "Auto exec"
                    }
                });
            var changeSet = harness.CreateCustomChangeSet(
                session,
                snapshot.Revision,
                new TypedOperation(
                    "create-auto-exec",
                    OperationKind.CreateComponent,
                    AdapterOwner.Cordyceps,
                    Array.Empty<ResourceAddress>(),
                    [resource],
                    true,
                    artifact),
                [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "sentinel-exec", "Execute sentinel create"),
                CancellationToken.None));
            var jobId = submitted.GetProperty("jobId").GetGuid();

            var finished = await Task.WhenAny(dispatched.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(dispatched.Task, finished);
            var arguments = await dispatched.Task;

            // The sentinel never reaches the adapter: the DISPATCHED pivot is a concrete point and
            // autoUpstream is stripped — proving ResolveAutoPivots is wired into ExecuteAsync and that a
            // string pivot / autoUpstream can never leak past the broker to the strict bridge contract.
            Assert.Equal(JsonValueKind.Object, arguments.GetProperty("pivot").ValueKind);
            Assert.False(arguments.TryGetProperty("autoUpstream", out _));
            var request = arguments.Deserialize<CreateCanvasObjectRequest>(BridgeProtocol.JsonOptions);
            Assert.NotNull(request);
            // The upstream existing object sits at pivot (10,20); the new component lands to its right.
            Assert.True(request!.Pivot.X > 10f, "resolved pivot must be downstream (right) of its upstream");

            await harness.WaitForJobStateAsync(jobId);
        }

        [Fact]
        public async Task ExplicitPivotWithAutoUpstreamIsRejected()
        {
            await using var harness = await LiveDocumentBackendHarness.CreateAsync();
            await using var responder = harness.StartResponder();
            harness.Backend.SetPaused(true);
            var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Explicit creator"));
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var objectId = Guid.NewGuid();
            var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
            var artifact = await harness.WritePayloadAsync(
                session,
                "explicit-with-upstream.json",
                new
                {
                    bridgeOperation = "canvas.create",
                    arguments = new
                    {
                        operationId = "create-bad",
                        objectId,
                        componentTypeId = Guid.NewGuid(),
                        pivot = new { x = 10, y = 20 },
                        autoUpstream = new[] { Guid.NewGuid() },
                        nickName = "Invalid"
                    }
                });
            var changeSet = harness.CreateCustomChangeSet(
                session,
                snapshot.Revision,
                new TypedOperation(
                    "create-bad",
                    OperationKind.CreateComponent,
                    AdapterOwner.Cordyceps,
                    Array.Empty<ResourceAddress>(),
                    [resource],
                    true,
                    artifact),
                [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Backend.SubmitChangeAsync(
                    session,
                    Submission(changeSet, snapshot.Id, "explicit-upstream-reject", "Invalid create"),
                    CancellationToken.None));

            Assert.Contains("autoUpstream", exception.Message, StringComparison.Ordinal);
        }

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

    // ---- Helpers -----------------------------------------------------------------------------

    private static CanvasObjectState Existing(
        Guid id,
        float pivotX,
        float pivotY,
        float width,
        float height,
        CanvasPoint? boundsOrigin = null) =>
        new(
            id,
            Guid.NewGuid(),
            "Existing",
            new CanvasPoint(pivotX, pivotY),
            new CanvasSize(width, height),
            "fp")
        {
            BoundsOrigin = boundsOrigin,
        };

    private static CanvasAutoPlacement.AutoCreate Create(Guid id, int order, params Guid[] upstream) =>
        new(id, upstream, order);

    private static CanvasSnapshot EmptyCanvas() =>
        new(
            Guid.NewGuid(),
            "document-fp",
            Array.Empty<CanvasObjectState>(),
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());

    private static LiveDocumentBackend.PreparedOperation SentinelCreate(
        string operationId,
        Guid objectId,
        IReadOnlyList<Guid> autoUpstream)
    {
        var upstreamJson = string.Join(",", autoUpstream.Select(id => $"\"{id:D}\""));
        var json = $$"""
            {
              "operationId": "{{operationId}}",
              "objectId": "{{objectId:D}}",
              "componentTypeId": "{{Guid.NewGuid():D}}",
              "pivot": "gptino:auto",
              "autoUpstream": [{{upstreamJson}}],
              "nickName": "Auto"
            }
            """;
        return PreparedCreate(operationId, objectId, json);
    }

    private static LiveDocumentBackend.PreparedOperation SentinelReference(
        string operationId,
        Guid objectId)
    {
        var json = $$"""
            {
              "operationId": "{{operationId}}",
              "objectId": "{{objectId:D}}",
              "rhinoObjectIds": ["{{Guid.NewGuid():D}}"],
              "paramType": "curve",
              "pivot": "gptino:auto",
              "nickName": "Referenced"
            }
            """;
        using var document = JsonDocument.Parse(json);
        var arguments = document.RootElement.Clone();
        var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
        return new LiveDocumentBackend.PreparedOperation(
            new TypedOperation(
                operationId,
                OperationKind.ReferenceRhinoObjects,
                AdapterOwner.Cordyceps,
                Array.Empty<ResourceAddress>(),
                [resource],
                true,
                $"operations/{operationId}.json"),
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.referenceRhinoObjects",
            arguments,
            Array.Empty<byte>(),
            "sha");
    }

    private static LiveDocumentBackend.PreparedOperation ExplicitCreate(
        string operationId,
        Guid objectId,
        int x,
        int y)
    {
        var json = $$"""
            {
              "operationId": "{{operationId}}",
              "objectId": "{{objectId:D}}",
              "componentTypeId": "{{Guid.NewGuid():D}}",
              "pivot": { "x": {{x}}, "y": {{y}} },
              "nickName": "Explicit"
            }
            """;
        return PreparedCreate(operationId, objectId, json);
    }

    private static LiveDocumentBackend.PreparedOperation PreparedCreate(
        string operationId,
        Guid objectId,
        string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement.Clone();
        var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
        return new LiveDocumentBackend.PreparedOperation(
            new TypedOperation(
                operationId,
                OperationKind.CreateComponent,
                AdapterOwner.Cordyceps,
                Array.Empty<ResourceAddress>(),
                [resource],
                true,
                $"operations/{operationId}.json"),
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.create",
            arguments,
            Array.Empty<byte>(),
            "sha");
    }

    private readonly record struct TestRect(float X, float Y, float Width, float Height)
    {
        public float RightEdge => X + Width;

        public float BottomEdge => Y + Height;
    }

    private static TestRect RectOf(CanvasPoint pivot) =>
        new(pivot.X - (EstimatedWidth / 2f), pivot.Y - (EstimatedHeight / 2f), EstimatedWidth, EstimatedHeight);

    private static TestRect ExistingRectOf(CanvasObjectState item)
    {
        if (item.BoundsOrigin is { } origin)
        {
            return new TestRect(origin.X, origin.Y, item.Bounds.Width, item.Bounds.Height);
        }
        return new TestRect(
            item.Pivot.X - (item.Bounds.Width / 2f),
            item.Pivot.Y - (item.Bounds.Height / 2f),
            item.Bounds.Width,
            item.Bounds.Height);
    }

    private static bool Overlaps(TestRect a, TestRect b) =>
        a.X < b.RightEdge && a.RightEdge > b.X &&
        a.Y < b.BottomEdge && a.BottomEdge > b.Y;
}
