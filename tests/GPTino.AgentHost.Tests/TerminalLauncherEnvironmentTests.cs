using System.Diagnostics;
using System.Reflection;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class TerminalLauncherEnvironmentTests
{
    [Fact]
    public void ConfigureChildEnvironmentRemovesInheritedGptinoValuesAndSetsOnlySessionToken()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["GPTINO_API_TOKEN"] = "stale-token";
        startInfo.Environment["GPTINO_BRIDGE_SECRET"] = "bridge-secret";
        startInfo.Environment["gptino_future_value"] = "future-value";
        startInfo.Environment["GPTino:ApiToken"] = "colon-token";
        startInfo.Environment["GPTino:FutureSecret"] = "colon-secret";
        startInfo.Environment["PATH"] = "preserved-path";
        startInfo.Environment["UNRELATED_VALUE"] = "preserved-value";
        var configure = typeof(TerminalLauncher).GetMethod(
            "ConfigureChildEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(configure);
        configure.Invoke(null, [startInfo, "session-token"]);

        var gptinoKeys = startInfo.Environment.Keys
            .Where(key =>
                key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GPTINO:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(["GPTINO_API_TOKEN"], gptinoKeys);
        Assert.Equal("session-token", startInfo.Environment["GPTINO_API_TOKEN"]);
        Assert.Equal("preserved-path", startInfo.Environment["PATH"]);
        Assert.Equal("preserved-value", startInfo.Environment["UNRELATED_VALUE"]);
    }
}
