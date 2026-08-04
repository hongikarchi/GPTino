using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class DataLibraryTests
{
    [Fact]
    public void ReadsSubdirectoryFilesAndNormalizesSeparators()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(Path.Combine(root, "structural"));
        File.WriteAllText(Path.Combine(root, "structural", "sections.json"), "{\"sections\":[]}");
        var library = new DataLibrary(root);

        foreach (var name in new[] { "structural/sections.json", "structural\\sections.json" })
        {
            var result = library.Read(name);
            var content = result.GetType().GetProperty("content")?.GetValue(result) as string;
            Assert.Contains("sections", content, StringComparison.Ordinal);
        }
        Assert.Equal(["structural/sections.json"], library.List());
    }

    [Fact]
    public void RejectsTraversalAndListsAvailableFilesOnMiss()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(Path.Combine(root, "structural"));
        File.WriteAllText(Path.Combine(root, "structural", "materials.json"), "{}");
        var library = new DataLibrary(root);

        Assert.ThrowsAny<Exception>(() => library.Read("../outside.json"));
        var missing = Assert.Throws<FileNotFoundException>(() => library.Read("structural/nope.json"));
        Assert.Contains("structural/materials.json", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRootYieldsEmptyLibraryWithoutThrowing()
    {
        using var directory = new TestDirectory();
        var library = new DataLibrary(Path.Combine(directory.Path, "does-not-exist"));

        Assert.Empty(library.List());
    }

    [Fact]
    public void ShippedStructuralCatalogsAreDiscoverableFromTheApplicationBase()
    {
        var library = new DataLibrary();
        if (!Directory.Exists(library.Root))
        {
            return;
        }
        var names = library.List();
        Assert.Contains("structural/sections.json", names);
        Assert.Contains("structural/materials.json", names);
    }
}
