using System.Text.Json;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
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

    // The ledger is doc-scoped (in memory exactly like the durable rows): every lookup carries the
    // job's docKey, so entries recorded for a file-copied sibling document never satisfy it.
    private const string DocKey = "doc0000aaaa00001";

    private static ResourceAddress Source(string id) =>
        new(ResourceKind.GrasshopperComponentSource, id, "*");

    private static string Key(ResourceAddress resource) =>
        LiveDocumentBackend.ResourceLedgerKey(DocKey, resource);

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
            changeSet, snapshot, session, DocKey, ledger);

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
            changeSet, snapshot, session, DocKey, ledger);

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
            changeSet, snapshot, session, DocKey, ledger);

        var message = Assert.Single(conflicts);
        Assert.Contains("drifted", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fp-live", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedOriginEntryStillFillsAuto()
    {
        // W3 Finding 1(b) boundary: the origin marker restricts DELETE authorization only. A
        // side-effect (Observed) row is still this session's own last write of the resource, so
        // gptino:auto keeps resolving from it — origin must never break CAS filling/rebase.
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-0000000000af");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source, "fp-observed"));
        var ledger = new Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry>(StringComparer.Ordinal)
        {
            [Key(source)] = new(
                source, "fp-observed", session, 4, GPTino.AgentHost.Data.ResourceLedgerOrigin.Observed),
        };

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Empty(conflicts);
        Assert.Equal("fp-observed", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
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
            changeSet, snapshot, session, DocKey, ledger);

        var message = Assert.Single(conflicts);
        Assert.Contains("has not written it", message, StringComparison.OrdinalIgnoreCase);
        // The decline must carry the live fingerprint so the model resubmits directly (no read).
        Assert.Contains("fp-3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot_read", message, StringComparison.OrdinalIgnoreCase);
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
            changeSet, snapshot, session, DocKey, ledger);

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
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Empty(conflicts);
        Assert.Equal("io-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
    }

    [Fact]
    public void SourceAutoWithOnlyAParentRowIsRefused()
    {
        // The live-gate canary shape (20260807T175523Z): a source auto whose only ledger evidence
        // is the session-owned, unchanged PARENT row. The parent structure fingerprint hashes
        // nickname/sockets/wires — never the source text — so an unchanged parent proves NOTHING
        // about foreign/manual source edits. Source (and Value below) therefore require a DIRECT
        // ledger row; the parent fallback must refuse.
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000444";
        var source = Source(id);
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-fp"),
            new ResourceFingerprint(source, "source-fp"));
        var ledger = LedgerOf((parent, "parent-fp", session)); // parent owned AND unchanged

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Contains("has not written it", Assert.Single(conflicts), StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(resolved.WriteSet).IsAuto);
    }

    [Fact]
    public void ValueAutoWithOnlyAParentRowIsRefused()
    {
        // Same reasoning as the source case: slider values and python outputs are absent from the
        // parent structure hash, so a manual/foreign value edit is invisible to the fallback.
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000445";
        var value = Value(id);
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-fp"),
            new ResourceFingerprint(value, "value-fp"));
        var ledger = LedgerOf((parent, "parent-fp", session));

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Contains("has not written it", Assert.Single(conflicts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceAutoWithADirectRowStillResolves()
    {
        // The replacement contract: the direct sub-domain row (seeded at creation, advanced on
        // every script write) carries the fill on its own.
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000446";
        var source = Source(id);
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(new ResourceFingerprint(source, "source-fp"));
        var ledger = LedgerOf((source, "source-fp", session));

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Empty(conflicts);
        Assert.Equal("source-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
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
            changeSet, snapshot, session, DocKey, ledger);

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
            changeSet, snapshot, session, DocKey, ledger);

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
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Empty(conflicts);
        Assert.Same(changeSet, resolved); // no auto anywhere -> identical instance returned
        Assert.Equal("concrete-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
    }

    // --- Self-attributable stale concrete rebase (roadmap A-1) -------------------------------------
    // Value/geometry writes carry a concrete fingerprint gptino:auto cannot fill. When the current
    // live state IS this session's own last write (per the ledger), a stale base is rebased to live
    // instead of Blocking; every foreign/drift/unknown case is left untouched so the Block still fires.

    private static ResourceAddress Value(string id) =>
        new(ResourceKind.GrasshopperComponentValue, id, "*");

    private static LiveDocumentBackend.PreparedOperation ValueOperation(ResourceAddress resource, string payloadFingerprint)
    {
        var operation = new TypedOperation(
            "op-1",
            OperationKind.SetValue,
            AdapterOwner.Canvas,
            Array.Empty<ResourceAddress>(),
            new[] { resource },
            Reversible: false);
        var json = $$"""{"operationId":"op-1","objectId":"{{resource.Id}}","expectedFingerprint":"{{payloadFingerprint}}","value":5}""";
        using var document = JsonDocument.Parse(json);
        return new LiveDocumentBackend.PreparedOperation(
            operation,
            BridgeAdapterOwner.Canvas,
            "canvas.setNumberSlider",
            document.RootElement.Clone(),
            Array.Empty<byte>(),
            "sha");
    }

    [Fact]
    public void SelfStaleConcreteRebasesWriteSetAndPayloadToLive()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000501");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "stale-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = Ledger(value, "live-fp", session); // live == this session's own last write

        var (resolved, operations, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "stale-fp") }, snapshot, session, DocKey, ledger);

        Assert.Equal("live-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
        var item = Assert.Single(rebased);
        Assert.Equal("stale-fp", item.StaleFingerprint);
        Assert.Equal("live-fp", item.LiveFingerprint);
        // The payload fingerprint is rewritten too, so the payload/writeSet alignment preflight passes.
        Assert.Equal("live-fp", Assert.Single(operations).Arguments.GetProperty("expectedFingerprint").GetString());
    }

    [Fact]
    public void ForeignStaleConcreteIsNotRebased()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000502");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "stale-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = Ledger(value, "live-fp", Guid.NewGuid()); // another session last wrote it

        var (resolved, _, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "stale-fp") }, snapshot, session, DocKey, ledger);

        Assert.Empty(rebased); // left stale so ConflictDetector Blocks the genuine conflict
        Assert.Equal("stale-fp", Assert.Single(resolved.WriteSet).ExpectedFingerprint);
    }

    [Fact]
    public void DriftedStaleConcreteIsNotRebased()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000503");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "stale-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = Ledger(value, "ledger-fp", session); // this session's last write != live (manual drift)

        var (_, _, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "stale-fp") }, snapshot, session, DocKey, ledger);

        Assert.Empty(rebased);
    }

    [Fact]
    public void ConcreteStaleWithNoLedgerRowIsNotRebased()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000504");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "stale-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = new Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry>(StringComparer.Ordinal);

        var (_, _, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "stale-fp") }, snapshot, session, DocKey, ledger);

        Assert.Empty(rebased);
    }

    [Fact]
    public void ConcreteAlreadyMatchingLiveIsNotRebased()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000505");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "live-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = Ledger(value, "live-fp", session);

        var (resolved, operations, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "live-fp") }, snapshot, session, DocKey, ledger);

        Assert.Empty(rebased);
        Assert.Same(changeSet, resolved); // unchanged instance returned when nothing rebases
    }

    // --- Doc-scoping (review FINDING 1) ------------------------------------------------------------
    // A file-copied document reuses component InstanceGuids, so identical resource ids (and identical
    // fingerprints!) exist in two documents at once. A ledger entry recorded for doc A must never
    // fill (or rebase) an expectation for doc B — neither via the direct key match nor via the
    // parent-ownership fallback scan.

    private const string OtherDocKey = "doc0000bbbb00002";

    private static Dictionary<string, LiveDocumentBackend.ResourceLedgerEntry> LedgerInDoc(
        string docKey, ResourceAddress resource, string fingerprint, Guid session) =>
        new(StringComparer.Ordinal)
        {
            [LiveDocumentBackend.ResourceLedgerKey(docKey, resource)] = new(resource, fingerprint, session, 4)
        };

    [Fact]
    public void AutoIsRefusedWhenTheOnlyLedgerEntryBelongsToAnotherDocument()
    {
        var session = Guid.NewGuid();
        var source = Source("00000000-0000-0000-0000-000000000601");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(source, ResourceExpectation.AutoFingerprint));
        // Same id, same fingerprint, same session — but the entry was recorded in the OTHER doc.
        var snapshot = SnapshotWith(new ResourceFingerprint(source, "fp-copy"));
        var ledger = LedgerInDoc(OtherDocKey, source, "fp-copy", session);

        var (resolved, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.Contains("has not written it", Assert.Single(conflicts), StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(resolved.WriteSet).IsAuto);
    }

    [Fact]
    public void ParentFallbackIgnoresAParentEntryRecordedInAnotherDocument()
    {
        var session = Guid.NewGuid();
        var id = "00000000-0000-0000-0000-000000000602";
        var io = new ResourceAddress(ResourceKind.GrasshopperComponentIo, id, "*");
        var parent = new ResourceAddress(ResourceKind.GrasshopperComponent, id, "*");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(io, ResourceExpectation.AutoFingerprint));
        var snapshot = SnapshotWith(
            new ResourceFingerprint(parent, "parent-fp"),
            new ResourceFingerprint(io, "io-fp"));
        // The parent row would satisfy the fallback in ITS document — but it is doc B's row.
        var ledger = LedgerInDoc(OtherDocKey, parent, "parent-fp", session);

        var (_, conflicts) = LiveDocumentBackend.ResolveAutoExpectations(
            changeSet, snapshot, session, DocKey, ledger);

        Assert.NotEmpty(conflicts);
    }

    [Fact]
    public void SelfStaleRebaseIgnoresALedgerEntryRecordedInAnotherDocument()
    {
        var session = Guid.NewGuid();
        var value = Value("00000000-0000-0000-0000-000000000603");
        var changeSet = ChangeSetWith(session, new ResourceExpectation(value, "stale-fp"));
        var snapshot = SnapshotWith(new ResourceFingerprint(value, "live-fp"));
        var ledger = LedgerInDoc(OtherDocKey, value, "live-fp", session);

        var (_, _, rebased) = LiveDocumentBackend.ResolveSelfStaleConcreteRebase(
            changeSet, new[] { ValueOperation(value, "stale-fp") }, snapshot, session, DocKey, ledger);

        Assert.Empty(rebased); // left stale so ConflictDetector Blocks it
    }
}
