using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class DocumentTargetTests
{
    [Fact]
    public void TargetKey_IsStableAcrossGenerationChanges()
    {
        var target = CreateTarget();
        var next = target.NextGeneration();

        Assert.Equal(target.StableTargetKey(), next.StableTargetKey());
        Assert.Equal(2, next.Generation);
    }

    [Fact]
    public void Guard_RejectsStaleGeneration()
    {
        var target = CreateTarget();

        var exception = Assert.Throws<DocumentTargetMismatchException>(
            () => DocumentTargetGuard.RequireCurrent(target.NextGeneration(), target));

        Assert.Equal(2, exception.Expected.Generation);
        Assert.Equal(1, exception.Actual.Generation);
    }

    [Fact]
    public void Create_NormalizesPaths()
    {
        var target = CreateTarget();

        Assert.True(Path.IsPathFullyQualified(target.RhinoPath));
        Assert.True(Path.IsPathFullyQualified(target.GrasshopperPath));
        Assert.DoesNotContain("..", target.RhinoPath, StringComparison.Ordinal);
    }

    internal static DocumentTarget CreateTarget() =>
        DocumentRuntimeTarget.Create(
            Guid.Parse("bd368228-75d8-43a9-a67e-f50946b0a029"),
            4321,
            new DateTimeOffset(2026, 7, 16, 1, 2, 3, TimeSpan.Zero),
            19,
            Guid.Parse("75cfe50c-7ca1-47c6-87ad-425c43522b55"),
            Path.Combine(Path.GetTempPath(), "model", "sample.3dm"),
            Path.Combine(Path.GetTempPath(), "model", "sample.gh"));
}
