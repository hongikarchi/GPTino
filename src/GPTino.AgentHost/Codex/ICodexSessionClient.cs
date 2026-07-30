using System.Text.Json;

namespace GPTino.AgentHost.Codex;

public interface ICodexSessionClient
{
    event Func<string, JsonElement, Task>? NotificationReceived;

    bool IsRunning { get; }

    Task<string> StartThreadAsync(
        string cwd,
        string? model,
        string? roleInstructions = null,
        CancellationToken cancellationToken = default);

    Task ResumeThreadAsync(
        string threadId,
        string cwd,
        string? model,
        string? roleInstructions = null,
        CancellationToken cancellationToken = default);

    Task<string> StartTurnAsync(
        string threadId,
        string message,
        string? model,
        string? effort,
        IReadOnlyList<string>? imagePaths = null,
        CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Injects input into an already-running turn (turn/steer) so the model course-corrects without
    /// a restart. Fails if <paramref name="turnId"/> is not the thread's active turn — callers fall
    /// back to starting a fresh turn.
    /// </summary>
    Task SteerTurnAsync(
        string threadId,
        string turnId,
        string message,
        IReadOnlyList<string>? imagePaths = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the thread's native goal (objective + optional token budget) so Codex tracks progress
    /// toward it. Best-effort — a failure never blocks the turn.
    /// </summary>
    Task SetThreadGoalAsync(
        string threadId,
        string objective,
        long? tokenBudget,
        CancellationToken cancellationToken = default);

    Task<CodexTurnReadResult?> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}
