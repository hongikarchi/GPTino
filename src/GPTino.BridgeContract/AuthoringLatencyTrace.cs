using System.Text;
using System.Text.Json;

namespace GPTino.BridgeContract;

/// <summary>
/// High-volume, dev-only latency trace: one JSONL line per dynamic tool call (and per turn
/// boundary), appended to <c>authoring-latency.jsonl</c> in the DevLoop data directory. Unlike
/// <see cref="DevelopmentDiagnosticTrace"/> (256 one-file-per-record breadcrumbs) this is an
/// append-only stream sized for a whole authoring turn's hundreds of tool calls, so a benchmark can
/// decompose wall-clock into model-inference (inter-call gaps) vs GPTino tool-handling (durations,
/// by tool). Production launches never set GPTINO_DEV_MODE and therefore never write anything.
/// </summary>
public static class AuthoringLatencyTrace
{
    private const string FileName = "authoring-latency.jsonl";
    private static readonly object Gate = new();

    /// <summary>Records one completed dynamic tool call.</summary>
    public static void TryToolCall(string tool, long durationMs, bool ok, string threadId)
    {
        Append(new
        {
            t = DateTimeOffset.UtcNow,
            kind = "tool",
            tool,
            ms = durationMs,
            ok,
            thread = threadId
        });
    }

    /// <summary>Records a turn boundary (message accepted / turn returned to idle).</summary>
    public static void TryTurn(string phase, string threadId)
    {
        Append(new
        {
            t = DateTimeOffset.UtcNow,
            kind = "turn",
            phase,
            thread = threadId
        });
    }

    private static void Append(object record)
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
            var path = Path.Combine(dataDirectory, FileName);
            var line = JsonSerializer.Serialize(record) + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(line);
            lock (Gate)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Latency tracing must never alter Rhino, Grasshopper, or AgentHost behavior.
        }
    }
}
