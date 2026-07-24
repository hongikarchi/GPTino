using System.Text.Json;
using GPTino.AgentHost.Data;
using GPTino.BridgeContract;
using GPTino.Contracts;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Tests;

public sealed class DurableJobStoreTests
{
    [Fact]
    public async Task InitializeMigratesTargetDocColumnOntoPreExistingDatabase()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("live-jobs.db");
        var legacy = CreateRecord(JobState.Queued);
        await CreateLegacySchemaDatabaseAsync(databasePath, legacy);

        var store = new DurableJobStore(databasePath);
        await store.InitializeAsync();
        var boundRecord = CreateRecord(JobState.Executing) with { TargetDoc = "0123456789abcdef" };
        var inserted = await store.InsertOrReadAsync(boundRecord);
        var recovered = await store.RecoverInterruptedAsync();

        Assert.True(inserted.Inserted);
        // The legacy row reads a NULL frozen target (default-document resolution) with no backfill,
        // and the new column round-trips through insert + startup recovery.
        var legacyRecovered = Assert.Single(recovered, record => record.JobId == legacy.JobId);
        Assert.Null(legacyRecovered.TargetDoc);
        Assert.Equal(JobState.RecoveryRequired, legacyRecovered.State);
        var boundRecovered = Assert.Single(recovered, record => record.JobId == boundRecord.JobId);
        Assert.Equal("0123456789abcdef", boundRecovered.TargetDoc);
        Assert.Equal(JobState.RecoveryRequired, boundRecovered.State);
    }

    [Fact]
    public async Task RemapTargetDocRewritesEveryMatchingFrozenJob()
    {
        using var directory = new TestDirectory();
        var store = new DurableJobStore(directory.GetPath("live-jobs.db"));
        await store.InitializeAsync();
        // Uppercase stored value: the remap matches case-insensitively (docKeys are canonical
        // lowercase, but the column is unvalidated) and always writes the canonical form.
        var renamed = CreateRecord(JobState.Queued) with { TargetDoc = "AAAA000011112222" };
        var untouched = CreateRecord(JobState.Queued) with { TargetDoc = "ffffeeeeddddcccc" };
        _ = await store.InsertOrReadAsync(renamed);
        _ = await store.InsertOrReadAsync(untouched);

        var affected = await store.RemapTargetDocAsync("aaaa000011112222", "BBBB333344445555");

        Assert.Equal(1, affected);
        var recovered = await store.RecoverInterruptedAsync();
        Assert.Equal(
            "bbbb333344445555",
            Assert.Single(recovered, record => record.JobId == renamed.JobId).TargetDoc);
        Assert.Equal(
            "ffffeeeeddddcccc",
            Assert.Single(recovered, record => record.JobId == untouched.JobId).TargetDoc);
    }

    private static async Task CreateLegacySchemaDatabaseAsync(
        string databasePath,
        DurableJobRecord record)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE live_jobs (
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
            INSERT INTO live_jobs(
                job_id,session_id,idempotency_key,summary,change_set_json,
                enqueue_sequence,state,phase,message,enqueued_at,created_at,updated_at,request_hash)
            VALUES(
                $job,$session,$key,$summary,$changeSet,
                $sequence,$state,$phase,NULL,$stamp,$stamp,$stamp,$requestHash);
            """;
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
        command.Parameters.AddWithValue("$stamp", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$requestHash", record.RequestHash);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Validating)]
    [InlineData(JobState.Executing)]
    [InlineData(JobState.Verifying)]
    public async Task StartupConvertsEveryInterruptedPhaseWithoutChangingJobIdentity(JobState state)
    {
        using var directory = new TestDirectory();
        var store = new DurableJobStore(directory.GetPath("live-jobs.db"));
        await store.InitializeAsync();
        var record = CreateRecord(state);
        var inserted = await store.InsertOrReadAsync(record);

        var recovered = Assert.Single(await store.RecoverInterruptedAsync());

        Assert.True(inserted.Inserted);
        Assert.Equal(record.JobId, recovered.JobId);
        Assert.Equal(record.ChangeSet.ChangeSetId, recovered.ChangeSet.ChangeSetId);
        Assert.Equal(record.ChangeSet.ProjectId, recovered.ChangeSet.ProjectId);
        Assert.Equal(record.ChangeSet.SessionId, recovered.ChangeSet.SessionId);
        Assert.Equal(record.RequestHash, recovered.RequestHash);
        Assert.Equal(JobState.RecoveryRequired, recovered.State);
        Assert.Equal("recoveryrequired", recovered.Phase);
        Assert.Equal(DurableJobStore.RestartRecoveryMessage, recovered.Message);
    }

    [Fact]
    public async Task TerminalJobRemainsQueryableAndIdempotentAcrossStartupRecovery()
    {
        using var directory = new TestDirectory();
        var store = new DurableJobStore(directory.GetPath("live-jobs.db"));
        await store.InitializeAsync();
        var record = CreateRecord(JobState.Committed) with
        {
            Phase = "committed",
            Message = "Verified and committed."
        };
        _ = await store.InsertOrReadAsync(record);

        var restored = Assert.Single(await store.RecoverInterruptedAsync());
        var duplicate = await store.InsertOrReadAsync(record with { JobId = Guid.NewGuid() });

        Assert.Equal(JobState.Committed, restored.State);
        Assert.Equal(record.JobId, duplicate.Record.JobId);
        Assert.False(duplicate.Inserted);
    }

    private static DurableJobRecord CreateRecord(JobState state)
    {
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            projectId,
            sessionId,
            1,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            Array.Empty<ResourceExpectation>(),
            Array.Empty<TypedOperation>(),
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            now);
        return new DurableJobRecord(
            Guid.NewGuid(),
            sessionId,
            "same-request",
            "Durability test",
            changeSet,
            7,
            state,
            state.ToString().ToLowerInvariant(),
            null,
            now,
            now,
            now,
            "sha256-request");
    }
}
