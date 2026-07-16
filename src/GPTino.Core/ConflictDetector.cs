using GPTino.Contracts;

namespace GPTino.Core;

public enum ConflictKind
{
    TargetMismatch,
    Exclusive,
    Delete,
    WriteWrite,
    ReadWrite,
    Stale,
    ManualDrift,
    Unmanaged,
}

public sealed record ChangeConflict(
    ConflictKind Kind,
    Guid LeftChangeSetId,
    Guid? RightChangeSetId,
    ResourceAddress? Resource,
    string Message);

public sealed class ConflictDetector
{
    public IReadOnlyList<ChangeConflict> Detect(ChangeSet left, ChangeSet right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        List<ChangeConflict> conflicts = [];

        if (left.ProjectId != right.ProjectId)
        {
            conflicts.Add(new(
                ConflictKind.TargetMismatch,
                left.ChangeSetId,
                right.ChangeSetId,
                null,
                "ChangeSets belong to different projects."));
            return conflicts;
        }

        if (IsExclusive(left) || IsExclusive(right))
        {
            conflicts.Add(new(
                ConflictKind.Exclusive,
                left.ChangeSetId,
                right.ChangeSetId,
                new ResourceAddress(ResourceKind.Document, left.ProjectId.ToString("N")),
                "A document-global operation is exclusive."));
            return conflicts;
        }

        var leftReads = ReadAddresses(left);
        var leftWrites = WriteAddresses(left);
        var rightReads = ReadAddresses(right);
        var rightWrites = WriteAddresses(right);

        foreach (var (leftAddress, rightAddress) in OverlappingPairs(
                     leftWrites,
                     rightWrites,
                     WriteDomainsOverlap))
        {
            var kind = HasDeleteTouch(left, leftAddress) || HasDeleteTouch(right, rightAddress)
                ? ConflictKind.Delete
                : ConflictKind.WriteWrite;
            AddUnique(conflicts, new(
                kind,
                left.ChangeSetId,
                right.ChangeSetId,
                MostSpecific(leftAddress, rightAddress),
                kind == ConflictKind.Delete
                    ? "A delete overlaps another write."
                    : "Both ChangeSets write the same resource field."));
        }

        foreach (var (read, write) in OverlappingPairs(leftReads, rightWrites))
        {
            var kind = HasDeleteTouch(right, write) ? ConflictKind.Delete : ConflictKind.ReadWrite;
            AddUnique(conflicts, new(
                kind,
                left.ChangeSetId,
                right.ChangeSetId,
                MostSpecific(read, write),
                kind == ConflictKind.Delete
                    ? "The right ChangeSet deletes data read by the left ChangeSet."
                    : "The right ChangeSet invalidates data read by the left ChangeSet."));
        }

        foreach (var (read, write) in OverlappingPairs(rightReads, leftWrites))
        {
            var kind = HasDeleteTouch(left, write) ? ConflictKind.Delete : ConflictKind.ReadWrite;
            AddUnique(conflicts, new(
                kind,
                left.ChangeSetId,
                right.ChangeSetId,
                MostSpecific(read, write),
                kind == ConflictKind.Delete
                    ? "The left ChangeSet deletes data read by the right ChangeSet."
                    : "The left ChangeSet invalidates data read by the right ChangeSet."));
        }

        return conflicts;
    }

    public IReadOnlyList<ChangeConflict> ValidateAgainstSnapshot(
        ChangeSet changeSet,
        StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(snapshot);
        List<ChangeConflict> conflicts = [];

        if (changeSet.ProjectId != snapshot.ProjectId)
        {
            conflicts.Add(new(
                ConflictKind.TargetMismatch,
                changeSet.ChangeSetId,
                null,
                null,
                "The snapshot belongs to a different project."));
            return conflicts;
        }

        foreach (var expectation in changeSet.ReadSet.Concat(changeSet.WriteSet).Distinct())
        {
            var candidates = snapshot.Resources.Where(resource =>
                Overlaps(resource.Resource, expectation.Resource));
            var actual = candidates.LastOrDefault(resource =>
                resource.Resource.Kind == expectation.Resource.Kind) ??
                candidates.LastOrDefault();
            if (expectation.ExpectsAbsence)
            {
                if (actual is not null)
                {
                    AddUnique(conflicts, new(
                        ConflictKind.Stale,
                        changeSet.ChangeSetId,
                        null,
                        expectation.Resource,
                        "The resource was expected to be absent but now exists."));
                }
                continue;
            }
            if (actual is null)
            {
                AddUnique(conflicts, new(
                    ConflictKind.Stale,
                    changeSet.ChangeSetId,
                    null,
                    expectation.Resource,
                    "The expected resource is absent from the current snapshot."));
                continue;
            }

            if (actual.TrackingState == TrackingState.Drifted)
            {
                AddUnique(conflicts, new(
                    ConflictKind.ManualDrift,
                    changeSet.ChangeSetId,
                    null,
                    expectation.Resource,
                    "The resource has overlapping manual drift."));
                continue;
            }

            if (actual.TrackingState is TrackingState.Untracked or TrackingState.Unsupported)
            {
                AddUnique(conflicts, new(
                    ConflictKind.Unmanaged,
                    changeSet.ChangeSetId,
                    null,
                    expectation.Resource,
                    $"The resource tracking state is {actual.TrackingState}."));
                continue;
            }

            if (!string.Equals(
                    expectation.ExpectedFingerprint,
                    actual.Fingerprint,
                    StringComparison.Ordinal))
            {
                AddUnique(conflicts, new(
                    ConflictKind.Stale,
                    changeSet.ChangeSetId,
                    null,
                    expectation.Resource,
                    "The resource fingerprint changed after the base snapshot."));
            }
        }

        return conflicts;
    }

    public static bool Overlaps(ResourceAddress left, ResourceAddress right) =>
        KindsOverlap(left.Kind, right.Kind) &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        (left.Field == "*" || right.Field == "*" ||
         string.Equals(left.Field, right.Field, StringComparison.Ordinal));

    private static bool KindsOverlap(ResourceKind left, ResourceKind right)
    {
        if (left == right)
        {
            return true;
        }

        return IsParentChild(
                left,
                right,
                ResourceKind.GrasshopperComponent,
                ResourceKind.GrasshopperComponentSource,
                ResourceKind.GrasshopperComponentIo,
                ResourceKind.GrasshopperComponentValue,
                ResourceKind.GrasshopperComponentLayout) ||
            IsParentChild(
                left,
                right,
                ResourceKind.RhinoObject,
                ResourceKind.RhinoObjectGeometry,
                ResourceKind.RhinoObjectAttributes);
    }

    public static bool SharesPythonStateFingerprint(ResourceAddress left, ResourceAddress right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        (left.Field == "*" || right.Field == "*" ||
         string.Equals(left.Field, right.Field, StringComparison.Ordinal)) &&
        IsPythonStateFingerprintKind(left.Kind) &&
        IsPythonStateFingerprintKind(right.Kind);

    private static bool IsPythonStateFingerprintKind(ResourceKind kind) => kind is
        ResourceKind.GrasshopperComponentSource or
        ResourceKind.GrasshopperComponentIo or
        ResourceKind.GrasshopperComponentValue;

    private static bool WriteDomainsOverlap(ResourceAddress left, ResourceAddress right) =>
        Overlaps(left, right) || SharesPythonStateFingerprint(left, right);

    private static bool IsParentChild(
        ResourceKind left,
        ResourceKind right,
        ResourceKind parent,
        params ResourceKind[] children) =>
        left == parent && children.Contains(right) ||
        right == parent && children.Contains(left);

    private static IReadOnlyList<ResourceAddress> ReadAddresses(ChangeSet changeSet) =>
        changeSet.ReadSet.Select(item => item.Resource)
            .Concat(changeSet.Operations.SelectMany(operation => operation.Reads))
            .Distinct()
            .ToArray();

    private static IReadOnlyList<ResourceAddress> WriteAddresses(ChangeSet changeSet) =>
        changeSet.WriteSet.Select(item => item.Resource)
            .Concat(changeSet.Operations.SelectMany(operation => operation.Writes))
            .Distinct()
            .ToArray();

    private static IEnumerable<(ResourceAddress Left, ResourceAddress Right)> OverlappingPairs(
        IEnumerable<ResourceAddress> left,
        IEnumerable<ResourceAddress> right,
        Func<ResourceAddress, ResourceAddress, bool>? overlaps = null) =>
        from leftAddress in left
        from rightAddress in right
        where (overlaps ?? Overlaps)(leftAddress, rightAddress)
        select (leftAddress, rightAddress);

    private static bool IsExclusive(ChangeSet changeSet) =>
        changeSet.Operations.Any(operation =>
            operation.Kind is OperationKind.DocumentGlobal or OperationKind.SetSolverState);

    private static bool HasDeleteTouch(ChangeSet changeSet, ResourceAddress address) =>
        changeSet.Operations.Any(operation =>
            operation.Kind is OperationKind.DeleteComponent or OperationKind.DeleteRhinoObject &&
            operation.Writes.Any(write => Overlaps(write, address)));

    private static ResourceAddress MostSpecific(ResourceAddress left, ResourceAddress right) =>
        left.Field == "*" && right.Field != "*" ? right : left;

    private static void AddUnique(List<ChangeConflict> conflicts, ChangeConflict candidate)
    {
        if (!conflicts.Any(existing =>
                existing.Kind == candidate.Kind &&
                Equals(existing.Resource, candidate.Resource)))
        {
            conflicts.Add(candidate);
        }
    }
}
