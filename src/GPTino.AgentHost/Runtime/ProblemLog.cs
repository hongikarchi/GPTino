using System.Text.Json;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;
using GPTino.Contracts;
using GPTino.Core;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Append-only JSONL trail (problem-log.jsonl in the project data root) of job state transitions
/// and detected conflicts, with the structured fields — conflict kind, resource address — that
/// live-jobs.db flattens into prose. This is the harvestable record for the usage-data pipeline:
/// what blocked or failed, why, and how the run continued. Writes are best-effort and never throw
/// into the broker path.
/// </summary>
public sealed class ProblemLog
{
    private readonly string _path;
    private readonly ILogger<ProblemLog> _logger;
    private readonly object _gate = new();
    private bool _writeFailed;

    public ProblemLog(AgentHostOptions options, ILogger<ProblemLog> logger)
    {
        _path = Path.Combine(options.ResolveDataDirectory(), "problem-log.jsonl");
        _logger = logger;
    }

    public void RecordJobState(
        Guid jobId,
        Guid sessionId,
        string summary,
        JobState state,
        string? message,
        IReadOnlyList<ChangeConflict>? conflicts = null)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "job-state",
            jobId,
            sessionId,
            summary,
            state = state.ToString(),
            message,
            conflicts = conflicts?.Select(conflict => new
            {
                kind = conflict.Kind.ToString(),
                resource = FormatResource(conflict.Resource),
                message = conflict.Message
            }).ToArray()
        });
    }

    public void RecordQueuedConflict(Guid jobId, Guid sessionId, Guid otherJobId, ChangeConflict conflict)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "queued-conflict",
            jobId,
            sessionId,
            otherJobId,
            conflictKind = conflict.Kind.ToString(),
            resource = FormatResource(conflict.Resource),
            message = conflict.Message
        });
    }

    internal static string? FormatResource(ResourceAddress? resource) =>
        resource is null ? null : $"{resource.Kind}:{resource.Id}:{resource.Field}";

    private void Append(object record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, JsonDefaults.Options);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            _writeFailed = false;
        }
        catch (Exception exception)
        {
            if (!_writeFailed)
            {
                _writeFailed = true;
                _logger.LogWarning(exception, "Problem log append failed; further failures are suppressed until a write succeeds.");
            }
        }
    }
}
