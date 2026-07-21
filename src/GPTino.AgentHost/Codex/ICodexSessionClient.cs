using System.Text.Json;

namespace GPTino.AgentHost.Codex;

public interface ICodexSessionClient
{
    event Func<string, JsonElement, Task>? NotificationReceived;

    bool IsRunning { get; }

    Task<string> StartThreadAsync(
        string cwd,
        string? model,
        CancellationToken cancellationToken = default);

    Task ResumeThreadAsync(
        string threadId,
        string cwd,
        string? model,
        CancellationToken cancellationToken = default);

    Task<string> StartTurnAsync(
        string threadId,
        string message,
        string? model,
        string? effort,
        CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<CodexTurnReadResult?> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}
