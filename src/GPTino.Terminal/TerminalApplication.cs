using System.Net.Http;

namespace GPTino.Terminal;

internal sealed class TerminalApplication
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private readonly CliArguments _arguments;
    private readonly ITerminalApiClient _api;
    private readonly ITerminalView _view;

    public TerminalApplication(CliArguments arguments, ITerminalApiClient api, ITerminalView view)
    {
        _arguments = arguments;
        _api = api;
        _view = view;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await _view.WriteBannerAsync(
            _arguments.Title,
            _arguments.SessionId,
            _arguments.Endpoint,
            cancellationToken).ConfigureAwait(false);

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pollTask = PollMessagesAsync(runCancellation.Token);
        try
        {
            await InputLoopAsync(runCancellation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
            }
        }

        await _view.WriteNoticeAsync("Detached from session.", CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private async Task InputLoopAsync(CancellationTokenSource runCancellation, CancellationToken cancellationToken)
    {
        while (!runCancellation.IsCancellationRequested)
        {
            string? input;
            try
            {
                input = await _view.ReadLineAsync(runCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
                break;
            }

            if (input is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var command = TerminalCommand.Parse(input);
            try
            {
                switch (command.Kind)
                {
                    case TerminalCommandKind.Message:
                        await _api.SendMessageAsync(
                            command.Content!,
                            Guid.NewGuid().ToString("N"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Pause:
                        await _api.SetPausedAsync(true, cancellationToken).ConfigureAwait(false);
                        await _view.WriteNoticeAsync("Session paused.", cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Resume:
                        await _api.SetPausedAsync(false, cancellationToken).ConfigureAwait(false);
                        await _view.WriteNoticeAsync("Session resumed.", cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Status:
                        await _view.WriteStatusAsync(
                            await _api.ReadStatusAsync(cancellationToken).ConfigureAwait(false),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Help:
                        await _view.WriteNoticeAsync(
                            "Commands: /pause, /resume, /status, /help, /quit. Any other text is sent to this session.",
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Quit:
                        await runCancellation.CancelAsync().ConfigureAwait(false);
                        break;
                    case TerminalCommandKind.Unknown:
                        await _view.WriteErrorAsync(
                            $"Unknown command '{command.Content}'. Type /help.",
                            cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported terminal command {command.Kind}.");
                }
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                await _view.WriteErrorAsync(exception.Message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollMessagesAsync(CancellationToken cancellationToken)
    {
        long after = 0;
        var initialCatchup = true;
        var retryDelay = PollInterval;
        string? lastError = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var messages = await _api.ReadMessagesAsync(after, cancellationToken).ConfigureAwait(false);
                var unseen = messages
                    .Where(message => message.Id > after)
                    .OrderBy(message => message.Id)
                    .ToArray();
                if (unseen.Length > 0)
                {
                    await _view.WriteMessagesAsync(unseen, initialCatchup, cancellationToken).ConfigureAwait(false);
                    after = unseen[^1].Id;
                }

                retryDelay = PollInterval;
                lastError = null;
                if (messages.Count < 250)
                {
                    initialCatchup = false;
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                if (!string.Equals(lastError, exception.Message, StringComparison.Ordinal))
                {
                    await _view.WriteErrorAsync(
                        $"Message polling interrupted: {exception.Message}. Retrying…",
                        cancellationToken).ConfigureAwait(false);
                    lastError = exception.Message;
                }
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, MaximumRetryDelay.TotalMilliseconds));
            }
        }
    }

    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or TaskCanceledException or TerminalApiException or TerminalProtocolException;
}
