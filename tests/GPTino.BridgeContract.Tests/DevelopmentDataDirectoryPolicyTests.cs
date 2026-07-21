using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class DevelopmentDataDirectoryPolicyTests
{
    [Fact]
    public void MarkedRunAllowsOnlyItsDescendants()
    {
        var runRoot = CreateRunRoot();
        var dataDirectory = Path.Combine(runRoot, "runtime", "agent");
        Directory.CreateDirectory(dataDirectory);

        var result = DevelopmentDataDirectoryPolicy.Validate(dataDirectory);

        Assert.Equal(Path.GetFullPath(dataDirectory), result);
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentDataDirectoryPolicy.Validate(runRoot));
    }

    [Fact]
    public void MarkerOutsideRepositoryDevLoopLayoutIsRejected()
    {
        var repository = FindRepositoryRoot();
        var invalidRoot = Path.Combine(
            repository,
            "artifacts",
            "invalid-dev-layout",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(invalidRoot, "runtime"));
        File.WriteAllText(
            Path.Combine(invalidRoot, DevelopmentDataDirectoryPolicy.OwnedRunMarker),
            "test");

        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentDataDirectoryPolicy.Validate(Path.Combine(invalidRoot, "runtime")));
    }

    [Fact]
    public void MissingMarkerIsRejected()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "dev-loop",
            Guid.NewGuid().ToString("N"),
            "runtime");
        Directory.CreateDirectory(path);

        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentDataDirectoryPolicy.Validate(path));
    }

    private static string CreateRunRoot()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "dev-loop",
            "policy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, DevelopmentDataDirectoryPolicy.OwnedRunMarker),
            "test");
        return root;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(start)); current is not null; current = current.Parent)
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
