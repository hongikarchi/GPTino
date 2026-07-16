using GPTino.Contracts;

namespace GPTino.Core.Tests;

internal static class TestData
{
    public static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public static ResourceAddress Resource(
        string id = "component-a",
        string field = "value",
        ResourceKind kind = ResourceKind.GrasshopperComponentValue) =>
        new(kind, id, field);

    public static TypedOperation Operation(
        OperationKind kind = OperationKind.SetValue,
        ResourceAddress? resource = null,
        bool reversible = true)
    {
        resource ??= Resource();
        var isRead = kind == OperationKind.Read;
        return new(
            Guid.NewGuid().ToString("N"),
            kind,
            AdapterOwner.Cordyceps,
            isRead ? [resource] : [],
            isRead ? [] : [resource],
            reversible);
    }

    public static ChangeSet ChangeSet(
        Guid sessionId,
        IReadOnlyList<TypedOperation>? operations = null,
        IReadOnlyList<Guid>? dependencies = null,
        IReadOnlyList<ResourceExpectation>? reads = null,
        IReadOnlyList<ResourceExpectation>? writes = null,
        Guid? projectId = null) =>
        new(
            Guid.NewGuid(),
            projectId ?? ProjectId,
            sessionId,
            1,
            "abc123",
            dependencies ?? [],
            reads ?? [],
            writes ?? [],
            operations ?? (reads is not null || writes is not null ? [] : [Operation()]),
            [],
            [],
            DateTimeOffset.UtcNow);

    public static QueuedJob Job(
        Guid sessionId,
        long sequence,
        IReadOnlyList<Guid>? dependencies = null,
        bool recovery = false,
        IReadOnlyList<TypedOperation>? operations = null) =>
        new(
            Guid.NewGuid(),
            ChangeSet(sessionId, operations, dependencies),
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            recovery);

    public static RoutingRequest RoutingRequest(
        IReadOnlyList<TypedOperation> operations,
        QualityPolicy quality = QualityPolicy.Auto,
        RiskFlags risks = RiskFlags.None,
        bool exact = true,
        int resourceCount = 1,
        bool recovery = false,
        string? pinnedModel = null) =>
        new(
            Guid.NewGuid(),
            quality,
            recovery,
            exact,
            resourceCount,
            operations,
            risks,
            pinnedModel);
}
