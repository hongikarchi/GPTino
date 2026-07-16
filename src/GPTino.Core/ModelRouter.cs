using GPTino.Contracts;

namespace GPTino.Core;

public sealed class ModelRoutingException(string message) : InvalidOperationException(message);

public sealed class ModelRouter(FastSafePolicy fastSafePolicy)
{
    public const string Version = "gptino-router-v1";

    private static readonly RiskFlags ComplexRisks =
        RiskFlags.UnknownTarget |
        RiskFlags.AmbiguousTarget |
        RiskFlags.WireCycle |
        RiskFlags.TypeMismatch |
        RiskFlags.PythonChange |
        RiskFlags.IoSchemaChange |
        RiskFlags.GeometryTopologyChange |
        RiskFlags.Delete |
        RiskFlags.Bake |
        RiskFlags.SolverGlobal |
        RiskFlags.OpaquePluginState |
        RiskFlags.ExternalReference |
        RiskFlags.ManualDrift |
        RiskFlags.CrossSessionDependency |
        RiskFlags.RuntimeFailure |
        RiskFlags.MultiDocument |
        RiskFlags.NonReversible |
        RiskFlags.Unsupported |
        RiskFlags.UnqualifiedModelVersion |
        RiskFlags.LargeScope;

    public RoutingDecision Route(RoutingRequest request, IEnumerable<ModelDescriptor> models)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(models);

        var available = models.Where(model => model.Available).ToArray();
        var fastSafe = fastSafePolicy.Evaluate(request);
        var taskClass = Classify(request, fastSafe);
        var classFloor = ProfileFor(taskClass);
        var requested = RequestedProfile(request.RequestedQuality, classFloor);
        var required = MaxProfile(classFloor, requested);
        List<RoutingEscalation> escalations = [];
        List<string> evidence =
        [
            $"Task classified as {taskClass}.",
            $"Capability floor is {classFloor}.",
        ];

        if (ProfileRank(requested) < ProfileRank(classFloor))
        {
            escalations.Add(new(requested, classFloor, "The requested quality was below the deterministic capability floor."));
        }

        if (taskClass == TaskClass.SimpleDeterministicWrite)
        {
            evidence.Add("All live writes passed the FastSafe allowlist.");
        }
        else if (request.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind)))
        {
            evidence.AddRange(fastSafe.Reasons);
        }

        var model = SelectModel(request, required, available);
        var effective = MaxProfile(required, ProfileForCapability(model.CapabilityTier, request.IsRecovery));
        if (ProfileRank(effective) > ProfileRank(required))
        {
            escalations.Add(new(required, effective, $"Model {model.Id} provides a higher capability tier."));
        }

        evidence.Add($"Selected available model {model.Id} at tier {model.CapabilityTier}.");

        return new(
            Version,
            taskClass,
            request.RiskFlags,
            requested,
            effective,
            model.Id,
            ReasoningFor(effective),
            fastSafe.Eligible || taskClass == TaskClass.ReadOnly ? 1.0 : 0.95,
            evidence,
            escalations);
    }

    private static TaskClass Classify(RoutingRequest request, FastSafeEvaluation fastSafe)
    {
        if (request.IsRecovery)
        {
            return TaskClass.Recovery;
        }

        var hasWrites = request.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind));
        if (!hasWrites)
        {
            return TaskClass.ReadOnly;
        }

        if ((request.RiskFlags & ComplexRisks) != RiskFlags.None ||
            request.Operations.Any(operation => operation.Kind is
                OperationKind.SetComponentIo or
                OperationKind.ConvertSocket or
                OperationKind.UpdatePythonSource or
                OperationKind.ExecutePython or
                OperationKind.CreateComponent or
                OperationKind.DeleteComponent or
                OperationKind.SetSolverState or
                OperationKind.CreateRhinoPrimitive or
                OperationKind.CreateRhinoObject or
                OperationKind.ModifyRhinoObject or
                OperationKind.DeleteRhinoObject or
                OperationKind.BakeGeometry or
                OperationKind.DocumentGlobal))
        {
            return TaskClass.ComplexWrite;
        }

        return fastSafe.Eligible
            ? TaskClass.SimpleDeterministicWrite
            : TaskClass.StandardWrite;
    }

    private static ModelProfile RequestedProfile(QualityPolicy quality, ModelProfile automatic) => quality switch
    {
        QualityPolicy.Auto => automatic,
        QualityPolicy.Fast => ModelProfile.FastSafe,
        QualityPolicy.Standard => ModelProfile.Standard,
        QualityPolicy.Deep => ModelProfile.HighAssurance,
        QualityPolicy.Pinned => automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
    };

    private static ModelDescriptor SelectModel(
        RoutingRequest request,
        ModelProfile required,
        IReadOnlyCollection<ModelDescriptor> models)
    {
        var requiredTier = RequiredTier(required);
        var needsWrite = request.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind));

        if (request.RequestedQuality == QualityPolicy.Pinned)
        {
            if (string.IsNullOrWhiteSpace(request.PinnedModelId))
            {
                throw new ModelRoutingException("Pinned quality requires a model ID.");
            }

            var pinned = models.FirstOrDefault(model =>
                string.Equals(model.Id, request.PinnedModelId, StringComparison.Ordinal));
            if (pinned is null)
            {
                throw new ModelRoutingException($"Pinned model '{request.PinnedModelId}' is unavailable.");
            }

            EnsureEligible(pinned, requiredTier, needsWrite);
            return pinned;
        }

        var candidates = models
            .Where(model => model.CapabilityTier >= requiredTier)
            .Where(model => !needsWrite || model.QualifiedForLiveWrites)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new ModelRoutingException($"No available model satisfies profile {required}.");
        }

        if (request.IsRecovery)
        {
            return candidates
                .OrderByDescending(model => model.CapabilityTier)
                .ThenBy(model => model.Id, StringComparer.Ordinal)
                .First();
        }

        return candidates
            .OrderBy(model => model.CapabilityTier)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .First();
    }

    private static void EnsureEligible(
        ModelDescriptor model,
        ModelCapabilityTier requiredTier,
        bool needsWrite)
    {
        if (model.CapabilityTier < requiredTier)
        {
            throw new ModelRoutingException(
                $"Pinned model '{model.Id}' is below the required capability tier {requiredTier}.");
        }

        if (needsWrite && !model.QualifiedForLiveWrites)
        {
            throw new ModelRoutingException($"Pinned model '{model.Id}' is not qualified for live writes.");
        }
    }

    private static ModelProfile ProfileFor(TaskClass taskClass) => taskClass switch
    {
        TaskClass.ReadOnly => ModelProfile.ReadFast,
        TaskClass.SimpleDeterministicWrite => ModelProfile.FastSafe,
        TaskClass.StandardWrite => ModelProfile.Standard,
        TaskClass.ComplexWrite => ModelProfile.HighAssurance,
        TaskClass.Recovery => ModelProfile.Recovery,
        _ => throw new ArgumentOutOfRangeException(nameof(taskClass), taskClass, null),
    };

    private static ModelProfile ProfileForCapability(ModelCapabilityTier capability, bool recovery) =>
        recovery
            ? ModelProfile.Recovery
            : capability switch
            {
                ModelCapabilityTier.ReadOnly => ModelProfile.ReadFast,
                ModelCapabilityTier.FastWrite => ModelProfile.FastSafe,
                ModelCapabilityTier.StandardWrite => ModelProfile.Standard,
                ModelCapabilityTier.DeepReasoning or ModelCapabilityTier.Recovery => ModelProfile.HighAssurance,
                _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
            };

    private static ModelCapabilityTier RequiredTier(ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast => ModelCapabilityTier.ReadOnly,
        ModelProfile.FastSafe => ModelCapabilityTier.FastWrite,
        ModelProfile.Standard => ModelCapabilityTier.StandardWrite,
        ModelProfile.HighAssurance or ModelProfile.Recovery => ModelCapabilityTier.DeepReasoning,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static ReasoningEffort ReasoningFor(ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast or ModelProfile.FastSafe => ReasoningEffort.Low,
        ModelProfile.Standard => ReasoningEffort.Medium,
        ModelProfile.HighAssurance => ReasoningEffort.ExtraHigh,
        ModelProfile.Recovery => ReasoningEffort.Maximum,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static ModelProfile MaxProfile(ModelProfile left, ModelProfile right) =>
        ProfileRank(left) >= ProfileRank(right) ? left : right;

    private static int ProfileRank(ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast => 0,
        ModelProfile.FastSafe => 1,
        ModelProfile.Standard => 2,
        ModelProfile.HighAssurance => 3,
        ModelProfile.Recovery => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };
}
