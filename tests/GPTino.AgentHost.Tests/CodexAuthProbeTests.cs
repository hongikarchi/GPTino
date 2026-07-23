using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class CodexAuthProbeTests
{
    [Fact]
    public void ReadsLoggedInWhenAuthFileIsPresent()
    {
        using var home = new TestDirectory();
        File.WriteAllText(Path.Combine(home.Path, "auth.json"), "{\"tokens\":{\"access\":\"abc\"}}");
        WithCodexHome(home.Path, () =>
        {
            var probe = new CodexAuthProbe(new AgentHostOptions());
            var snapshot = probe.Read();
            Assert.Equal(CodexAuthStatus.LoggedIn, snapshot.Status);
            Assert.Equal("logged-in", snapshot.Wire);
        });
    }

    [Fact]
    public void ReadsLoggedOutWhenCredentialsMissingButCliResolvable()
    {
        using var home = new TestDirectory(); // deliberately no auth.json
        using var install = new TestDirectory();
        var fakeCodex = Path.Combine(install.Path, "codex.exe");
        File.WriteAllText(fakeCodex, "binary");
        WithCodexHome(home.Path, () =>
        {
            var probe = new CodexAuthProbe(new AgentHostOptions { CodexExecutable = fakeCodex });
            var snapshot = probe.Read();
            Assert.Equal(CodexAuthStatus.LoggedOut, snapshot.Status);
            Assert.Equal("logged-out", snapshot.Wire);
        });
    }

    [Fact]
    public void HasStoredCredentialsIsFalseForMissingOrEmptyAuthFile()
    {
        using var home = new TestDirectory();
        WithCodexHome(home.Path, () =>
        {
            Assert.False(CodexInstallation.HasStoredCredentials());
            File.WriteAllText(Path.Combine(home.Path, "auth.json"), string.Empty);
            Assert.False(CodexInstallation.HasStoredCredentials());
            Assert.Equal(
                Path.GetFullPath(Path.Combine(home.Path, "auth.json")),
                CodexInstallation.AuthFilePath());
        });
    }

    [Fact]
    public void HasStoredCredentialsIsTrueForNonEmptyAuthFile()
    {
        using var home = new TestDirectory();
        File.WriteAllText(Path.Combine(home.Path, "auth.json"), "{}");
        WithCodexHome(home.Path, () => Assert.True(CodexInstallation.HasStoredCredentials()));
    }

    private static void WithCodexHome(string path, Action body)
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", path);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }
}
