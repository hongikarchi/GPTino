using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class ProjectContextStoreTests
{
    [Fact]
    public void ScaffoldCreatesSeedFilesOnceAndPreservesUserEdits()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);

        store.EnsureScaffolded(Guid.NewGuid(), "Tower A", "C:\\models\\Tower.3dm", "C:\\models\\Facade.gh");
        Assert.True(File.Exists(Path.Combine(store.ContextDirectory, "project.json")));
        Assert.True(File.Exists(store.RulesPath));
        Assert.True(File.Exists(store.MemoryPath));

        File.WriteAllText(store.RulesPath, "- Always model in millimeters.");
        store.EnsureScaffolded(Guid.NewGuid(), "Tower A", "C:\\models\\Tower.3dm", "C:\\models\\Facade.gh");
        Assert.Equal("- Always model in millimeters.", File.ReadAllText(store.RulesPath));
    }

    [Fact]
    public void ComposeAppendsRulesAndMemorySectionsToBaseInstructions()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);
        Directory.CreateDirectory(store.ContextDirectory);
        File.WriteAllText(store.RulesPath, "- 단위는 밀리미터.");
        File.WriteAllText(store.MemoryPath, "## Anchor drift\nRe-read object 7F2A first.");

        var composed = store.Compose("BASE INSTRUCTIONS");

        Assert.StartsWith("BASE INSTRUCTIONS", composed, StringComparison.Ordinal);
        Assert.Contains("## Project rules (rules.md)", composed, StringComparison.Ordinal);
        Assert.Contains("단위는 밀리미터", composed, StringComparison.Ordinal);
        Assert.Contains("## Project memory (MEMORY.md)", composed, StringComparison.Ordinal);
        Assert.Contains("7F2A", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeReturnsBaseUnchangedWhenContextIsMissingOrEmpty()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);

        Assert.Equal("BASE", store.Compose("BASE"));

        Directory.CreateDirectory(store.ContextDirectory);
        File.WriteAllText(store.RulesPath, "   \n  ");
        Assert.Equal("BASE", store.Compose("BASE"));
    }

    [Fact]
    public void ComposeTruncatesOversizedContextFiles()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);
        Directory.CreateDirectory(store.ContextDirectory);
        File.WriteAllText(store.RulesPath, new string('r', 40_000));

        var composed = store.Compose("BASE");

        Assert.Contains("[Truncated:", composed, StringComparison.Ordinal);
        Assert.True(composed.Length < 20_000);
    }

    [Fact]
    public void AppendMemoryCreatesFileAndAccumulatesEntries()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);

        Assert.True(store.AppendMemory("## First\nsymptom -> cause -> fix").Appended);
        Assert.True(store.AppendMemory("## Second\nanother lesson").Appended);

        var content = File.ReadAllText(store.MemoryPath);
        Assert.Contains("## First", content, StringComparison.Ordinal);
        Assert.Contains("## Second", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppendMemoryRejectsEmptyEntry(string? entry)
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);

        Assert.False(store.AppendMemory(entry).Appended);
    }

    [Fact]
    public void AppendMemoryRefusesToGrowPastTheContextCap()
    {
        using var directory = new TestDirectory();
        var store = new ProjectContextStore(directory.Path);
        Assert.True(store.AppendMemory(new string('x', 15 * 1024)).Appended);

        var overflow = store.AppendMemory(new string('y', 2 * 1024));

        Assert.False(overflow.Appended);
        Assert.Contains("cap", overflow.Message, StringComparison.OrdinalIgnoreCase);
    }
}
