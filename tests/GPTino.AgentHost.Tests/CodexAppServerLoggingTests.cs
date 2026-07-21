using System.Reflection;
using System.Text;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;
using Microsoft.Extensions.Logging;

namespace GPTino.AgentHost.Tests;

public sealed class CodexAppServerLoggingTests
{
    [Fact]
    public async Task StandardErrorLogDoesNotEchoChildOutput()
    {
        const string sensitiveLine = "fail: credential=do-not-log";
        var logger = new RecordingLogger<CodexAppServerClient>();
        await using var client = new CodexAppServerClient(
            new AgentHostOptions { ProjectDirectory = Directory.GetCurrentDirectory() },
            logger);
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(sensitiveLine + Environment.NewLine));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var method = typeof(CodexAppServerClient).GetMethod(
            "ReadErrorLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            client,
            [reader, CancellationToken.None]));
        await task;

        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain(sensitiveLine, message, StringComparison.Ordinal);
        Assert.Contains(sensitiveLine.Length.ToString(), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardErrorLoggingBoundsOversizedAndNoisyStreams()
    {
        const string sensitiveMarker = "do-not-log-this-record";
        var line = sensitiveMarker + new string('x', 10_000);
        var payload = string.Join(
            Environment.NewLine,
            Enumerable.Repeat(line, 100));
        var logger = new RecordingLogger<CodexAppServerClient>();
        await using var client = new CodexAppServerClient(
            new AgentHostOptions { ProjectDirectory = Directory.GetCurrentDirectory() },
            logger);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var method = typeof(CodexAppServerClient).GetMethod(
            "ReadErrorLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            client,
            [reader, CancellationToken.None]));
        await task;

        Assert.Equal(33, logger.Messages.Count);
        Assert.Contains("suppressed", logger.Messages[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            sensitiveMarker,
            string.Join(Environment.NewLine, logger.Messages),
            StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
