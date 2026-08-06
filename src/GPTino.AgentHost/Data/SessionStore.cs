using GPTino.AgentHost.Api;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Data;

public sealed class SessionStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SessionStore(string databasePath)
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
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    role TEXT NOT NULL,
                    model_profile TEXT NOT NULL,
                    model TEXT NULL,
                    state TEXT NOT NULL,
                    sort_order INTEGER NOT NULL UNIQUE,
                    codex_thread_id TEXT NULL UNIQUE,
                    current_task TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    phase TEXT NULL,
                    client_message_id TEXT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_messages_session_id_id
                    ON messages(session_id, id);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_session_client_message
                    ON messages(session_id, client_message_id)
                    WHERE client_message_id IS NOT NULL;
                INSERT OR IGNORE INTO settings(key, value) VALUES ('order_version', '0');
                """, cancellationToken).ConfigureAwait(false);
            // Column migration for pre-existing user databases (CREATE TABLE IF NOT EXISTS never
            // alters an existing table). Nullable with no backfill: NULL = default-document
            // resolution, so legacy rows keep today's single-Grasshopper behavior untouched.
            if (!await HasColumnAsync(connection, "sessions", "gh_doc", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE sessions ADD COLUMN gh_doc TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            // Soft-delete marker: NULL = live (default for all legacy rows), a timestamp = hidden
            // from the active list but recoverable. Deleted rows are parked at a deep-negative
            // sort_order (see SetSessionDeletedAsync) so they never collide with the reorder path.
            if (!await HasColumnAsync(connection, "sessions", "deleted_at", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE sessions ADD COLUMN deleted_at TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            // Opt-in native Codex thread goal per session (0 = off, the default for all legacy rows).
            if (!await HasColumnAsync(connection, "sessions", "goal_enabled", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE sessions ADD COLUMN goal_enabled INTEGER NOT NULL DEFAULT 0;",
                    cancellationToken).ConfigureAwait(false);
            }
            // The goal CARD: the agent's structured reading of what the user asked for
            // (objective, verification criteria, assumptions, out-of-scope) plus its lifecycle
            // (proposing -> confirmed -> scored). One active card per session, so a column beats
            // an append-only transcript row. NULL means "no goal has been framed yet".
            if (!await HasColumnAsync(connection, "sessions", "goal_card", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE sessions ADD COLUMN goal_card TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            // The approval card: what the agent wants to change on the USER's own geometry, listed
            // for the user to pick from. Same one-active-card reasoning as goal_card.
            if (!await HasColumnAsync(connection, "sessions", "approval_card", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE sessions ADD COLUMN approval_card TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            await AbsorbRolesAndModesAsync(connection, cancellationToken).ConfigureAwait(false);
            // model_profile now stores a reasoning-effort level (low..ultra). Rewrite any legacy
            // profile values from pre-refactor sessions to the nearest effort. Idempotent: effort
            // values fall through the ELSE untouched.
            await ExecuteAsync(
                connection,
                """
                UPDATE sessions SET model_profile = CASE model_profile
                    WHEN 'auto' THEN 'xhigh'
                    WHEN 'high-assurance' THEN 'xhigh'
                    WHEN 'recovery' THEN 'xhigh'
                    WHEN 'deep' THEN 'xhigh'
                    WHEN 'standard' THEN 'medium'
                    WHEN 'fast-safe' THEN 'low'
                    WHEN 'read-fast' THEN 'low'
                    WHEN 'fast' THEN 'low'
                    ELSE model_profile END
                WHERE model_profile IN
                    ('auto','high-assurance','recovery','deep','standard','fast-safe','read-fast','fast');
                """,
                cancellationToken).ConfigureAwait(false);
            await NormalizeInterruptedSessionsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(IReadOnlyList<SessionRecord> Sessions, long OrderVersion)> ReadStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await ReadSessionsAsync(connection, cancellationToken).ConfigureAwait(false);
        var version = await ReadOrderVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return (sessions, version);
    }

    public async Task<SessionRecord?> FindSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at,gh_doc,goal_enabled,goal_card,approval_card FROM sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSession(reader) : null;
    }

    public async Task<SessionRecord?> FindSessionByThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at,gh_doc,goal_enabled,goal_card,approval_card FROM sessions WHERE codex_thread_id=$thread;";
        command.Parameters.AddWithValue("$thread", threadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSession(reader) : null;
    }

    public async Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Session name is required.", nameof(request));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var order = await ReadScalarLongAsync(
                connection,
                transaction,
                "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM sessions;",
                cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var grasshopperDoc = NormalizeGrasshopperDoc(request.GrasshopperDoc);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // role is a retired concept kept as a NOT NULL column (see AbsorbRolesAndModesAsync);
            // every row now carries the same constant.
            command.CommandText = """
                INSERT INTO sessions(id,name,role,model_profile,model,state,sort_order,created_at,updated_at,gh_doc,goal_enabled)
                VALUES($id,$name,'modeler',$profile,$model,$state,$order,$created,$updated,$ghDoc,$goal);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name.Trim());
            command.Parameters.AddWithValue("$profile", Normalize(request.ModelProfile, "xhigh"));
            command.Parameters.AddWithValue("$model", (object?)request.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", SessionStates.Idle);
            command.Parameters.AddWithValue("$order", order);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToString("O"));
            command.Parameters.AddWithValue("$ghDoc", (object?)grasshopperDoc ?? DBNull.Value);
            command.Parameters.AddWithValue("$goal", request.GoalEnabled ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new SessionRecord(
                id,
                request.Name.Trim(),
                Normalize(request.ModelProfile, "xhigh"),
                request.Model,
                SessionStates.Idle,
                checked((int)order),
                null,
                null,
                now,
                now,
                grasshopperDoc,
                request.GoalEnabled,
                null,
                null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Imports one archived session as a brand-new live session in a single transaction: the session
    /// row (fresh id, "(imported)" name, Idle, sort_order MAX+1, codex_thread_id NULL —
    /// never copy the archived thread id, it is UNIQUE and still owned by the source root — and
    /// gh_doc NULL for default-document resolution), then, in rowid/display order, the stale-reference
    /// banner, the copied transcript rows (verbatim role/content/phase/createdAt, client_message_id
    /// NULL to avoid the per-session uniqueness index), and the trailing model-visible context seed.
    /// Purely additive data motion — no canvas op, no adapter call, nothing touches any document.
    /// </summary>
    public async Task<SessionRecord> ImportSessionAsync(
        ImportedSessionSeed seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (string.IsNullOrWhiteSpace(seed.Name))
        {
            throw new ArgumentException("Imported session name is required.", nameof(seed));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var order = await ReadScalarLongAsync(
                connection,
                transaction,
                "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM sessions;",
                cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            const string profile = "auto";

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO sessions(id,name,role,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at,gh_doc)
                    VALUES($id,$name,'modeler',$profile,NULL,$state,$order,NULL,NULL,$created,$updated,NULL);
                    """;
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$name", seed.Name);
                command.Parameters.AddWithValue("$profile", profile);
                command.Parameters.AddWithValue("$state", SessionStates.Idle);
                command.Parameters.AddWithValue("$order", order);
                command.Parameters.AddWithValue("$created", now.ToString("O"));
                command.Parameters.AddWithValue("$updated", now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Insertion order == rowid order == transcript display order (reads sort by id, not by
            // created_at), so the banner leads and the seed trails even though the copied rows carry
            // older createdAt values that predate this session.
            await InsertImportedMessageAsync(
                connection, transaction, id, "system", seed.BannerContent,
                ImportedSessionPhases.Banner, now, cancellationToken).ConfigureAwait(false);
            foreach (var message in seed.Messages)
            {
                await InsertImportedMessageAsync(
                    connection, transaction, id, message.Role, message.Content,
                    message.Phase, message.CreatedAt, cancellationToken).ConfigureAwait(false);
            }
            await InsertImportedMessageAsync(
                connection, transaction, id, "system", seed.ContextSeedContent,
                ImportedSessionPhases.ContextSeed, now, cancellationToken).ConfigureAwait(false);

            transaction.Commit();
            return new SessionRecord(
                id,
                seed.Name,
                profile,
                null,
                SessionStates.Idle,
                checked((int)order),
                null,
                null,
                now,
                now,
                null,
                false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads the model-visible context seed an imported session carries, or null for a session that
    /// was not imported. Filters on role='system' too so a user message can never masquerade as the
    /// seed (phase is server-assigned, but the belt-and-suspenders filter matches the injection contract).
    /// </summary>
    public async Task<string?> ReadImportedContextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content
            FROM messages
            WHERE session_id=$session AND role='system' AND phase=$phase
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$phase", ImportedSessionPhases.ContextSeed);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    private static async Task InsertImportedMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string role,
        string content,
        string? phase,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(session_id,role,content,phase,client_message_id,created_at)
            VALUES($session,$role,$content,$phase,NULL,$created);
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$phase", (object?)phase ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> ReorderAsync(
        IReadOnlyList<Guid> orderedSessionIds,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedSessionIds);
        if (orderedSessionIds.Distinct().Count() != orderedSessionIds.Count)
        {
            throw new InvalidOperationException("Session order contains duplicate identifiers.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var actualVersion = await ReadOrderVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (actualVersion != expectedVersion)
            {
                throw new SessionOrderConcurrencyException(expectedVersion, actualVersion);
            }

            var existing = await ReadSessionIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (!existing.SetEquals(orderedSessionIds))
            {
                throw new InvalidOperationException("Session order must contain every current session exactly once.");
            }

            for (var index = 0; index < orderedSessionIds.Count; index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE sessions SET sort_order=$temporary WHERE id=$id;";
                command.Parameters.AddWithValue("$temporary", -(index + 1));
                command.Parameters.AddWithValue("$id", orderedSessionIds[index].ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var index = 0; index < orderedSessionIds.Count; index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE sessions SET sort_order=$order, updated_at=$now WHERE id=$id;";
                command.Parameters.AddWithValue("$order", index);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", orderedSessionIds[index].ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var nextVersion = checked(actualVersion + 1);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE settings SET value=$value WHERE key='order_version';";
                command.Parameters.AddWithValue("$value", nextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            return nextVersion;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Soft-deletes (deleted=true) or restores (deleted=false) a session. A soft-deleted session is
    /// hidden from the active list but keeps its row, messages, and thread so it can be restored
    /// intact. To stay clear of the reorder path (which parks live rows at temporary negatives
    /// -1..-n), a deleted row is moved to a deep-negative sort_order strictly below any existing
    /// value and below -1,000,000; a restored row re-appends at the end of the live order.
    /// </summary>
    public async Task SetSessionDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = deleted
                ? """
                  UPDATE sessions
                  SET deleted_at=$now, updated_at=$now,
                      sort_order = MIN(-1000000, (SELECT MIN(sort_order) FROM sessions) - 1)
                  WHERE id=$id AND deleted_at IS NULL;
                  """
                : """
                  UPDATE sessions
                  SET deleted_at=NULL, updated_at=$now,
                      sort_order = COALESCE((SELECT MAX(sort_order) FROM sessions WHERE deleted_at IS NULL), -1) + 1
                  WHERE id=$id AND deleted_at IS NOT NULL;
                  """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Lists soft-deleted sessions (most-recently-deleted first) for a restore/purge view.</summary>
    public async Task<IReadOnlyList<SessionRecord>> ReadDeletedSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at,gh_doc,goal_enabled,goal_card,approval_card FROM sessions WHERE deleted_at IS NOT NULL ORDER BY deleted_at DESC;";
        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(MapSession(reader));
        }
        return sessions;
    }

    /// <summary>
    /// Permanently removes a session and its transcript (messages cascade on the FK). Irreversible;
    /// callers gate this behind an explicit user confirmation. Live-jobs history and attachment
    /// files keyed by this session id are left on disk — harmless orphans that never surface once
    /// the session row is gone.
    /// </summary>
    public async Task PurgeSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            // Delete the transcript explicitly rather than relying on ON DELETE CASCADE — this
            // connection does not enable PRAGMA foreign_keys, so the cascade would not fire and
            // would leave orphaned message rows behind.
            await using (var messages = connection.CreateCommand())
            {
                messages.Transaction = transaction;
                messages.CommandText = "DELETE FROM messages WHERE session_id=$id;";
                messages.Parameters.AddWithValue("$id", id.ToString("D"));
                await messages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var session = connection.CreateCommand())
            {
                session.Transaction = transaction;
                session.CommandText = "DELETE FROM sessions WHERE id=$id;";
                session.Parameters.AddWithValue("$id", id.ToString("D"));
                await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Retracts the session's most recent user message and everything after it (its assistant reply /
    /// partial turn output), returning that message's text so the panel can reload it into the
    /// composer for editing. Used by "Stop &amp; edit": the user pauses, pulls the message back, edits,
    /// and resends as a fresh turn. Returns null when there is no user message to retract.
    /// </summary>
    public async Task<string?> RetractLastUserMessageAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            long lastUserId;
            string content;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText =
                    "SELECT id, content FROM messages WHERE session_id=$id AND role='user' ORDER BY id DESC LIMIT 1;";
                find.Parameters.AddWithValue("$id", sessionId.ToString("D"));
                await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
                lastUserId = reader.GetInt64(0);
                content = reader.GetString(1);
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                // Remove the user message and anything after it (assistant reply / partial output).
                delete.CommandText = "DELETE FROM messages WHERE session_id=$id AND id>=$from;";
                delete.Parameters.AddWithValue("$id", sessionId.ToString("D"));
                delete.Parameters.AddWithValue("$from", lastUserId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            return content;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(
        Guid sessionId,
        long after = 0,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = after <= 0
            ? """
                SELECT id,session_id,role,content,phase,created_at
                FROM (
                    SELECT id,session_id,role,content,phase,created_at
                    FROM messages
                    WHERE session_id=$session
                    ORDER BY id DESC
                    LIMIT $limit
                ) AS newest
                ORDER BY id;
                """
            : """
                SELECT id,session_id,role,content,phase,created_at
                FROM messages
                WHERE session_id=$session AND id>$after
                ORDER BY id
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ChatMessage(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public async Task<ChatMessage> AppendMessageAsync(
        Guid sessionId,
        string role,
        string content,
        string? phase = null,
        string? clientMessageId = null,
        CancellationToken cancellationToken = default) =>
        (await AppendMessageOnceAsync(
            sessionId,
            role,
            content,
            phase,
            clientMessageId,
            cancellationToken).ConfigureAwait(false)).Message;

    public async Task<MessageAppendResult> AppendMessageOnceAsync(
        Guid sessionId,
        string role,
        string content,
        string? phase = null,
        string? clientMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content is required.", nameof(content));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(clientMessageId))
            {
                var existing = await FindMessageByClientIdAsync(
                    connection,
                    sessionId,
                    clientMessageId,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return new MessageAppendResult(existing, false);
                }
            }

            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO messages(session_id,role,content,phase,client_message_id,created_at)
                VALUES($session,$role,$content,$phase,$client_message_id,$created);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$phase", (object?)phase ?? DBNull.Value);
            command.Parameters.AddWithValue("$client_message_id", (object?)clientMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            return new MessageAppendResult(new ChatMessage(id, sessionId, role, content, phase, now), true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<ChatMessage?> FindMessageByClientIdAsync(
        SqliteConnection connection,
        Guid sessionId,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,role,content,phase,created_at
            FROM messages
            WHERE session_id=$session AND client_message_id=$client_message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$client_message_id", clientMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new ChatMessage(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
    }

    public Task SetSessionStateAsync(
        Guid id,
        string state,
        string? currentTask = null,
        CancellationToken cancellationToken = default) =>
        UpdateSessionAsync(id, state, currentTask, null, cancellationToken);

    public Task SetThreadIdAsync(Guid id, string threadId, CancellationToken cancellationToken = default) =>
        UpdateSessionAsync(id, null, null, threadId, cancellationToken);

    public async Task UpdatePreferencesAsync(
        Guid id,
        string? modelProfile,
        string? model,
        bool setModel,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET model_profile=COALESCE($profile,model_profile),
                    model=CASE WHEN $set_model=1 THEN $model ELSE model END,
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$profile", (object?)modelProfile ?? DBNull.Value);
            command.Parameters.AddWithValue("$set_model", setModel ? 1 : 0);
            command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session {id:D} was not found.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Binds the session to one Grasshopper document (a durable docKey) or clears the binding
    /// (null = default-document resolution). Follows the UpdatePreferencesAsync pattern; the value
    /// is deliberately not validated against live targets — resolution happens at tool-call time
    /// with an actionable message listing the registered documents.
    /// </summary>
    public async Task SetGrasshopperDocAsync(
        Guid id,
        string? grasshopperDoc,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGrasshopperDoc(grasshopperDoc);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET gh_doc=$ghDoc,
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$ghDoc", (object?)normalized ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session {id:D} was not found.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Renames a session (the display title = the <c>name</c> column). The name is trimmed and must
    /// be non-empty; length is capped so a runaway paste cannot bloat the row. Follows the
    /// UpdatePreferencesAsync write-gate pattern.
    /// </summary>
    public async Task SetSessionTitleAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A session name cannot be empty.", nameof(name));
        }
        if (trimmed.Length > 120)
        {
            trimmed = trimmed[..120];
        }
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET name=$name,
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$name", trimmed);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session {id:D} was not found.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Rewrites every session bound to one Grasshopper docKey to another in a single UPDATE.
    /// Called when a Save As re-registration recomputes a target's path-derived docKey: the
    /// StableTargetKey proves it is the same live document, so bindings follow the rename
    /// instead of stranding sessions on a key that no longer resolves. Matching is
    /// case-insensitive (docKeys are canonical lowercase hex, but the column is unvalidated).
    /// </summary>
    public async Task<int> RemapGrasshopperDocAsync(
        string oldGrasshopperDoc,
        string newGrasshopperDoc,
        CancellationToken cancellationToken = default)
    {
        var oldNormalized = NormalizeGrasshopperDoc(oldGrasshopperDoc)
            ?? throw new ArgumentException("The old Grasshopper docKey is required.", nameof(oldGrasshopperDoc));
        var newNormalized = NormalizeGrasshopperDoc(newGrasshopperDoc)
            ?? throw new ArgumentException("The new Grasshopper docKey is required.", nameof(newGrasshopperDoc));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET gh_doc=$new,
                    updated_at=$updated
                WHERE gh_doc=$old COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$new", newNormalized);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$old", oldNormalized);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task UpdateSessionAsync(
        Guid id,
        string? state,
        string? currentTask,
        string? threadId,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET state=COALESCE($state,state),
                    current_task=CASE WHEN $set_task=1 THEN $task ELSE current_task END,
                    codex_thread_id=COALESCE($thread,codex_thread_id),
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
            command.Parameters.AddWithValue("$set_task", state is null ? 0 : 1);
            command.Parameters.AddWithValue("$task", (object?)currentTask ?? DBNull.Value);
            command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session {id:D} was not found.");
            }
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
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One-time absorption of the retired session-role/mode model: every session becomes the one
    /// thing GPTino is now, keeping its name and transcript. Three effects, in this order because
    /// the first two read the values the third erases:
    /// <list type="number">
    /// <item>Sessions that could NOT write before (plan mode, the read-only role) get a system
    /// message saying so — a capability that silently widens is exactly the kind of change a user
    /// must be told about rather than discover by watching geometry change.</item>
    /// <item>The parked resident curator (sort_order in the 1,000,000+ band, out of the panel's
    /// draggable range) re-enters the ordinary order at the end.</item>
    /// <item>role collapses to the constant 'modeler'. The column itself stays: it is NOT NULL with
    /// no default, so dropping it would rewrite the table for no gain.</item>
    /// </list>
    /// Idempotent: after the first run nothing matches any of the three predicates.
    /// </summary>
    private static async Task AbsorbRolesAndModesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // 'mode' arrived after 'role' and is gone again; a database old enough to predate it
        // encoded plan mode as role='planner', which the role predicate already catches.
        var hasMode = await HasColumnAsync(connection, "sessions", "mode", cancellationToken)
            .ConfigureAwait(false);
        var couldNotWrite = hasMode
            ? "lower(role) IN ('planner','read-only') OR lower(mode)='plan'"
            : "lower(role) IN ('planner','read-only')";

        await using (var notify = connection.CreateCommand())
        {
            notify.CommandText = $"""
                INSERT INTO messages(session_id,role,content,phase,created_at)
                SELECT id,
                       'system',
                       'Session roles and plan/auto mode are gone: this session could not apply changes before and now can. What still gates a destructive edit is the approval card, not the session.',
                       'recovery',
                       $now
                FROM sessions
                WHERE {couldNotWrite};
                """;
            notify.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Un-park one row at a time: MAX(sort_order) has to be recomputed after each move, and the
        // curator was a singleton, so this loop runs at most once on a real database.
        var parked = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT id FROM sessions WHERE deleted_at IS NULL AND sort_order >= 1000000 ORDER BY sort_order;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                parked.Add(reader.GetString(0));
            }
        }
        foreach (var id in parked)
        {
            await using var move = connection.CreateCommand();
            move.CommandText = """
                UPDATE sessions
                SET sort_order = (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM sessions WHERE sort_order < 1000000)
                WHERE id=$id;
                """;
            move.Parameters.AddWithValue("$id", id);
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            "UPDATE sessions SET role='modeler' WHERE role <> 'modeler';",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task NormalizeInterruptedSessionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var message = connection.CreateCommand())
        {
            message.Transaction = transaction;
            message.CommandText = """
                INSERT INTO messages(session_id,role,content,phase,created_at)
                SELECT id,
                       'system',
                       'The previous turn was interrupted by an AgentHost restart; review the document state before retrying.',
                       'recovery',
                       $now
                FROM sessions
                WHERE state IN ($running,$waiting);
                """;
            message.Parameters.AddWithValue("$now", now);
            message.Parameters.AddWithValue("$running", SessionStates.Running);
            message.Parameters.AddWithValue("$waiting", SessionStates.Waiting);
            await message.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                UPDATE sessions
                SET state=$failed,
                    current_task=NULL,
                    updated_at=$now
                WHERE state IN ($running,$waiting);
                """;
            session.Parameters.AddWithValue("$failed", SessionStates.Failed);
            session.Parameters.AddWithValue("$now", now);
            session.Parameters.AddWithValue("$running", SessionStates.Running);
            session.Parameters.AddWithValue("$waiting", SessionStates.Waiting);
            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static async Task<IReadOnlyList<SessionRecord>> ReadSessionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at,gh_doc,goal_enabled,goal_card,approval_card FROM sessions WHERE deleted_at IS NULL ORDER BY sort_order;";
        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(MapSession(reader));
        }
        return sessions;
    }

    /// <summary>
    /// Writes the session's goal card (opaque JSON owned by the agent + panel; the store only
    /// persists it). Null clears the card.
    /// </summary>
    public async Task SetGoalCardAsync(Guid id, string? card, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET goal_card=$card, updated_at=$updated WHERE id=$id;";
            command.Parameters.AddWithValue("$card", (object?)card ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Writes the session's approval card (opaque JSON). Null clears it.</summary>
    public async Task SetApprovalCardAsync(Guid id, string? card, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET approval_card=$card, updated_at=$updated WHERE id=$id;";
            command.Parameters.AddWithValue("$card", (object?)card ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetGoalEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET goal_enabled=$goal, updated_at=$updated WHERE id=$id;";
            command.Parameters.AddWithValue("$goal", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static SessionRecord MapSession(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private static async Task<HashSet<Guid>> ReadSessionIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Reorder operates on the live set only; deleted rows are parked out of band and the panel
        // never includes them in an order request.
        command.CommandText = "SELECT id FROM sessions WHERE deleted_at IS NULL;";
        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return ids;
    }

    private static async Task<long> ReadOrderVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken) =>
        await ReadScalarLongAsync(
            connection,
            transaction,
            "SELECT value FROM settings WHERE key='order_version';",
            cancellationToken).ConfigureAwait(false);

    private static async Task<long> ReadScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    // Lowercased to match ComputeDocumentKey's canonical lowercase-hex form: the backend resolves
    // docKeys case-insensitively, but the panel compares boundGrasshopperDocId strictly, so a
    // non-canonical stored casing would render a correctly-executing session as unbound.
    private static string? NormalizeGrasshopperDoc(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

public sealed class SessionOrderConcurrencyException(long expected, long actual)
    : InvalidOperationException($"Session order changed. Expected version {expected}, actual version {actual}.")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

public sealed record MessageAppendResult(ChatMessage Message, bool Created);
