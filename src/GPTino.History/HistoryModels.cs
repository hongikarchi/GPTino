using System.Collections.ObjectModel;

namespace GPTino.History;

public sealed record ProjectManifest(
    int SchemaVersion,
    Guid ProjectId,
    string RhinoPath,
    string GrasshopperPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAttachedAt,
    string? BaselineCommit);

public sealed record HistoryCommitMetadata(
    int Revision,
    Guid ProjectId,
    Guid SessionId,
    Guid TaskId,
    string SnapshotId,
    string ChangeSetHash,
    string ModelProfile,
    string Summary);

public sealed record HistoryCommitRequest(
    string? ExpectedHead,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files,
    HistoryCommitMetadata Metadata)
{
    public static HistoryCommitRequest Create(
        string? expectedHead,
        IReadOnlyDictionary<string, string> textFiles,
        HistoryCommitMetadata metadata) =>
        new(
            expectedHead,
            new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(
                textFiles.ToDictionary(
                    pair => pair.Key,
                    pair => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(pair.Value),
                    StringComparer.Ordinal)),
            metadata);
}

public sealed record HistoryCommitResult(
    string Head,
    bool CreatedCommit,
    int ChangedFiles,
    DateTimeOffset CommittedAt);

public sealed record HistoryVerificationResult(
    bool IsValid,
    string? Head,
    IReadOnlyList<string> Problems);

public sealed class HistoryConcurrencyException(string message) : InvalidOperationException(message);

public sealed class HistoryPathException(string message) : InvalidOperationException(message);
