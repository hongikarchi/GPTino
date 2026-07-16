namespace GPTino.Terminal;

internal enum TerminalCommandKind
{
    Message,
    Pause,
    Resume,
    Status,
    Help,
    Quit,
    Unknown,
}

internal sealed record TerminalCommand(TerminalCommandKind Kind, string? Content = null)
{
    public static TerminalCommand Parse(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return new TerminalCommand(TerminalCommandKind.Message, trimmed);
        }

        return trimmed.ToLowerInvariant() switch
        {
            "/pause" => new TerminalCommand(TerminalCommandKind.Pause),
            "/resume" => new TerminalCommand(TerminalCommandKind.Resume),
            "/status" => new TerminalCommand(TerminalCommandKind.Status),
            "/help" => new TerminalCommand(TerminalCommandKind.Help),
            "/quit" or "/exit" => new TerminalCommand(TerminalCommandKind.Quit),
            _ => new TerminalCommand(TerminalCommandKind.Unknown, trimmed),
        };
    }
}
