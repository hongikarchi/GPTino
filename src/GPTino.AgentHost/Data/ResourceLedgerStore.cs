using System.Globalization;
using GPTino.Contracts;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Data;

/// <summary>
/// One durable resource-ledger row: the composite resource key ("Kind:Id:Field" — the doc_key
/// column scopes it; the in-memory ledger prefixes the same docKey as "{docKey}|Kind:Id:Field"),
/// the structured address it was built from, and the "this session last committed this
/// fingerprint at this revision" fact the gptino:auto / self-stale-rebase safety predicate
/// consults.
/// </summary>
public sealed record ResourceLedgerRecord(
    string ResourceKey,
    ResourceAddress Resource,
    string Fingerprint,
    Guid SessionId,
    long Revision);

/// <summary>
/// Durable mirror of the per-runtime resource ledger ("this session last committed fingerprint X
/// for resource R"), keyed by the same durable path-derived docKey the runtime uses for every
/// other per-document store (histories, sessions.gh_doc, live_jobs.target_doc) — so two documents
/// with identical component InstanceGuids can never cross-contaminate, and a Save As remap
/// follows the same rename path. The ledger is restored knowledge, not authority: the safety
/// predicate (ledger fingerprint == live fingerprint AND same session) still runs against the
/// live document, so a stale or wrong row can only ever produce a refusal, never a bad write.
/// Losing this store entirely is a cold start — exactly today's pre-persistence behavior.
/// </summary>
public sealed class ResourceLedgerStore
{
    // Bump on any incompatible table change. A mismatch DROPS the table and starts cold — the
    // ledger is restorable knowledge, so losing it can never lose user data, and it must never
    // crash startup. v2: added the store-side monotonic `seq` column compaction orders by.
    private const string SchemaVersion = "2";

    // Per-document row cap: deleted resources' rows are never removed (mirroring the in-memory
    // ledger, which also only ever upserts), so growth is bounded here instead — on exceeding
    // the cap, compaction keeps the most recently WRITTEN entries, ordered by the store-side
    // monotonic `seq` column (never by snapshot revision, which resets to 1 on every
    // runtime/document reopen and would evict the newest rows right after a restart).
    public const int MaximumRowsPerDocument = 2048;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ResourceLedgerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS resource_ledger_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);
            var storedVersion = await ReadMetaAsync(connection, "schema_version", cancellationToken)
                .ConfigureAwait(false);
            if (storedVersion is not null &&
                !string.Equals(storedVersion, SchemaVersion, StringComparison.Ordinal))
            {
                // Incompatible (or corrupt) stamp: cold start. Dropping is always safe — see the
                // class remarks — and strictly better than crashing or misreading old rows.
                await ExecuteAsync(connection, "DROP TABLE IF EXISTS resource_ledger;", cancellationToken)
                    .ConfigureAwait(false);
            }
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS resource_ledger (
                    doc_key TEXT NOT NULL,
                    resource_key TEXT NOT NULL,
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    resource_field TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    seq INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(doc_key, resource_key)
                );
                CREATE INDEX IF NOT EXISTS ix_resource_ledger_session
                    ON resource_ledger(session_id);
                CREATE INDEX IF NOT EXISTS ix_resource_ledger_doc_seq
                    ON resource_ledger(doc_key, seq);
                """, cancellationToken).ConfigureAwait(false);
            await using var stamp = connection.CreateCommand();
            stamp.CommandText =
                "INSERT OR REPLACE INTO resource_ledger_meta(key,value) VALUES('schema_version',$version);";
            stamp.Parameters.AddWithValue("$version", SchemaVersion);
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Upserts exactly the entries one commit changed, then compacts the document down to
    /// <see cref="MaximumRowsPerDocument"/> keeping the most recently written rows.
    /// </summary>
    /// <remarks>
    /// Eviction order is the store-side monotonic <c>seq</c> column, assigned per row inside this
    /// transaction from <c>MAX(seq)+1</c> (a touched row is re-stamped to the newest seq). This is
    /// the simplest scheme that is correct on BOTH review findings at once: (a) snapshot revisions
    /// reset to 1 on every runtime/document reopen, so ordering by revision would evict the NEWEST
    /// rows right after a restart — seq only ever grows, across restarts; (b) all rows of one
    /// commit share revision and updated_at, so a tie-break could evict the very rows being
    /// upserted — the current commit's rows always carry the highest seq values, so compaction
    /// can only reach them when the single commit alone exceeds the cap.
    /// </remarks>
    public async Task UpsertAsync(
        string docKey,
        IReadOnlyList<ResourceLedgerRecord> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docKey);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }
        var canonicalDocKey = docKey.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            long nextSequence;
            await using (var readSequence = connection.CreateCommand())
            {
                readSequence.Transaction = transaction;
                readSequence.CommandText = "SELECT COALESCE(MAX(seq),0) FROM resource_ledger;";
                nextSequence = (long)(await readSequence.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;
            }
            foreach (var entry in entries)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO resource_ledger(
                        doc_key,resource_key,resource_kind,resource_id,resource_field,
                        session_id,fingerprint,revision,seq,updated_at)
                    VALUES($doc,$key,$kind,$id,$field,$session,$fingerprint,$revision,$seq,$updated)
                    ON CONFLICT(doc_key,resource_key) DO UPDATE SET
                        resource_kind=excluded.resource_kind,
                        resource_id=excluded.resource_id,
                        resource_field=excluded.resource_field,
                        session_id=excluded.session_id,
                        fingerprint=excluded.fingerprint,
                        revision=excluded.revision,
                        seq=excluded.seq,
                        updated_at=excluded.updated_at;
                    """;
                upsert.Parameters.AddWithValue("$doc", canonicalDocKey);
                upsert.Parameters.AddWithValue("$key", entry.ResourceKey);
                upsert.Parameters.AddWithValue("$kind", entry.Resource.Kind.ToString());
                upsert.Parameters.AddWithValue("$id", entry.Resource.Id);
                upsert.Parameters.AddWithValue("$field", entry.Resource.Field);
                upsert.Parameters.AddWithValue("$session", entry.SessionId.ToString("D"));
                upsert.Parameters.AddWithValue("$fingerprint", entry.Fingerprint);
                upsert.Parameters.AddWithValue("$revision", entry.Revision);
                upsert.Parameters.AddWithValue("$seq", ++nextSequence);
                upsert.Parameters.AddWithValue("$updated", now);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var compact = connection.CreateCommand())
            {
                compact.Transaction = transaction;
                compact.CommandText = """
                    DELETE FROM resource_ledger
                    WHERE doc_key=$doc AND rowid NOT IN (
                        SELECT rowid FROM resource_ledger
                        WHERE doc_key=$doc
                        ORDER BY seq DESC
                        LIMIT $cap);
                    """;
                compact.Parameters.AddWithValue("$doc", canonicalDocKey);
                compact.Parameters.AddWithValue("$cap", MaximumRowsPerDocument);
                await compact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Reads exactly one document's rows (hydration input). Unknown resource kinds
    /// (a downgrade reading newer rows) are skipped, never thrown. Runs under the write gate
    /// like every other public method (same convention as <see cref="DurableJobStore"/>), so a
    /// hydration read can never interleave with a half-applied upsert/sweep.</summary>
    public async Task<IReadOnlyList<ResourceLedgerRecord>> ReadDocumentAsync(
        string docKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docKey);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT resource_key,resource_kind,resource_id,resource_field,session_id,fingerprint,revision
                FROM resource_ledger
                WHERE doc_key=$doc COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$doc", docKey.Trim());
            var records = new List<ResourceLedgerRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Enum.TryParse<ResourceKind>(reader.GetString(1), ignoreCase: true, out var kind) ||
                    !Guid.TryParse(reader.GetString(4), out var sessionId))
                {
                    continue;
                }
                records.Add(new ResourceLedgerRecord(
                    reader.GetString(0),
                    new ResourceAddress(kind, reader.GetString(2), reader.GetString(3)),
                    reader.GetString(5),
                    sessionId,
                    reader.GetInt64(6)));
            }
            return records;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Removes every row a PURGED session owns, across all documents — a purged session can never
    /// submit again, so its baselines are dead weight. (Soft-delete deliberately does NOT call
    /// this: a restored session must come back with its baselines working.)
    /// </summary>
    public async Task<int> RemoveSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM resource_ledger WHERE session_id=$session;";
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Orphan sweep: deletes every row whose session no longer exists in the caller's session
    /// store (live AND soft-deleted ids both count as known — soft-deleted sessions keep their
    /// baselines for restore). Covers the purge race: a fire-and-forget
    /// <see cref="RemoveSessionAsync"/> that loses to an in-flight commit's
    /// <see cref="UpsertAsync"/> leaves rows behind; until the next startup runs this sweep those
    /// rows can only ever cause a refusal (the safety predicate requires the SAME session id),
    /// but they would otherwise erode the per-document cap forever. The store stays decoupled:
    /// the backend passes the known-session id set in.
    /// </summary>
    public async Task<int> RemoveSessionsExceptAsync(
        IReadOnlyCollection<Guid> knownSessionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownSessionIds);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            if (knownSessionIds.Count == 0)
            {
                // No sessions exist at all: every row is an orphan by definition.
                command.CommandText = "DELETE FROM resource_ledger;";
            }
            else
            {
                var parameters = new List<string>(knownSessionIds.Count);
                var index = 0;
                foreach (var sessionId in knownSessionIds)
                {
                    var name = $"$s{index++}";
                    parameters.Add(name);
                    command.Parameters.AddWithValue(name, sessionId.ToString("D"));
                }
                command.CommandText =
                    $"DELETE FROM resource_ledger WHERE session_id NOT IN ({string.Join(",", parameters)});";
            }
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Follows a Save As docKey rename, same contract as
    /// <see cref="DurableJobStore.RemapTargetDocAsync"/>: case-insensitive match, canonical
    /// lowercase result; a colliding row under the new key is replaced.
    /// </summary>
    public async Task<int> RemapDocKeyAsync(
        string oldDocKey,
        string newDocKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDocKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDocKey);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OR REPLACE resource_ledger
                SET doc_key=$new
                WHERE doc_key=$old COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$new", newDocKey.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("$old", oldDocKey.Trim());
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM resource_ledger_meta WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }
}
