using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class CodexExecutableResolverTests
{
    [Fact]
    public void ExplicitSettingIsAuthoritativeAndIgnoresEnvironmentAndDiscovery()
    {
        using var directory = new TestDirectory();
        var explicitExe = WriteExe(directory, "explicit");
        var envExe = WriteExe(directory, "env");
        WithEnv("CODEX_EXECUTABLE", envExe, () =>
        {
            var candidates = CodexExecutableResolver.EnumerateCandidates(
                new AgentHostOptions { CodexExecutable = explicitExe });

            // The explicit app setting wins outright — env and any machine install are not consulted.
            var only = Assert.Single(candidates);
            Assert.Equal(explicitExe, only.Path);
            Assert.Equal("app setting", only.Source);
        });
    }

    [Fact]
    public void EnvironmentUsedOnlyWhenNoExplicitSetting()
    {
        using var directory = new TestDirectory();
        var envExe = WriteExe(directory, "env");
        WithEnv("CODEX_EXECUTABLE", envExe, () =>
        {
            var candidates = CodexExecutableResolver.EnumerateCandidates(new AgentHostOptions());

            var only = Assert.Single(candidates);
            Assert.Equal(envExe, only.Path);
            Assert.Equal("CODEX_EXECUTABLE", only.Source);
        });
    }

    [Fact]
    public void ConfiguredPathThatIsNotCodexExeYieldsNoCandidate()
    {
        using var directory = new TestDirectory();
        var notCodex = Path.Combine(directory.Path, "notes.txt");
        File.WriteAllText(notCodex, "not an executable");

        var candidates = CodexExecutableResolver.EnumerateCandidates(
            new AgentHostOptions { CodexExecutable = notCodex });

        Assert.Empty(candidates);
    }

    [Fact]
    public void MissingConfiguredPathYieldsNoCandidate()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Path, "codex.exe"); // never created

        var candidates = CodexExecutableResolver.EnumerateCandidates(
            new AgentHostOptions { CodexExecutable = missing });

        Assert.Empty(candidates);
    }

    private static string WriteExe(TestDirectory directory, string folder)
    {
        var dir = Path.Combine(directory.Path, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "codex.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void WithEnv(string name, string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
