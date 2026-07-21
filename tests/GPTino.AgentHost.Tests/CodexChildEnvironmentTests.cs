using System.Diagnostics;
using System.Reflection;
using GPTino.AgentHost.Codex;

namespace GPTino.AgentHost.Tests;

public sealed class CodexChildEnvironmentTests
{
    [Fact]
    public void CodexChildDoesNotInheritAnyGptinoEnvironmentVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["GPTINO_API_TOKEN"] = "api-secret";
        startInfo.Environment["GPTINO_BRIDGE_SECRET"] = "bridge-secret";
        startInfo.Environment["GPTINO_BRIDGE_PIPE"] = "bridge-pipe";
        startInfo.Environment["gptino_future_secret"] = "future-secret";
        startInfo.Environment["GPTino:ApiToken"] = "colon-token";
        startInfo.Environment["GPTino:FutureSecret"] = "colon-secret";
        startInfo.Environment["CODEX_HOME"] = "keep-me";
        var scrub = typeof(CodexAppServerClient).GetMethod(
            "RemoveGptinoEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(scrub);
        scrub.Invoke(null, [startInfo]);

        Assert.DoesNotContain(
            startInfo.Environment.Keys,
            key =>
                key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GPTINO:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("keep-me", startInfo.Environment["CODEX_HOME"]);
    }
}
