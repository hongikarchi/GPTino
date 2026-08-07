using GPTino.AgentHost.Data;
using GPTino.Contracts;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Durable resource-ledger coverage (W2): the ledger is restorable knowledge only, so every
/// degraded path (missing store, schema mismatch, cap overflow) must degrade to a cold start —
/// fewer auto-fills — and never to a wrong fill or a crash.
/// </summary>
public sealed class ResourceLedgerStoreTests
{
    private static ResourceLedgerRecord Record(
        string id,
        string fingerprint,
        Guid session,
        long revision = 1,
        ResourceKind kind = ResourceKind.GrasshopperComponent)
    {
        var resource = new ResourceAddress(kind, id, "*");
        return new ResourceLedgerRecord(
            $"{resource.Kind}:{resource.Id}:{resource.Field}",
            resource,
            fingerprint,
            session,
            revision);
    }

    [Fact]
    public async Task UpsertAndReadRoundTripsEveryField()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        var original = Record("component-a", "fp-1", session, revision: 7);

        await store.UpsertAsync("doc0000aaaa", [original]);
        // Same key again: the newer commit supersedes the row instead of duplicating it.
        var updated = original with { Fingerprint = "fp-2", Revision = 9 };
        await store.UpsertAsync("doc0000aaaa", [updated]);

        var stored = Assert.Single(await store.ReadDocumentAsync("doc0000aaaa"));
        Assert.Equal(updated.ResourceKey, stored.ResourceKey);
        Assert.Equal(updated.Resource, stored.Resource);
        Assert.Equal("fp-2", stored.Fingerprint);
        Assert.Equal(session, stored.SessionId);
        Assert.Equal(9, stored.Revision);
    }

    [Fact]
    public async Task SubDomainResourceKeysRoundTripThroughTheStore()
    {
        // The script/Rhino sub-domain rows the commit-time ledger now records (live gate
        // 20260807T175523Z): their composite keys and enum kinds must survive the durable
        // round trip byte-for-byte, or hydration would revive them under the wrong identity
        // and the direct-row source/value safety check would silently degrade to a refusal.
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        var componentId = Guid.NewGuid().ToString("D");
        var rows = new[]
        {
            Record(componentId, "py-fp", session, kind: ResourceKind.GrasshopperComponentSource),
            Record(componentId, "py-fp", session, kind: ResourceKind.GrasshopperComponentIo),
            Record(componentId, "py-fp", session, kind: ResourceKind.GrasshopperComponentValue),
            Record(componentId, "geo-fp", session, kind: ResourceKind.RhinoObjectGeometry),
            Record(componentId, "attr-fp", session, kind: ResourceKind.RhinoObjectAttributes),
        };

        await store.UpsertAsync("doc0000aaaa", rows);
        var stored = await store.ReadDocumentAsync("doc0000aaaa");

        Assert.Equal(rows.Length, stored.Count);
        foreach (var expected in rows)
        {
            var actual = Assert.Single(stored, item =>
                string.Equals(item.ResourceKey, expected.ResourceKey, StringComparison.Ordinal));
            Assert.Equal(expected.Resource, actual.Resource);
            Assert.Equal(expected.Fingerprint, actual.Fingerprint);
            Assert.Equal(session, actual.SessionId);
        }
    }

    [Fact]
    public async Task ReadReturnsOnlyTheRequestedDocumentsRows()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        // The SAME resource id in two documents (identical component InstanceGuids after a file
        // copy) must never cross-contaminate: hydration is strictly per doc_key.
        await store.UpsertAsync("doc0000aaaa", [Record("shared-id", "fp-doc-a", session)]);
        await store.UpsertAsync("doc0000bbbb", [Record("shared-id", "fp-doc-b", session)]);

        var docA = Assert.Single(await store.ReadDocumentAsync("doc0000aaaa"));
        var docB = Assert.Single(await store.ReadDocumentAsync("doc0000bbbb"));

        Assert.Equal("fp-doc-a", docA.Fingerprint);
        Assert.Equal("fp-doc-b", docB.Fingerprint);
        Assert.Empty(await store.ReadDocumentAsync("doc0000cccc"));
    }

    [Fact]
    public async Task SchemaVersionMismatchDropsTheTableAndStartsCold()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("resource-ledger.db");
        var store = new ResourceLedgerStore(databasePath);
        await store.InitializeAsync();
        await store.UpsertAsync("doc0000aaaa", [Record("component-a", "fp-1", Guid.NewGuid())]);
        // Simulate a future/incompatible schema stamp left by another build.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE resource_ledger_meta SET value='9999' WHERE key='schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        var reopened = new ResourceLedgerStore(databasePath);
        await reopened.InitializeAsync(); // must not throw

        Assert.Empty(await reopened.ReadDocumentAsync("doc0000aaaa"));
        // The recreated table is fully usable again.
        await reopened.UpsertAsync("doc0000aaaa", [Record("component-b", "fp-2", Guid.NewGuid())]);
        Assert.Single(await reopened.ReadDocumentAsync("doc0000aaaa"));
    }

    [Fact]
    public async Task PerDocumentCapCompactionKeepsTheMostRecentlyWrittenRows()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        const int overflow = 8;
        var rows = Enumerable.Range(1, ResourceLedgerStore.MaximumRowsPerDocument + overflow)
            .Select(revision => Record($"component-{revision:D5}", $"fp-{revision}", session, revision))
            .ToArray();
        // An unrelated document must not be touched by the other document's compaction.
        await store.UpsertAsync("doc0000bbbb", [Record("survivor", "fp-x", session, revision: 1)]);

        await store.UpsertAsync("doc0000aaaa", rows);

        var kept = await store.ReadDocumentAsync("doc0000aaaa");
        Assert.Equal(ResourceLedgerStore.MaximumRowsPerDocument, kept.Count);
        // Eviction follows the write sequence (oldest first), so the earliest-inserted rows fell.
        Assert.Equal(overflow + 1, kept.Min(record => record.Revision));
        Assert.Single(await store.ReadDocumentAsync("doc0000bbbb"));
    }

    [Fact]
    public async Task CompactionSurvivesTheRevisionResetOfARestart()
    {
        // Snapshot revisions restart at 1 on every runtime/document reopen. If compaction ordered
        // by revision, the FRESHEST rows (written after a restart, revision 1) would be the first
        // evicted at the cap. The store-side monotonic seq must keep them and evict the oldest
        // WRITTEN rows instead, regardless of their (higher) pre-restart revisions.
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("resource-ledger.db");
        var store = new ResourceLedgerStore(databasePath);
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        var preRestart = Enumerable.Range(1, ResourceLedgerStore.MaximumRowsPerDocument)
            .Select(index => Record($"old-{index:D5}", $"fp-{index}", session, revision: 100 + index))
            .ToArray();
        await store.UpsertAsync("doc0000aaaa", preRestart);

        // "Restart": a new store instance over the same file, committing at revision 1.
        var reopened = new ResourceLedgerStore(databasePath);
        await reopened.InitializeAsync();
        const int fresh = 8;
        var postRestart = Enumerable.Range(1, fresh)
            .Select(index => Record($"new-{index:D5}", $"fp-new-{index}", session, revision: 1))
            .ToArray();
        await reopened.UpsertAsync("doc0000aaaa", postRestart);

        var kept = await reopened.ReadDocumentAsync("doc0000aaaa");
        Assert.Equal(ResourceLedgerStore.MaximumRowsPerDocument, kept.Count);
        // Every post-restart row survived (the current commit's rows are never the eviction
        // victims), and exactly the OLDEST-written pre-restart rows fell.
        Assert.Equal(fresh, kept.Count(record => record.ResourceKey.Contains(":new-")));
        Assert.DoesNotContain(kept, record => record.ResourceKey.Contains(":old-00001"));
        Assert.DoesNotContain(kept, record => record.ResourceKey.Contains($":old-{fresh:D5}"));
        Assert.Contains(kept, record => record.ResourceKey.Contains($":old-{fresh + 1:D5}"));
    }

    [Fact]
    public async Task ReUpsertingARowMakesItNewestSoItSurvivesCompaction()
    {
        // A commit that touches an EXISTING key re-stamps its seq: the row becomes the newest and
        // must never be evicted by that same commit's compaction, however old its first insert was.
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        var initial = Enumerable.Range(1, ResourceLedgerStore.MaximumRowsPerDocument)
            .Select(index => Record($"component-{index:D5}", $"fp-{index}", session, index))
            .ToArray();
        await store.UpsertAsync("doc0000aaaa", initial);

        // One commit: refresh the OLDEST row and add one brand-new row (cap + 1 total).
        await store.UpsertAsync("doc0000aaaa",
        [
            initial[0] with { Fingerprint = "fp-refreshed" },
            Record("component-extra", "fp-extra", session, revision: 1)
        ]);

        var kept = await store.ReadDocumentAsync("doc0000aaaa");
        Assert.Equal(ResourceLedgerStore.MaximumRowsPerDocument, kept.Count);
        // Both rows of the current commit survived; the eviction victim was the oldest UNTOUCHED
        // row (component-00002), not the just-refreshed component-00001.
        Assert.Equal("fp-refreshed",
            Assert.Single(kept, record => record.ResourceKey.Contains(":component-00001")).Fingerprint);
        Assert.Contains(kept, record => record.ResourceKey.Contains(":component-extra"));
        Assert.DoesNotContain(kept, record => record.ResourceKey.Contains(":component-00002"));
    }

    [Fact]
    public async Task RemoveSessionDeletesOnlyThatSessionsRowsAcrossDocuments()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var deleted = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await store.UpsertAsync("doc0000aaaa",
        [
            Record("component-a", "fp-1", deleted),
            Record("component-b", "fp-2", kept)
        ]);
        await store.UpsertAsync("doc0000bbbb", [Record("component-c", "fp-3", deleted)]);

        var removed = await store.RemoveSessionAsync(deleted);

        Assert.Equal(2, removed);
        var remaining = Assert.Single(await store.ReadDocumentAsync("doc0000aaaa"));
        Assert.Equal(kept, remaining.SessionId);
        Assert.Empty(await store.ReadDocumentAsync("doc0000bbbb"));
    }

    [Fact]
    public async Task OrphanSweepDeletesOnlyRowsOfUnknownSessions()
    {
        // The purge race (fire-and-forget RemoveSessionAsync vs an in-flight commit's upsert) can
        // strand rows of a session that no longer exists; the startup sweep reclaims exactly those.
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var known = Guid.NewGuid();
        var softDeleted = Guid.NewGuid(); // still a known id — restorable sessions keep baselines
        var purged = Guid.NewGuid();
        await store.UpsertAsync("doc0000aaaa",
        [
            Record("component-a", "fp-1", known),
            Record("component-b", "fp-2", softDeleted),
            Record("component-c", "fp-3", purged)
        ]);
        await store.UpsertAsync("doc0000bbbb", [Record("component-d", "fp-4", purged)]);

        var removed = await store.RemoveSessionsExceptAsync([known, softDeleted]);

        Assert.Equal(2, removed);
        var kept = await store.ReadDocumentAsync("doc0000aaaa");
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, record => record.SessionId == purged);
        Assert.Empty(await store.ReadDocumentAsync("doc0000bbbb"));
        // Idempotent: a second sweep finds nothing.
        Assert.Equal(0, await store.RemoveSessionsExceptAsync([known, softDeleted]));
    }

    [Fact]
    public async Task OrphanSweepWithNoKnownSessionsClearsEveryRow()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        await store.UpsertAsync("doc0000aaaa", [Record("component-a", "fp-1", Guid.NewGuid())]);

        Assert.Equal(1, await store.RemoveSessionsExceptAsync(Array.Empty<Guid>()));
        Assert.Empty(await store.ReadDocumentAsync("doc0000aaaa"));
    }

    [Fact]
    public async Task RemapDocKeyMovesRowsToTheNewKey()
    {
        using var directory = new TestDirectory();
        var store = new ResourceLedgerStore(directory.GetPath("resource-ledger.db"));
        await store.InitializeAsync();
        var session = Guid.NewGuid();
        await store.UpsertAsync("doc0000aaaa", [Record("component-a", "fp-1", session)]);

        // Case-insensitive match, canonical lowercase result — same contract as the job store.
        var affected = await store.RemapDocKeyAsync("DOC0000AAAA", "DOC0000RENAMED");

        Assert.Equal(1, affected);
        Assert.Empty(await store.ReadDocumentAsync("doc0000aaaa"));
        Assert.Single(await store.ReadDocumentAsync("doc0000renamed"));
    }
}
