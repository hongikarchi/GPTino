using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CanvasSceneAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.ScriptAdapter;

namespace GPTino.AgentHost.Runtime;

// Nested DTO records and per-target/per-job state types shared by the backend partials.
public sealed partial class LiveDocumentBackend
{
    private sealed record ResourceObservation(ResourceAddress Resource, string? Fingerprint);

    internal sealed record JobDiagnostic(
        string OperationId,
        BridgeDiagnosticSeverity Severity,
        string Code,
        string Message);

    /// <summary>
    /// The Grasshopper-assigned socket identities of a component this job reshaped, read from the
    /// post-write snapshot, so the session can wire without a follow-up snapshot_read round trip.
    /// </summary>
    private sealed record JobComponentSockets(
        Guid ComponentId,
        IReadOnlyList<JobSocket> Inputs,
        IReadOnlyList<JobSocket> Outputs);

    private sealed record JobSocket(
        Guid Id,
        string Name,
        string NickName,
        string? TypeHint,
        string Access);

    /// <summary>Post-solve canvas.inspectOutputs result for one written component.</summary>
    internal sealed record JobComponentOutputs(Guid ComponentId, JsonElement Inspection);

    // Origin defaults to Observed — the SAFE default: an origin-less construction can serve as a
    // CAS baseline (auto-fill / self-stale rebase ignore origin) but never as delete authorization.
    internal readonly record struct ResourceLedgerEntry(
        ResourceAddress Resource,
        string Fingerprint,
        Guid SessionId,
        long Revision,
        ResourceLedgerOrigin Origin = ResourceLedgerOrigin.Observed);

    /// <summary>
    /// Post-commit chaining data: the fresh snapshot identity plus the committed write
    /// resources' fingerprints, so a session can base its next ChangeSet on job_status
    /// instead of paying another full snapshot_read.
    /// </summary>
    private sealed record CommittedJobView(
        string SnapshotId,
        long Revision,
        IReadOnlyList<CommittedResourceFingerprint> Resources);

    private sealed record CommittedResourceFingerprint(ResourceAddress Resource, string? Fingerprint);

    // internal (not private) so the pure CanvasAutoPlacement.ResolveAutoPivots wrapper in this same
    // assembly can accept the prepared list and return a rewritten one without a broader refactor.
    internal sealed record PreparedOperation(
        TypedOperation Operation,
        BridgeAdapterOwner Owner,
        string BridgeOperation,
        JsonElement Arguments,
        byte[] FrozenPayload,
        string PayloadSha256);

    private sealed record SnapshotEnvelope(
        string SnapshotId,
        StateSnapshot State,
        CanvasSnapshot Canvas);

    /// <summary>
    /// Per-registered-Grasshopper-document state: the live target (freshest registration), its
    /// advertised adapters, the per-document snapshot cache + capture gate, the last selection
    /// event, and the lazily created per-docKey managed history. Membership and Target/Adapters/
    /// DocKey mutations happen under _connectionGate; Snapshot follows the former singleton
    /// field's benign-race discipline; Selection is written under _connectionGate.
    /// </summary>
    private sealed class TargetState(DocumentRuntime target, string docKey, long sequence)
    {
        public DocumentRuntime Target { get; set; } = target;

        /// <summary>Durable path-derived docKey; recomputed on re-registration (Save As).</summary>
        public string DocKey { get; set; } = docKey;

        /// <summary>Registration order; the smallest live sequence is the DEFAULT target.</summary>
        public long Sequence { get; } = sequence;

        public HashSet<BridgeAdapterOwner> Adapters { get; set; } = [];

        public SnapshotEnvelope? Snapshot { get; set; }

        public SemaphoreSlim SnapshotGate { get; } = new(1, 1);

        public SelectionChangedEvent? Selection { get; set; }

        /// <summary>Backend receipt ordinal of <see cref="Selection"/>; written under _connectionGate.</summary>
        public long SelectionSequence { get; set; }

        /// <summary>Backend receipt time of <see cref="Selection"/>; written under _connectionGate.</summary>
        public DateTimeOffset SelectionStamp { get; set; }

        public ManagedHistoryRepository? History { get; set; }
    }

    /// <summary>
    /// A bridge call awaiting its response, remembering the exact target it was stamped with so
    /// the response guard and per-document failure paths never cross documents.
    /// </summary>
    private sealed record PendingBridgeRequest(
        TaskCompletionSource<BridgeFrame> Completion,
        DocumentRuntime ExpectedTarget,
        string ExpectedTargetKey);

    private sealed record ScopedInspection(
        string Scope,
        BridgeAdapterOwner Owner,
        string Operation,
        string? Fingerprint,
        JsonElement Result,
        IReadOnlyList<BridgeDiagnostic> Diagnostics);

    private sealed record QueuedConflict(Guid OtherJobId, ChangeConflict Conflict);

    /// <summary>
    /// Session-level recovery halt latch: set when a job of the session ends RecoveryRequired,
    /// removed only by an explicit resume (model tool recovery_resume or the panel's resume
    /// button) that names the halting job. While latched, fresh submissions are refused, queued
    /// jobs are cancelled, the scheduler treats the session as Blocked, and the post-turn
    /// auto-tidy never runs — the incident's canvas state stays untouched for review.
    /// </summary>
    internal sealed record SessionHaltState(Guid JobId, string Message, DateTimeOffset At);

    /// <summary>
    /// Result of a resume attempt: <c>Resumed</c> is true when the halt was lifted or the session
    /// was not halted (idempotent); on a jobId mismatch it is false and <c>Halt</c> carries the
    /// current halt so the caller can self-correct.
    /// </summary>
    internal sealed record SessionResumeOutcome(bool Resumed, SessionHaltState? Halt);

    private sealed class LiveJobEntry(
        QueuedJob job,
        SessionRecord session,
        string summary,
        string idempotencyKey,
        string requestHash,
        IReadOnlyList<QueuedConflict> conflicts,
        string? targetDoc = null)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<JobExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private JobState _state = JobState.Queued;
        private string _phase = "queued";
        private string? _message;
        private DateTimeOffset _updatedAt = job.EnqueuedAt;

        public QueuedJob Job { get; } = job;
        public SessionRecord Session { get; } = session;

        /// <summary>
        /// Resolved user-approval items (objectId -> audited fingerprint) from the ChangeSet's
        /// approvalGrantId; null when no grant was supplied. In-memory only: interrupted jobs
        /// never execute after a restart, so grants need no durability.
        /// </summary>
        public IReadOnlyDictionary<Guid, string>? ApprovalItems { get; init; }

        /// <summary>Source grant id, so a committed job can consume its covered objects.</summary>
        public string? ApprovalGrantId { get; init; }
        public string Summary { get; } = summary;
        public string IdempotencyKey { get; } = idempotencyKey;
        public string RequestHash { get; } = requestHash;
        public IReadOnlyList<QueuedConflict> Conflicts { get; } = conflicts;

        private string? _targetDoc = targetDoc;

        /// <summary>
        /// Durable docKey of the Grasshopper document this job was resolved to at submit time;
        /// null on legacy/recovered rows (default-document resolution at execute time).
        /// Re-keyed in place (under the backend's _connectionGate) when a Save As
        /// re-registration recomputes the target's docKey, so queued jobs keep resolving.
        /// </summary>
        public string? TargetDoc => Volatile.Read(ref _targetDoc);

        /// <summary>Follows a Save As docKey rename; never changes which document the job targets.</summary>
        public void RemapTargetDoc(string? targetDoc) => Volatile.Write(ref _targetDoc, targetDoc);

        /// <summary>
        /// Written once when the job goes Blocked: the structured conflicts that stopped it, so
        /// the panel can show the concrete resource instead of only the flattened prose message.
        /// </summary>
        public IReadOnlyList<ChangeConflict>? BlockingConflicts { get; set; }

        /// <summary>Written once by the single-writer executor just before Committed.</summary>
        public CommittedJobView? Committed { get; set; }

        /// <summary>
        /// Written once whenever the writes landed and the post-state is fully known: on commit
        /// (same view as Committed) and on deterministic verification failure. A failed job with
        /// Applied means "the change is live but not committed — fix and resubmit against these
        /// fingerprints"; committed stays success-only.
        /// </summary>
        public CommittedJobView? Applied { get; set; }

        /// <summary>
        /// Written once at a terminal transition: the per-operation bridge diagnostics the
        /// executor collected, so job_status carries errors/warnings/remarks without another read.
        /// </summary>
        public IReadOnlyList<JobDiagnostic>? Diagnostics { get; set; }

        /// <summary>Written once alongside Committed: post-solve socket map for I/O-writing jobs.</summary>
        public IReadOnlyList<JobComponentSockets>? Sockets { get; set; }

        /// <summary>Written once alongside Committed: post-solve output inspections per written component.</summary>
        public IReadOnlyList<JobComponentOutputs>? Outputs { get; set; }

        /// <summary>
        /// Resolves after the terminal phase has been recorded, so an awaiter that wakes always
        /// projects the terminal state. Duplicate submissions can safely share this task.
        /// </summary>
        public Task<JobExecutionResult> Completion => _completion.Task;

        public void CompleteWith(JobExecutionResult result) => _completion.TrySetResult(result);

        public JobState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public string Phase
        {
            get
            {
                lock (_gate)
                {
                    return _phase;
                }
            }
        }

        public string? Message
        {
            get
            {
                lock (_gate)
                {
                    return _message;
                }
            }
        }

        public DateTimeOffset UpdatedAt
        {
            get
            {
                lock (_gate)
                {
                    return _updatedAt;
                }
            }
        }

        public void SetPhase(
            JobState state,
            string phase,
            string? message,
            DateTimeOffset? updatedAt = null)
        {
            lock (_gate)
            {
                _state = state;
                _phase = phase;
                _message = message;
                _updatedAt = updatedAt ?? DateTimeOffset.UtcNow;
            }
        }
    }
}
