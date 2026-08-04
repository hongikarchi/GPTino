using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DevelopmentDiagnosticTraceCollection
{
    public const string Name = "Development diagnostic environment";
}

[Collection(DevelopmentDiagnosticTraceCollection.Name)]
public sealed class DevelopmentDiagnosticTraceTests
{
    [Fact]
    public void StandardErrorRecordContainsOnlySafeFingerprintMetadata()
    {
        WithDiagnosticEnvironment(dataDirectory =>
        {
            const string standardError = "fail: sensitive-token-value";

            DevelopmentDiagnosticTrace.TryWriteStandardError("Rhino", standardError);

            var json = ReadOnlyRecord(dataDirectory);
            using var document = JsonDocument.Parse(json);
            var detail = document.RootElement.GetProperty("Detail").GetString();
            var bytes = Encoding.UTF8.GetBytes(standardError);
            var expectedDigest = Convert.ToHexString(SHA256.HashData(bytes));

            Assert.DoesNotContain(standardError, json, StringComparison.Ordinal);
            Assert.Equal(
                $"classification=error;utf8Length={bytes.Length};sha256={expectedDigest}",
                detail);
        });
    }

    [Fact]
    public void ExceptionRecordDoesNotContainTheExceptionMessage()
    {
        WithDiagnosticEnvironment(dataDirectory =>
        {
            const string sensitiveMessage = "credential=do-not-persist";

            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "test-failure",
                new InvalidDataException(sensitiveMessage));

            var json = ReadOnlyRecord(dataDirectory);
            using var document = JsonDocument.Parse(json);
            var detail = document.RootElement.GetProperty("Detail").GetString();

            Assert.DoesNotContain(sensitiveMessage, json, StringComparison.Ordinal);
            Assert.Equal(
                "classification=invalid-data;exceptionType=System.IO.InvalidDataException",
                detail);
        });
    }

    [Fact]
    public void ConcurrentWritesAreNeverDropped()
    {
        // The regression this file exists for. Records used to be one file each behind a 250ms
        // cross-process mutex, and a caller that lost the race abandoned its record silently — so
        // the trace went quiet exactly during the busy moments worth tracing. A live investigation
        // read that silence as evidence and drew the wrong conclusion from it twice.
        WithDiagnosticEnvironment(dataDirectory =>
        {
            const int Writes = 320;
            Parallel.For(
                0,
                Writes,
                index => DevelopmentDiagnosticTrace.TryWrite("test", "bounded", $"index={index}"));

            var lines = ReadRecords(dataDirectory);
            Assert.Equal(Writes, lines.Count);
            // Every record is a complete JSON object: interleaved appends must not tear a line.
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("bounded", document.RootElement.GetProperty("Event").GetString());
            }
        });
    }

    [Fact]
    public void WritingStopsOnceTheTraceFileReachesItsCap()
    {
        // Unbounded appends would let a runaway loop fill the user's disk.
        WithDiagnosticEnvironment(dataDirectory =>
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, $".gptino-trace-{Environment.ProcessId}.jsonl");
            File.WriteAllBytes(path, new byte[(4 * 1024 * 1024) + 1]);
            var sizeBefore = new FileInfo(path).Length;

            DevelopmentDiagnosticTrace.TryWrite("test", "over-cap", "detail");

            Assert.Equal(sizeBefore, new FileInfo(path).Length);
        });
    }

    private static string ReadOnlyRecord(string dataDirectory) =>
        Assert.Single(ReadRecords(dataDirectory));

    private static IReadOnlyList<string> ReadRecords(string dataDirectory)
    {
        var path = Assert.Single(Directory.EnumerateFiles(
            dataDirectory,
            ".gptino-trace-*.jsonl",
            SearchOption.TopDirectoryOnly));
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static void WithDiagnosticEnvironment(Action<string> assertion)
    {
        var previousMode = Environment.GetEnvironmentVariable(
            DevelopmentDataDirectoryPolicy.ModeEnvironmentVariable);
        var previousDataDirectory = Environment.GetEnvironmentVariable(
            DevelopmentDataDirectoryPolicy.DataDirectoryEnvironmentVariable);
        var runRoot = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "dev-loop",
            "diagnostic-test-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(runRoot, "runtime", "diagnostics");
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(
            Path.Combine(runRoot, DevelopmentDataDirectoryPolicy.OwnedRunMarker),
            "test");

        try
        {
            Environment.SetEnvironmentVariable(
                DevelopmentDataDirectoryPolicy.ModeEnvironmentVariable,
                "1");
            Environment.SetEnvironmentVariable(
                DevelopmentDataDirectoryPolicy.DataDirectoryEnvironmentVariable,
                dataDirectory);
            assertion(dataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DevelopmentDataDirectoryPolicy.ModeEnvironmentVariable,
                previousMode);
            Environment.SetEnvironmentVariable(
                DevelopmentDataDirectoryPolicy.DataDirectoryEnvironmentVariable,
                previousDataDirectory);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(start));
                 current is not null;
                 current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "GPTino.sln")))
                {
                    return current.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate GPTino.sln.");
    }
}
