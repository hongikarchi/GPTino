namespace GPTino.Terminal;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = CliArguments.Parse(args);
        if (!parsed.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(parsed.Error))
            {
                await Console.Error.WriteLineAsync(parsed.Error).ConfigureAwait(false);
            }
            await Console.Error.WriteLineAsync(CliArguments.Usage).ConfigureAwait(false);
            return parsed.ShowHelp ? 0 : 2;
        }

        var arguments = parsed.Arguments!;
        TrySetTitle(arguments.Title);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            using var api = new GptinoApiClient(arguments);
            using var view = new TerminalView(Console.In, Console.Out, Console.Error);
            var application = new TerminalApplication(arguments, api, view);
            return await application.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TerminalApiException or TerminalProtocolException)
        {
            await Console.Error.WriteLineAsync($"GPTino terminal could not attach: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void TrySetTitle(string title)
    {
        try
        {
            Console.Title = $"GPTino · {title}";
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
