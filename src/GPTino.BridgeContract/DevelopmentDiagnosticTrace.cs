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
    private const int MaximumRecordCount = 256;
    private static readonly ConcurrentDictionary<string, byte> ExhaustedDirectories =
        new(StringComparer.OrdinalIgnoreCase);

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
            if (ExhaustedDirectories.ContainsKey(dataDirectory))
            {
                return;
            }

            using var mutex = new Mutex(false, CreateMutexName(dataDirectory));
            var lockTaken = false;
            try
            {
                try
                {
                    lockTaken = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
                if (!lockTaken)
                {
                    return;
                }

                if (Directory
                    .EnumerateFiles(
                        dataDirectory,
                        ".gptino-diagnostic-*.json",
                        SearchOption.TopDirectoryOnly)
                    .Take(MaximumRecordCount)
                    .Count() >= MaximumRecordCount)
                {
                    ExhaustedDirectories.TryAdd(dataDirectory, 0);
                    return;
                }

                var timestamp = DateTimeOffset.UtcNow;
                var record = new DevelopmentDiagnosticRecord(
                    timestamp,
                    Environment.ProcessId,
                    Limit(component),
                    Limit(eventName),
                    Limit(detail));
                for (var slot = 0; slot < MaximumRecordCount; slot++)
                {
                    var path = Path.Combine(
                        dataDirectory,
                        $".gptino-diagnostic-{slot:D3}.json");
                    try
                    {
                        using var stream = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None);
                        JsonSerializer.Serialize(stream, record);
                        return;
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // This canonical slot is already part of the global count.
                    }
                }

                ExhaustedDirectories.TryAdd(dataDirectory, 0);
            }
            finally
            {
                if (lockTaken)
                {
                    mutex.ReleaseMutex();
                }
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

    private static string CreateMutexName(string dataDirectory)
    {
        var canonicalPath = Path.GetFullPath(dataDirectory).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        return $"Local\\GPTino-Diagnostics-{digest[..32]}";
    }

    private sealed record DevelopmentDiagnosticRecord(
        DateTimeOffset TimestampUtc,
        int ProcessId,
        string? Component,
        string? Event,
        string? Detail);
}
