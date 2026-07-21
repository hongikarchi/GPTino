namespace GPTino.AgentHost.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "test-temp",
            "agenthost",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public void Dispose()
    {
        // Local verification artifacts are evidence and are intentionally preserved. Do not call
        // the process-wide SQLite pool-clearing API here: xUnit disposes test directories
        // concurrently, and global cleanup can invalidate another test's connection while it opens.
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(System.IO.Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(System.IO.Path.Combine(current.FullName, "GPTino.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the GPTino repository root for test artifacts.");
    }
}
