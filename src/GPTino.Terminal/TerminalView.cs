namespace GPTino.Terminal;

internal interface ITerminalView
{
    Task WriteBannerAsync(string title, Guid sessionId, Uri endpoint, CancellationToken cancellationToken);

    Task WriteMessagesAsync(IReadOnlyList<ChatMessage> messages, bool isHistory, CancellationToken cancellationToken);

    Task WriteStatusAsync(SessionStatus status, CancellationToken cancellationToken);

    Task WriteNoticeAsync(string message, CancellationToken cancellationToken);

    Task WriteErrorAsync(string message, CancellationToken cancellationToken);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

internal sealed class TerminalView : ITerminalView, IDisposable
{
    private const string Prompt = "you> ";
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private bool _promptVisible;

    public TerminalView(TextReader input, TextWriter output, TextWriter error)
    {
        _input = input;
        _output = output;
        _error = error;
    }

    public async Task WriteBannerAsync(string title, Guid sessionId, Uri endpoint, CancellationToken cancellationToken)
    {
        await WithOutputAsync(_output, async writer =>
        {
            await writer.WriteLineAsync(new string('=', 72)).ConfigureAwait(false);
            await writer.WriteLineAsync($" GPTino · {title}").ConfigureAwait(false);
            await writer.WriteLineAsync($" Session  {sessionId:D}").ConfigureAwait(false);
            await writer.WriteLineAsync($" Host     {endpoint.GetLeftPart(UriPartial.Authority)}").ConfigureAwait(false);
            await writer.WriteLineAsync(" Commands /pause  /resume  /status  /help  /quit").ConfigureAwait(false);
            await writer.WriteLineAsync(new string('=', 72)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        bool isHistory,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await WithOutputAsync(_output, async writer =>
        {
            await writer.WriteLineAsync(isHistory ? "--- session history ---" : "--- new activity ---").ConfigureAwait(false);
            foreach (var message in messages)
            {
                var role = DisplayRole(message.Role);
                var phase = string.IsNullOrWhiteSpace(message.Phase) ? string.Empty : $" · {message.Phase}";
                await writer.WriteLineAsync($"[{message.CreatedAt.ToLocalTime():HH:mm:ss}] {role}{phase}").ConfigureAwait(false);
                foreach (var line in NormalizeLines(message.Content))
                {
                    await writer.WriteLineAsync($"  {line}").ConfigureAwait(false);
                }
            }
            await writer.WriteLineAsync(new string('-', 72)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteStatusAsync(SessionStatus status, CancellationToken cancellationToken) =>
        WithOutputAsync(_output, async writer =>
        {
            var model = status.EffectiveModel ?? status.ModelProfile ?? "automatic";
            await writer.WriteLineAsync("--- status ---").ConfigureAwait(false);
            await writer.WriteLineAsync($" Project  {status.ProjectName} ({status.Health})").ConfigureAwait(false);
            await writer.WriteLineAsync($" Session  {status.SessionTitle} · {status.SessionStatusValue}").ConfigureAwait(false);
            await writer.WriteLineAsync($" Model    {model}").ConfigureAwait(false);
            await writer.WriteLineAsync($" Paused   session={status.SessionPaused.ToString().ToLowerInvariant()}, runtime={status.RuntimePaused.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            await writer.WriteLineAsync(new string('-', 72)).ConfigureAwait(false);
        }, cancellationToken);

    public Task WriteNoticeAsync(string message, CancellationToken cancellationToken) =>
        WithOutputAsync(_output, writer => writer.WriteLineAsync($"[gptino] {message}"), cancellationToken);

    public Task WriteErrorAsync(string message, CancellationToken cancellationToken) =>
        WithOutputAsync(_error, writer => writer.WriteLineAsync($"[error] {message}"), cancellationToken);

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(Prompt).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            _promptVisible = true;
        }
        finally
        {
            _outputGate.Release();
        }

        try
        {
            return await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _promptVisible = false;
        }
    }

    public void Dispose() => _outputGate.Dispose();

    private async Task WithOutputAsync(
        TextWriter writer,
        Func<TextWriter, Task> action,
        CancellationToken cancellationToken)
    {
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_promptVisible)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }
            await action(writer).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_promptVisible)
            {
                await _output.WriteAsync(Prompt).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _outputGate.Release();
        }
    }

    private static IEnumerable<string> NormalizeLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string DisplayRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "user" => "YOU",
        "assistant" => "AGENT",
        "system" => "SYSTEM",
        _ => role.ToUpperInvariant(),
    };
}
