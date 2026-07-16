using System.Text.Json;

namespace GPTino.Terminal;

internal sealed record ChatMessage(
    long Id,
    Guid SessionId,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

internal sealed record SendMessageRequest(string Content, string ClientMessageId);

internal sealed record SetPausedRequest(bool Paused);

internal sealed record AcceptedTurn(Guid SessionId, long MessageId, string State);

internal sealed record ApiError(string? Code, string? Message);

internal sealed record SessionStatus(
    string ProjectName,
    string Health,
    bool RuntimePaused,
    string SessionTitle,
    string SessionStatusValue,
    string? ModelProfile,
    string? EffectiveModel,
    bool SessionPaused)
{
    public static SessionStatus Parse(JsonElement root, Guid sessionId)
    {
        var projectName = ReadString(root, "projectName") ?? "GPTino";
        var health = ReadString(root, "health") ?? "unknown";
        var runtimePaused = ReadBoolean(root, "paused");
        if (!root.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
        {
            throw new TerminalProtocolException("Runtime response did not contain a sessions array.");
        }

        foreach (var session in sessions.EnumerateArray())
        {
            if (!Guid.TryParse(ReadString(session, "id"), out var id) || id != sessionId)
            {
                continue;
            }

            return new SessionStatus(
                projectName,
                health,
                runtimePaused,
                ReadString(session, "title") ?? sessionId.ToString("D"),
                ReadString(session, "status") ?? "unknown",
                ReadString(session, "modelProfile"),
                ReadString(session, "effectiveModel"),
                ReadBoolean(session, "paused"));
        }

        throw new TerminalProtocolException($"Session {sessionId:D} was not present in the runtime response.");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();
}

internal sealed class TerminalProtocolException : Exception
{
    public TerminalProtocolException(string message)
        : base(message)
    {
    }
}
