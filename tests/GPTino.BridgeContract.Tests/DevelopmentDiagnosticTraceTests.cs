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

            var recordPath = Assert.Single(Directory.EnumerateFiles(
                dataDirectory,
                ".gptino-diagnostic-*.json",
                SearchOption.TopDirectoryOnly));
            var json = File.ReadAllText(recordPath);
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

            var recordPath = Assert.Single(Directory.EnumerateFiles(
                dataDirectory,
                ".gptino-diagnostic-*.json",
                SearchOption.TopDirectoryOnly));
            var json = File.ReadAllText(recordPath);
            using var document = JsonDocument.Parse(json);
            var detail = document.RootElement.GetProperty("Detail").GetString();

            Assert.DoesNotContain(sensitiveMessage, json, StringComparison.Ordinal);
            Assert.Equal(
                "classification=invalid-data;exceptionType=System.IO.InvalidDataException",
                detail);
        });
    }

    [Fact]
    public void ConcurrentRecordCountNeverExceedsCap()
    {
        WithDiagnosticEnvironment(dataDirectory =>
        {
            Parallel.For(
                0,
                320,
                index => DevelopmentDiagnosticTrace.TryWrite(
                    "test",
                    "bounded",
                    $"index={index}"));

            var recordCount = Directory.EnumerateFiles(
                    dataDirectory,
                    ".gptino-diagnostic-*.json",
                    SearchOption.TopDirectoryOnly)
                .Count();

            // Diagnostics intentionally abandon a write when the bounded mutex wait
            // expires, so concurrent callers need not fill every slot. They must never
            // exceed the global cap.
            Assert.InRange(recordCount, 1, 256);
        });
    }

    [Fact]
    public void LegacyDiagnosticRecordsConsumeTheSameGlobalCap()
    {
        WithDiagnosticEnvironment(dataDirectory =>
        {
            Directory.CreateDirectory(dataDirectory);
            for (var index = 0; index < 3; index++)
            {
                File.WriteAllText(
                    Path.Combine(
                        dataDirectory,
                        $".gptino-diagnostic-legacy-{index}.json"),
                    "{}");
            }
            var preexistingHighSlot = Path.Combine(
                dataDirectory,
                ".gptino-diagnostic-255.json");
            File.WriteAllText(preexistingHighSlot, "{}");

            for (var index = 0; index < 260; index++)
            {
                DevelopmentDiagnosticTrace.TryWrite("test", "bounded", $"index={index}");
            }

            Assert.Equal(
                256,
                Directory.EnumerateFiles(
                    dataDirectory,
                    ".gptino-diagnostic-*.json",
                    SearchOption.TopDirectoryOnly)
                    .Count());
            Assert.True(File.Exists(preexistingHighSlot));
        });
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
