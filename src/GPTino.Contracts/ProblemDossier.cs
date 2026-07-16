namespace GPTino.Contracts;

public sealed record ProblemAttempt(
    int Number,
    Guid? SessionId,
    string Hypothesis,
    string? ChangeSetHash,
    string Outcome,
    string? ErrorFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record ProblemDossier(
    Guid TaskId,
    string Objective,
    IReadOnlyList<string> Constraints,
    string ContextCapsule,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<ProblemAttempt> Attempts,
    string? CurrentBest,
    IReadOnlyList<VerificationPredicate> AcceptancePredicates,
    DateTimeOffset UpdatedAt);

public sealed record IdempotencyKey(
    Guid ProjectRuntimeId,
    string ThreadId,
    string TurnId,
    string CallId);
