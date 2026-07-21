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
        command.CommandText = "SELECT id,name,role,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at FROM sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSession(reader) : null;
    }

    public async Task<SessionRecord?> FindSessionByThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,role,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at FROM sessions WHERE codex_thread_id=$thread;";
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
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions(id,name,role,model_profile,model,state,sort_order,created_at,updated_at)
                VALUES($id,$name,$role,$profile,$model,$state,$order,$created,$updated);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name.Trim());
            command.Parameters.AddWithValue("$role", Normalize(request.Role, "modeler"));
            command.Parameters.AddWithValue("$profile", Normalize(request.ModelProfile, "standard"));
            command.Parameters.AddWithValue("$model", (object?)request.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", SessionStates.Idle);
            command.Parameters.AddWithValue("$order", order);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new SessionRecord(
                id,
                request.Name.Trim(),
                Normalize(request.Role, "modeler"),
                Normalize(request.ModelProfile, "standard"),
                request.Model,
                SessionStates.Idle,
                checked((int)order),
                null,
                null,
                now,
                now);
        }
        finally
        {
            _writeGate.Release();
        }
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
        string? role,
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
                SET role=COALESCE($role,role),
                    model_profile=COALESCE($profile,model_profile),
                    model=CASE WHEN $set_model=1 THEN $model ELSE model END,
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
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
        command.CommandText = "SELECT id,name,role,model_profile,model,state,sort_order,codex_thread_id,current_task,created_at,updated_at FROM sessions ORDER BY sort_order;";
        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(MapSession(reader));
        }
        return sessions;
    }

    private static SessionRecord MapSession(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<HashSet<Guid>> ReadSessionIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM sessions;";
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
}

public sealed class SessionOrderConcurrencyException(long expected, long actual)
    : InvalidOperationException($"Session order changed. Expected version {expected}, actual version {actual}.")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

public sealed record MessageAppendResult(ChatMessage Message, bool Created);
