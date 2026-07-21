using System.Globalization;
using System.Text.Json;
using GPTino.BridgeContract;
using GPTino.Contracts;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Data;

public sealed record DurableJobRecord(
    Guid JobId,
    Guid SessionId,
    string IdempotencyKey,
    string Summary,
    ChangeSet ChangeSet,
    long EnqueueSequence,
    JobState State,
    string Phase,
    string? Message,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RequestHash = "");

public sealed record DurableJobInsertResult(bool Inserted, DurableJobRecord Record);

/// <summary>
/// Durable metadata for live-document jobs. The store deliberately persists no
/// replay intent: interrupted jobs are converted to RecoveryRequired at startup.
/// </summary>
public sealed class DurableJobStore
{
    public const string RestartRecoveryMessage =
        "AgentHost restarted before this job reached a durable terminal state. " +
        "No operations were replayed; inspect the live document and submit an explicit recovery ChangeSet.";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DurableJobStore(string databasePath)
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
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS live_jobs (
                    job_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    change_set_json TEXT NOT NULL,
                    enqueue_sequence INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    message TEXT NULL,
                    enqueued_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    request_hash TEXT NOT NULL DEFAULT '',
                    UNIQUE(session_id, idempotency_key)
                );
                CREATE INDEX IF NOT EXISTS ix_live_jobs_state ON live_jobs(state);
                CREATE INDEX IF NOT EXISTS ix_live_jobs_enqueue_sequence
                    ON live_jobs(enqueue_sequence);
                """, cancellationToken).ConfigureAwait(false);
            if (!await HasColumnAsync(connection, "live_jobs", "request_hash", cancellationToken)
                    .ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE live_jobs ADD COLUMN request_hash TEXT NOT NULL DEFAULT '';",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<DurableJobRecord>> RecoverInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await using (var recover = connection.CreateCommand())
            {
                recover.Transaction = transaction;
                recover.CommandText = """
                    UPDATE live_jobs
                    SET state=$recovery,
                        phase=$phase,
                        message=$message,
                        updated_at=$updated
                    WHERE state IN ($queued,$validating,$executing,$verifying);
                    """;
                recover.Parameters.AddWithValue("$recovery", JobState.RecoveryRequired.ToString());
                recover.Parameters.AddWithValue("$phase", "recoveryrequired");
                recover.Parameters.AddWithValue("$message", RestartRecoveryMessage);
                recover.Parameters.AddWithValue("$updated", now);
                recover.Parameters.AddWithValue("$queued", JobState.Queued.ToString());
                recover.Parameters.AddWithValue("$validating", JobState.Validating.ToString());
                recover.Parameters.AddWithValue("$executing", JobState.Executing.ToString());
                recover.Parameters.AddWithValue("$verifying", JobState.Verifying.ToString());
                await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var records = await ReadAllAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return records;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DurableJobInsertResult> InsertOrReadAsync(
        DurableJobRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO live_jobs(
                    job_id,session_id,idempotency_key,summary,change_set_json,
                    enqueue_sequence,state,phase,message,enqueued_at,created_at,updated_at,request_hash)
                VALUES(
                    $job,$session,$key,$summary,$changeSet,
                    $sequence,$state,$phase,$message,$enqueued,$created,$updated,$requestHash);
                """;
            Bind(insert, record);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            var stored = inserted
                ? record
                : await FindByIdempotencyAsync(
                    connection,
                    transaction,
                    record.SessionId,
                    record.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "A durable job key collision occurred without an idempotency match.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurableJobInsertResult(inserted, stored);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UpdateStateAsync(
        Guid jobId,
        JobState state,
        string phase,
        string? message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE live_jobs
                SET state=$state,phase=$phase,message=$message,updated_at=$updated
                WHERE job_id=$job;
                """;
            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue("$phase", phase);
            command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$updated",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$job", jobId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Durable job '{jobId:D}' was not found.");
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

    private static void Bind(SqliteCommand command, DurableJobRecord record)
    {
        command.Parameters.AddWithValue("$job", record.JobId.ToString("D"));
        command.Parameters.AddWithValue("$session", record.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue(
            "$changeSet",
            JsonSerializer.Serialize(record.ChangeSet, BridgeProtocol.JsonOptions));
        command.Parameters.AddWithValue("$sequence", record.EnqueueSequence);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$phase", record.Phase);
        command.Parameters.AddWithValue("$message", (object?)record.Message ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$enqueued",
            record.EnqueuedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$created",
            record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$updated",
            record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$requestHash", record.RequestHash);
    }

    private static async Task<IReadOnlyList<DurableJobRecord>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id,session_id,idempotency_key,summary,change_set_json,
                   enqueue_sequence,state,phase,message,enqueued_at,created_at,updated_at,request_hash
            FROM live_jobs
            ORDER BY enqueue_sequence,created_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<DurableJobRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Map(reader));
        }
        return records;
    }

    private static async Task<DurableJobRecord?> FindByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id,session_id,idempotency_key,summary,change_set_json,
                   enqueue_sequence,state,phase,message,enqueued_at,created_at,updated_at,request_hash
            FROM live_jobs
            WHERE session_id=$session AND idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static DurableJobRecord Map(SqliteDataReader reader)
    {
        var changeSet = JsonSerializer.Deserialize<ChangeSet>(reader.GetString(4), BridgeProtocol.JsonOptions)
            ?? throw new InvalidDataException("A durable job contains an empty ChangeSet.");
        if (!Enum.TryParse<JobState>(reader.GetString(6), ignoreCase: true, out var state))
        {
            throw new InvalidDataException($"A durable job contains invalid state '{reader.GetString(6)}'.");
        }
        return new DurableJobRecord(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            changeSet,
            reader.GetInt64(5),
            state,
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            reader.GetString(12));
    }
}
