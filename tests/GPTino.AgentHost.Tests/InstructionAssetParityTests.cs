using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The model-facing instruction text lives in markdown assets (the runtime source of truth) but is
/// also duplicated as a compiled C# fallback so a broken install still works. The two must never
/// drift — a one-sided edit is exactly the "prompt drift" that fed the model a stale contract. These
/// tests fail the build if an asset and its fallback diverge.
/// </summary>
public sealed class InstructionAssetParityTests
{
    [Fact]
    public void HouseRulesAssetMatchesCompiledFallback() =>
        AssertAssetMatchesFallback("house-rules.md", HouseRules.DefaultText);

    [Fact]
    public void PayloadGuideAssetMatchesCompiledFallback() =>
        AssertAssetMatchesFallback("payload-guide.md", DynamicToolSpecs.DefaultPayloadGuide);

    [Fact]
    public void CuratorInstructionsAssetMatchesCompiledFallback() =>
        AssertAssetMatchesFallback("curator.md", CuratorInstructions.DefaultText);

    private static void AssertAssetMatchesFallback(string assetFileName, string fallback)
    {
        var assetPath = Path.Combine(RepoRoot(), "assets", "instructions", assetFileName);
        Assert.True(File.Exists(assetPath), $"Instruction asset not found: {assetPath}");
        Assert.Equal(Normalize(File.ReadAllText(assetPath)), Normalize(fallback));
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).TrimEnd();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "assets", "instructions", "house-rules.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root (assets/instructions).");
    }
}
