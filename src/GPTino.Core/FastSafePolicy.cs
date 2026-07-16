using GPTino.Contracts;

namespace GPTino.Core;

public static class OperationSemantics
{
    public static bool IsWrite(OperationKind kind) => kind is not (
        OperationKind.Read or OperationKind.ReadRuntimeMessages);
}

public sealed class FastSafePolicy
{
    private const int MaximumOperations = 3;
    private const int MaximumResources = 5;

    private static readonly HashSet<OperationKind> AllowedWrites =
    [
        OperationKind.MoveComponent,
        OperationKind.ConnectWire,
        OperationKind.DisconnectWire,
        OperationKind.TransformRhinoObject,
    ];

    public FastSafeEvaluation Evaluate(RoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> reasons = [];
        var writes = request.Operations.Where(operation => OperationSemantics.IsWrite(operation.Kind)).ToArray();
        var declaredResources = request.Operations
            .SelectMany(operation => operation.Reads.Concat(operation.Writes))
            .Distinct()
            .Count();
        var resourceCount = Math.Max(request.ResourceCount, declaredResources);

        if (!request.TargetIsExact)
        {
            reasons.Add("The target is not uniquely identified.");
        }

        if (writes.Length == 0)
        {
            reasons.Add("FastSafe is reserved for executable writes.");
        }

        if (writes.Length > MaximumOperations)
        {
            reasons.Add($"FastSafe allows at most {MaximumOperations} write operations.");
        }

        if (request.ResourceCount < 0)
        {
            reasons.Add("The resource count cannot be negative.");
        }

        if (resourceCount > MaximumResources)
        {
            reasons.Add($"FastSafe allows at most {MaximumResources} touched resources.");
        }

        if (request.RiskFlags != RiskFlags.None)
        {
            reasons.Add($"Risk flags require escalation: {request.RiskFlags}.");
        }

        foreach (var operation in writes)
        {
            if (!AllowedWrites.Contains(operation.Kind))
            {
                reasons.Add($"Operation {operation.Kind} is not in the FastSafe allowlist.");
            }

            if (!operation.Reversible)
            {
                reasons.Add($"Operation {operation.OperationId} has no deterministic rollback.");
            }
        }

        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
