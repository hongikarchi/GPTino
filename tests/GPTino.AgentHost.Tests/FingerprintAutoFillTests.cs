using GPTino.AgentHost.Runtime;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Unit coverage for the gptino:auto fingerprint resolution (roadmap #1). The safety-critical rule is that
/// auto is filled from live state ONLY for a session's own unchanged self-sequential write; every foreign or
/// unknown case must be REFUSED (returned as a conflict) so the existing Blocked path stops the job.
/// </summary>
public sealed class FingerprintAutoFillTests
{
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static ResourceAddress Source(string id) =>
        new(ResourceKind.GrasshopperComponentSource, id, "*");

    private static string Key(ResourceAddress resource) =>
        $"{resource.Kind}:{resource.Id}:{resource.Field}";

    private static ChangeSet ChangeSetWith(Guid session, params ResourceExpectation[] writes) =>
        new(
            Guid.NewGuid(),
            ProjectId,
            session,
            ResourceExpectation.AutoBaseRevision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            writes,
            Array.Empty<TypedOperation>(),
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UnixEpoch);

    private static StateSnapshot SnapshotWith(params ResourceFingerprint[] resources) =>
        new(
            ProjectId,
            5,
            null,
            DateTimeOffset.UnixEpoch,
            new DocumentRuntime(ProjectId, 42, DateTimeOffset.UnixEpoch, 7, Guid.NewGuid(), "model.3dm", "definition.gh", 1),
            resources);

    private static Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry> Ledger(
        ResourceAddress resource, string fingerprint, Guid session, long revision = 4) =>
        new(StringComparer.Ordinal) { [Key(resource)] = new(resource, fingerprint, session, revision) };

    private static Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry> LedgerOf(
        params (ResourceAddress Resource, string Fingerprint, Guid Session)[] entries)
    {
        var ledger = new Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry>(StringComparer.Ordinal);
        foreach (var (resource, fingerprint, session) in entries)
        {
            ledger[Key(resource)] = new(resource, fingerprint, session, 3);
        }
        return ledger;
    }

    [Fact]
    public void SelfSequentialUnchangedResolvesToLiveFingerprint()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000aa");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source,"fp-1"));
        var ledger = Ledger(source, "fp-1", session);

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.Empty(conflicts);
        Assert.Equal("fp-1", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
        Assert.False(Assert.Single(resolved.WriteSet).IsAuto);
    }

    [Fact]
    public void ForeignSessionWriteIsRefused()
    {
        var session = Guid.NewGuid();
        var otherSession = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000bb");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source,"fp-2"));
        var ledger = Ledger(source, "fp-2", otherSession); // last writer was someone else

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        var message = Assert.Single(conflicts);
        Assert.Contains("another session", message, StringComparison.OrdinalIgnoreCase);
        // The original (still-auto) ChangeSet is returned so the caller Blocks rather than silently applying.
        Assert.True(Assert.Single(resolved.WriteSet).IsAuto);
    }

    [Fact]
    public void ManualDriftIsRefused()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000cc");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source,"fp-live")); // live moved
        var ledger = Ledger(source, "fp-old", session); // this session last committed a different fp

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        var message = Assert.Single(conflicts);
        Assert.Contains("drifted", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fp-live", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceNeverWrittenByThisSessionIsRefused()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000dd");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source,"fp-3"));
        var ledger = new Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry>(StringComparer.Ordinal);

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.Contains("has not committed it", Assert.Single(conflicts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsentLiveResourceIsRefused()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000ee");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(); // resource not present live
        var ledger = Ledger(source, "fp-4", session);

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.Contains("absent", Assert.Single(conflicts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstSubDomainWriteResolvesWhenParentIsOwnedAndUnchanged()
    {
        // The first setComponentIo after createComponent: the Io sub-domain has no ledger row of its own and
        // is absent from the fresh snapshot's sub-domain rows, but the parent component this session created is
        // in the ledger and its own fingerprint is unchanged (no foreign write) -> the sub-domain auto resolves
        // to its live fingerprint.
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000111";
        var io = new ResourceAddress(ResourceKind.GrasshopperComponentIo, id, "*");
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(io, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-fp"),
            new ResourceFingerprint(io, "io-fp"));
        var ledger = LedgerOf((parent, "parent-fp", session)); // only the parent, as right after createComponent

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.Empty(conflicts);
        Assert.Equal("io-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
    }

    [Fact]
    public void SubDomainViaParentIsRefusedWhenParentFingerprintMoved()
    {
        // A foreign session write (or manual edit) to the component moves the PARENT fingerprint, so the
        // parent-ownership fallback declines even though this session created the component.
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000222";
        var io = new ResourceAddress(ResourceKind.GrasshopperComponentIo, id, "*");
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(io, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-moved"),
            new ResourceFingerprint(io, "io-fp"));
        var ledger = LedgerOf((parent, "parent-old", session));

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.NotEmpty(conflicts);
    }

    [Fact]
    public void SubDomainViaParentIsRefusedWhenParentOwnedByAnotherSession()
    {
        var session = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000333";
        var io = new ResourceAddress(ResourceKind.GrasshopperComponentIo, id, "*");
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(io, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-fp"),
            new ResourceFingerprint(io, "io-fp"));
        var ledger = LedgerOf((parent, "parent-fp", other)); // parent last written by another session

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.NotEmpty(conflicts);
    }

    [Fact]
    public void ConcreteExpectationsPassThroughUntouched()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000ff");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source,"concrete-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(source,"different-live"));
        var ledger = new Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry>(StringComparer.Ordinal);

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, ledger);

        Assert.Empty(conflicts);
        Assert.Same(changeSet, resolved); // no auto anywhere -> identical instance returned
        Assert.Equal("concrete-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
    }
}
