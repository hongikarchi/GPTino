using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GPTino.BridgeContract;

/// <summary>
/// Emits secret-free startup breadcrumbs only inside a validated DevLoop run.
/// Production launches do not set GPTINO_DEV_MODE and therefore never write a trace.
/// </summary>
public static class DevelopmentDiagnosticTrace
{
    private const int MaximumTraceBytes = 4 * 1024 * 1024;
    private static readonly object WriteGate = new();
    private static readonly UTF8Encoding BomlessUtf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, byte> ExhaustedDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Appends one JSON line to a PER-PROCESS trace file. The previous design wrote one file per
    /// record behind a 250ms mutex shared by Rhino, Grasshopper and the AgentHost — under the
    /// contention of a busy moment it failed to take the lock and dropped the record silently.
    /// Diagnostics that vanish exactly when something interesting happens are worse than none: a
    /// whole debugging session was spent reading "no trace" as evidence. One file per process needs
    /// no cross-process lock at all, and a byte cap keeps a runaway loop from filling the disk.
    /// </summary>
    public static void TryWrite(string component, string eventName, string? detail = null)
    {
        try
        {
            var dataDirectory = DevelopmentDataDirectoryPolicy.ResolveFromEnvironment();
            if (dataDirectory is null)
            {
                return;
            }

            Directory.CreateDirectory(dataDirectory);
            dataDirectory = DevelopmentDataDirectoryPolicy.Validate(dataDirectory);
            var path = Path.Combine(dataDirectory, $".gptino-trace-{Environment.ProcessId}.jsonl");
            if (ExhaustedDirectories.ContainsKey(path))
            {
                return;
            }

            var record = new DevelopmentDiagnosticRecord(
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                Limit(component),
                Limit(eventName),
                Limit(detail));
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;

            // Serialized in-process. The file is per-process, so there is no cross-process race
            // left to lose — and a retry loop still dropped a fifth of 320 concurrent records in
            // test, which is the exact failure this rewrite exists to end. Writes are a single
            // short line and rare in production, so holding a lock costs nothing worth measuring.
            lock (WriteGate)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                if (stream.Length > MaximumTraceBytes)
                {
                    ExhaustedDirectories.TryAdd(path, 0);
                    return;
                }
                // BOM-less: StreamWriter emits a preamble on an empty file, which put a BOM in
                // front of the first record and broke every JSONL reader on the first line.
                using var writer = new StreamWriter(stream, BomlessUtf8);
                writer.Write(line);
            }
        }
        catch
        {
            // Diagnostics must never alter Rhino, Grasshopper, or AgentHost behavior.
        }
    }

    public static void TryWriteStandardError(string component, string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return;
        }

        TryWriteFingerprint(
            component,
            "agent-stderr",
            ClassifyStandardError(standardError),
            standardError);
    }

    public static void TryWriteException(
        string component,
        string eventName,
        Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        TryWrite(
            component,
            eventName,
            $"classification={ClassifyException(exception)};" +
            $"exceptionType={exception.GetType().FullName ?? exception.GetType().Name}");
    }

    private static void TryWriteFingerprint(
        string component,
        string eventName,
        string classification,
        string sensitiveText)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(sensitiveText);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            TryWrite(
                component,
                eventName,
                $"classification={classification};utf8Length={bytes.Length};sha256={digest}");
        }
        catch
        {
            // Diagnostics must never alter Rhino, Grasshopper, or AgentHost behavior.
        }
    }

    private static string ClassifyStandardError(string standardError)
    {
        var trimmed = standardError.TrimStart();
        if (trimmed.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            return "unhandled-exception";
        }
        if (trimmed.StartsWith("crit:", StringComparison.OrdinalIgnoreCase))
        {
            return "critical";
        }
        if (trimmed.StartsWith("fail:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }
        if (trimmed.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        return "other";
    }

    private static string ClassifyException(Exception exception) => exception switch
    {
        JsonException => "invalid-json",
        UriFormatException => "invalid-uri",
        InvalidDataException => "invalid-data",
        UnauthorizedAccessException => "access-denied",
        OperationCanceledException => "canceled",
        IOException => "io",
        _ => "unexpected",
    };

    private static string? Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 1_024 ? singleLine : singleLine[..1_024];
    }

    private sealed record DevelopmentDiagnosticRecord(
        DateTimeOffset TimestampUtc,
        int ProcessId,
        string? Component,
        string? Event,
        string? Detail);
}
