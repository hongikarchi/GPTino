using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class SkillLibraryTests
{
    [Fact]
    public void ListsSkillsWithFirstLineSummariesAndBuildsIndex()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "skills");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bake_manager.py"), "#! python 3\n# GH->Rhino bake manager: layers and names.\ncode");
        File.WriteAllText(Path.Combine(root, "gh-authoring.md"), "# Grasshopper authoring reference\nbody");
        var library = new SkillLibrary(root);

        var skills = library.List();

        Assert.Equal(2, skills.Count);
        Assert.Equal("bake_manager.py", skills[0].Name);
        Assert.Contains("bake manager", skills[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bake_manager.py —", library.IndexText(), StringComparison.Ordinal);
        Assert.Contains("gh-authoring.md —", library.IndexText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReturnsContentAndRejectsTraversalAndUnknownNames()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "skills");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bake_manager.py"), "#! python 3\ncontent-body");
        var library = new SkillLibrary(root);

        var result = library.Read("bake_manager.py");
        var content = result.GetType().GetProperty("content")?.GetValue(result) as string;
        Assert.Contains("content-body", content, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => library.Read("..\\secrets.txt"));
        Assert.Throws<InvalidOperationException>(() => library.Read("sub/other.py"));
        var missing = Assert.Throws<FileNotFoundException>(() => library.Read("nope.py"));
        Assert.Contains("bake_manager.py", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRootYieldsEmptyLibraryWithoutThrowing()
    {
        using var directory = new TestDirectory();
        var library = new SkillLibrary(Path.Combine(directory.Path, "does-not-exist"));

        Assert.Empty(library.List());
        Assert.Equal(string.Empty, library.IndexText());
    }

    [Fact]
    public void ShippedSkillsAreDiscoverableFromTheApplicationBase()
    {
        var library = new SkillLibrary();
        if (!Directory.Exists(library.Root))
        {
            return;
        }
        var names = library.List().Select(skill => skill.Name).ToArray();
        Assert.Contains("bake_manager.py", names);
        Assert.Contains("gh-authoring.md", names);
    }
}

public sealed class InstructionAssemblerTests
{
    [Fact]
    public void ComposeLayersHouseRulesSkillsIndexAndProjectContext()
    {
        using var directory = new TestDirectory();
        var skillRoot = Path.Combine(directory.Path, "skills");
        Directory.CreateDirectory(skillRoot);
        File.WriteAllText(Path.Combine(skillRoot, "bake_manager.py"), "#! python 3\n# Bake manager skill.\npass");
        var contextStore = new ProjectContextStore(directory.Path);
        Directory.CreateDirectory(contextStore.ContextDirectory);
        File.WriteAllText(contextStore.RulesPath, "- 프로젝트 단위는 밀리미터.");
        var assembler = new InstructionAssembler(contextStore, new SkillLibrary(skillRoot));

        var composed = assembler.Compose("BASE");

        Assert.StartsWith("BASE", composed, StringComparison.Ordinal);
        var houseRules = composed.IndexOf("## GPTino house rules", StringComparison.Ordinal);
        var skillsIndex = composed.IndexOf("## Built-in skills", StringComparison.Ordinal);
        var projectRules = composed.IndexOf("## Project rules", StringComparison.Ordinal);
        Assert.True(houseRules > 0);
        Assert.True(skillsIndex > houseRules);
        Assert.True(projectRules > skillsIndex);
        Assert.Contains("bake_manager.py", composed, StringComparison.Ordinal);
        Assert.Contains("밀리미터", composed, StringComparison.Ordinal);
        Assert.Contains("719467e6-7cf5-4848-99b0-c5dd57e5442c", composed, StringComparison.Ordinal);
    }
}
