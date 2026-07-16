using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class ConflictDetectorTests
{
    private readonly ConflictDetector _detector = new();

    [Fact]
    public void SourceAndLayoutFieldsCanProceedIndependently()
    {
        var session = Guid.NewGuid();
        var source = TestData.Resource("component", "source", ResourceKind.GrasshopperComponent);
        var layout = TestData.Resource("component", "layout", ResourceKind.GrasshopperComponent);
        var left = TestData.ChangeSet(session, writes: [new(source, "a")]);
        var right = TestData.ChangeSet(session, writes: [new(layout, "b")]);

        var conflicts = _detector.Detect(left, right);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void SameWriteFieldProducesWriteWriteConflict()
    {
        var resource = TestData.Resource();
        var left = TestData.ChangeSet(Guid.NewGuid(), writes: [new(resource, "a")]);
        var right = TestData.ChangeSet(Guid.NewGuid(), writes: [new(resource, "a")]);

        var conflict = Assert.Single(_detector.Detect(left, right));

        Assert.Equal(ConflictKind.WriteWrite, conflict.Kind);
        Assert.Equal(resource, conflict.Resource);
    }

    [Fact]
    public void WholeResourceWildcardOverlapsSpecificField()
    {
        var whole = TestData.Resource("component", "*", ResourceKind.GrasshopperComponent);
        var source = TestData.Resource("component", "source", ResourceKind.GrasshopperComponent);
        var left = TestData.ChangeSet(Guid.NewGuid(), writes: [new(whole, "a")]);
        var right = TestData.ChangeSet(Guid.NewGuid(), reads: [new(source, "a")]);

        var conflict = Assert.Single(_detector.Detect(left, right));

        Assert.Equal(ConflictKind.ReadWrite, conflict.Kind);
        Assert.Equal(source, conflict.Resource);
    }

    [Fact]
    public void DeleteOverlapIsHardConflict()
    {
        var resource = TestData.Resource("object", "*", ResourceKind.RhinoObject);
        var delete = TestData.ChangeSet(
            Guid.NewGuid(),
            [TestData.Operation(OperationKind.DeleteRhinoObject, resource)],
            writes: [new(resource, "a")]);
        var update = TestData.ChangeSet(Guid.NewGuid(), writes: [new(resource, "a")]);

        var conflict = Assert.Single(_detector.Detect(delete, update));

        Assert.Equal(ConflictKind.Delete, conflict.Kind);
    }

    [Fact]
    public void DeleteAlsoHardConflictsWithAReader()
    {
        var resource = TestData.Resource("object", "*", ResourceKind.RhinoObject);
        var delete = TestData.ChangeSet(
            Guid.NewGuid(),
            [TestData.Operation(OperationKind.DeleteRhinoObject, resource)],
            writes: [new(resource, "a")]);
        var reader = TestData.ChangeSet(Guid.NewGuid(), reads: [new(resource, "a")]);

        var conflict = Assert.Single(_detector.Detect(delete, reader));

        Assert.Equal(ConflictKind.Delete, conflict.Kind);
    }

    [Fact]
    public void RhinoWholeObjectDeleteConflictsWithGeometryChildDomain()
    {
        var objectId = Guid.NewGuid().ToString("D");
        var whole = new ResourceAddress(ResourceKind.RhinoObject, objectId);
        var geometry = new ResourceAddress(ResourceKind.RhinoObjectGeometry, objectId);
        var delete = TestData.ChangeSet(
            Guid.NewGuid(),
            [TestData.Operation(OperationKind.DeleteRhinoObject, whole)],
            writes: [new(whole, "a")]);
        var geometryWriter = TestData.ChangeSet(Guid.NewGuid(), writes: [new(geometry, "a")]);

        var conflict = Assert.Single(_detector.Detect(delete, geometryWriter));

        Assert.Equal(ConflictKind.Delete, conflict.Kind);
    }

    [Fact]
    public void ComponentParentConflictsWithLayoutAndWireifyFingerprintSiblingsConflict()
    {
        var componentId = Guid.NewGuid().ToString("D");
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, componentId);
        var layout = new ResourceAddress(ResourceKind.GrasshopperComponentLayout, componentId);
        var source = new ResourceAddress(ResourceKind.GrasshopperComponentSource, componentId);
        var io = new ResourceAddress(ResourceKind.GrasshopperComponentIo, componentId);
        var value = new ResourceAddress(ResourceKind.GrasshopperComponentValue, componentId);

        var parentChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(parent, "a")]);
        var layoutChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(layout, "a")]);
        Assert.Single(_detector.Detect(parentChange, layoutChange));

        var sourceChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(source, "a")]);
        var ioChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(io, "a")]);
        var valueChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(value, "a")]);
        Assert.Single(_detector.Detect(sourceChange, valueChange));
        Assert.Single(_detector.Detect(sourceChange, ioChange));
        Assert.Single(_detector.Detect(ioChange, valueChange));

        Assert.Empty(_detector.Detect(sourceChange, layoutChange));
        var otherSource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            Guid.NewGuid().ToString("D"));
        var otherChange = TestData.ChangeSet(Guid.NewGuid(), writes: [new(otherSource, "a")]);
        Assert.Empty(_detector.Detect(sourceChange, otherChange));
    }

    [Fact]
    public void DocumentGlobalOperationIsExclusiveEvenWithoutMatchingResources()
    {
        var global = TestData.ChangeSet(
            Guid.NewGuid(),
            [TestData.Operation(OperationKind.SetSolverState)]);
        var other = TestData.ChangeSet(Guid.NewGuid());

        var conflict = Assert.Single(_detector.Detect(global, other));

        Assert.Equal(ConflictKind.Exclusive, conflict.Kind);
    }

    [Fact]
    public void SnapshotValidationUsesTouchedFingerprintsNotGlobalRevision()
    {
        var touched = TestData.Resource("a");
        var unrelated = TestData.Resource("b");
        var change = TestData.ChangeSet(
            Guid.NewGuid(),
            reads: [new(touched, "same")]);
        var target = new DocumentRuntime(
            TestData.ProjectId,
            42,
            DateTimeOffset.UnixEpoch,
            7,
            Guid.NewGuid(),
            "model.3dm",
            "definition.gh",
            1);
        var snapshot = new StateSnapshot(
            TestData.ProjectId,
            999,
            "new-head",
            DateTimeOffset.UtcNow,
            target,
            [
                new(touched, "same"),
                new(unrelated, "changed", TrackingState.Drifted),
            ]);

        var conflicts = _detector.ValidateAgainstSnapshot(change, snapshot);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void SnapshotWildcardFingerprintSatisfiesSpecificFieldExpectation()
    {
        var whole = TestData.Resource("component", "*", ResourceKind.GrasshopperComponent);
        var source = TestData.Resource("component", "source", ResourceKind.GrasshopperComponent);
        var change = TestData.ChangeSet(Guid.NewGuid(), reads: [new(source, "same")]);
        var target = new DocumentRuntime(
            TestData.ProjectId,
            42,
            DateTimeOffset.UnixEpoch,
            7,
            Guid.NewGuid(),
            "model.3dm",
            "definition.gh",
            1);
        var snapshot = new StateSnapshot(
            TestData.ProjectId,
            2,
            null,
            DateTimeOffset.UtcNow,
            target,
            [new(whole, "same")]);

        Assert.Empty(_detector.ValidateAgainstSnapshot(change, snapshot));
    }

    [Fact]
    public void AbsenceSentinelPassesOnlyWhileResourceIsActuallyAbsent()
    {
        var resource = TestData.Resource("new-component", "*", ResourceKind.GrasshopperComponent);
        var change = TestData.ChangeSet(
            Guid.NewGuid(),
            writes: [new(resource, ResourceExpectation.AbsentFingerprint)]);
        var target = new DocumentRuntime(
            TestData.ProjectId,
            42,
            DateTimeOffset.UnixEpoch,
            7,
            Guid.NewGuid(),
            "model.3dm",
            "definition.gh",
            1);
        var absent = new StateSnapshot(
            TestData.ProjectId,
            2,
            null,
            DateTimeOffset.UtcNow,
            target,
            Array.Empty<ResourceFingerprint>());
        var nowPresent = absent with
        {
            Resources = [new ResourceFingerprint(resource, "existing")]
        };

        Assert.Empty(_detector.ValidateAgainstSnapshot(change, absent));
        var conflict = Assert.Single(_detector.ValidateAgainstSnapshot(change, nowPresent));
        Assert.Equal(ConflictKind.Stale, conflict.Kind);
        Assert.Contains("expected to be absent", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TouchedFingerprintChangeAndManualDriftAreReported()
    {
        var stale = TestData.Resource("stale");
        var drift = TestData.Resource("drift");
        var change = TestData.ChangeSet(
            Guid.NewGuid(),
            reads: [new(stale, "old")],
            writes: [new(drift, "old")]);
        var target = new DocumentRuntime(
            TestData.ProjectId,
            42,
            DateTimeOffset.UnixEpoch,
            7,
            Guid.NewGuid(),
            "model.3dm",
            "definition.gh",
            1);
        var snapshot = new StateSnapshot(
            TestData.ProjectId,
            2,
            null,
            DateTimeOffset.UtcNow,
            target,
            [
                new(stale, "new"),
                new(drift, "old", TrackingState.Drifted),
            ]);

        var conflicts = _detector.ValidateAgainstSnapshot(change, snapshot);

        Assert.Contains(conflicts, item => item.Kind == ConflictKind.Stale && item.Resource == stale);
        Assert.Contains(conflicts, item => item.Kind == ConflictKind.ManualDrift && item.Resource == drift);
    }

    [Theory]
    [InlineData(TrackingState.Untracked)]
    [InlineData(TrackingState.Unsupported)]
    public void UnmanagedTouchedResourceIsRejected(TrackingState trackingState)
    {
        var resource = TestData.Resource();
        var change = TestData.ChangeSet(Guid.NewGuid(), reads: [new(resource, "same")]);
        var target = new DocumentRuntime(
            TestData.ProjectId,
            42,
            DateTimeOffset.UnixEpoch,
            7,
            Guid.NewGuid(),
            "model.3dm",
            "definition.gh",
            1);
        var snapshot = new StateSnapshot(
            TestData.ProjectId,
            2,
            null,
            DateTimeOffset.UtcNow,
            target,
            [new(resource, "same", trackingState)]);

        var conflict = Assert.Single(_detector.ValidateAgainstSnapshot(change, snapshot));

        Assert.Equal(ConflictKind.Unmanaged, conflict.Kind);
    }
}
