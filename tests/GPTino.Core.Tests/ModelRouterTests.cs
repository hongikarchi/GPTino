using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class ModelRouterTests
{
    private static readonly ModelDescriptor[] Models =
    [
        new("read", ModelCapabilityTier.ReadOnly, QualifiedForLiveWrites: false),
        new("fast", ModelCapabilityTier.FastWrite),
        new("standard", ModelCapabilityTier.StandardWrite),
        new("deep", ModelCapabilityTier.DeepReasoning),
        new("recovery", ModelCapabilityTier.Recovery),
    ];

    private readonly FastSafePolicy _fastSafe = new();

    [Fact]
    public void FastSafeAcceptsOnlySmallExactReversibleAllowlistedWrites()
    {
        var eligible = _fastSafe.Evaluate(TestData.RoutingRequest(
            [TestData.Operation(OperationKind.MoveComponent)]));
        var ambiguous = _fastSafe.Evaluate(TestData.RoutingRequest(
            [TestData.Operation(OperationKind.MoveComponent)], exact: false));
        var sourceEdit = _fastSafe.Evaluate(TestData.RoutingRequest(
            [TestData.Operation(OperationKind.UpdatePythonSource)]));
        var irreversible = _fastSafe.Evaluate(TestData.RoutingRequest(
            [TestData.Operation(OperationKind.MoveComponent, reversible: false)]));

        Assert.True(eligible.Eligible);
        Assert.False(ambiguous.Eligible);
        Assert.False(sourceEdit.Eligible);
        Assert.False(irreversible.Eligible);
    }

    [Fact]
    public void FastSafeCountsOperationResourcesEvenWhenRequestUnderReportsThem()
    {
        var writes = Enumerable.Range(0, 6)
            .Select(index => TestData.Resource($"component-{index}"))
            .ToArray();
        var operation = new TypedOperation(
            "move-many",
            OperationKind.MoveComponent,
            AdapterOwner.Cordyceps,
            [],
            writes,
            true);

        var evaluation = _fastSafe.Evaluate(TestData.RoutingRequest(
            [operation],
            resourceCount: 1));

        Assert.False(evaluation.Eligible);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("5", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOnlyTaskUsesLowestAvailableReadModel()
    {
        var router = new ModelRouter(_fastSafe);

        var decision = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.Read)]), Models);

        Assert.Equal(TaskClass.ReadOnly, decision.TaskClass);
        Assert.Equal(ModelProfile.ReadFast, decision.EffectiveProfile);
        Assert.Equal("read", decision.EffectiveModel);
    }

    [Fact]
    public void RuntimeMessageReadIsReadOnlyButPythonExecutionRequiresDeepReasoning()
    {
        var router = new ModelRouter(_fastSafe);

        var read = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.ReadRuntimeMessages)]), Models);
        var execute = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.ExecutePython)]), Models);

        Assert.Equal(TaskClass.ReadOnly, read.TaskClass);
        Assert.Equal("read", read.EffectiveModel);
        Assert.Equal(TaskClass.ComplexWrite, execute.TaskClass);
        Assert.Equal("deep", execute.EffectiveModel);
    }

    [Fact]
    public void ExactMoveUsesFastWriteModelButNeverReadOnlyModel()
    {
        var router = new ModelRouter(_fastSafe);

        var decision = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.MoveComponent)]), Models);

        Assert.Equal(TaskClass.SimpleDeterministicWrite, decision.TaskClass);
        Assert.Equal(ModelProfile.FastSafe, decision.EffectiveProfile);
        Assert.Equal("fast", decision.EffectiveModel);
    }

    [Fact]
    public void ExactRhinoTransformMayUseFastWriteButPrimitiveCreationUsesDeepReasoning()
    {
        var router = new ModelRouter(_fastSafe);

        var transform = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.TransformRhinoObject)]), Models);
        var create = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.CreateRhinoPrimitive)]), Models);

        Assert.Equal(TaskClass.SimpleDeterministicWrite, transform.TaskClass);
        Assert.Equal("fast", transform.EffectiveModel);
        Assert.Equal(TaskClass.ComplexWrite, create.TaskClass);
        Assert.Equal("deep", create.EffectiveModel);
    }

    [Fact]
    public void ComplexTaskEscalatesExplicitFastRequest()
    {
        var router = new ModelRouter(_fastSafe);

        var decision = router.Route(
            TestData.RoutingRequest(
                [TestData.Operation(OperationKind.ModifyRhinoObject)],
                QualityPolicy.Fast,
                RiskFlags.GeometryTopologyChange),
            Models);

        Assert.Equal(TaskClass.ComplexWrite, decision.TaskClass);
        Assert.Equal(ModelProfile.HighAssurance, decision.EffectiveProfile);
        Assert.Equal("deep", decision.EffectiveModel);
        Assert.Contains(decision.EscalationHistory, item => item.To == ModelProfile.HighAssurance);
    }

    [Fact]
    public void UnqualifiedFastModelIsSkippedForLiveWrite()
    {
        var router = new ModelRouter(_fastSafe);
        ModelDescriptor[] models =
        [
            new("unsafe-fast", ModelCapabilityTier.FastWrite, QualifiedForLiveWrites: false),
            new("standard", ModelCapabilityTier.StandardWrite),
        ];

        var decision = router.Route(
            TestData.RoutingRequest([TestData.Operation(OperationKind.SetValue)]), models);

        Assert.Equal("standard", decision.EffectiveModel);
        Assert.Equal(ModelProfile.Standard, decision.EffectiveProfile);
    }

    [Fact]
    public void PinnedModelCannotLowerCapabilityFloor()
    {
        var router = new ModelRouter(_fastSafe);
        var request = TestData.RoutingRequest(
            [TestData.Operation(OperationKind.ModifyRhinoObject)],
            QualityPolicy.Pinned,
            RiskFlags.GeometryTopologyChange,
            pinnedModel: "fast");

        Assert.Throws<ModelRoutingException>(() => router.Route(request, Models));
    }

    [Fact]
    public void RecoverySelectsHighestAvailableCapability()
    {
        var router = new ModelRouter(_fastSafe);

        var decision = router.Route(
            TestData.RoutingRequest(
                [TestData.Operation(OperationKind.Read)],
                recovery: true),
            Models);

        Assert.Equal(TaskClass.Recovery, decision.TaskClass);
        Assert.Equal(ModelProfile.Recovery, decision.EffectiveProfile);
        Assert.Equal("recovery", decision.EffectiveModel);
        Assert.Equal(ReasoningEffort.Maximum, decision.Reasoning);
    }
}
