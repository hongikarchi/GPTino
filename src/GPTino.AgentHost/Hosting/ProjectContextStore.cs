using System.Text;
using System.Text.Json;

namespace GPTino.AgentHost.Hosting;

/// <summary>Folds durable project context into every Codex thread's base instructions.</summary>
public interface IThreadInstructionComposer
{
    string Compose(string baseInstructions);
}

/// <summary>
/// Durable per-project context folder under the runtime data directory (never the user's
/// project folder). Holds human-editable working rules and an append-only memory ledger.
/// Scaffolding only creates missing files, so user edits always survive; composition
/// re-reads the files on every thread start/resume, so edits apply to the next turn
/// without a restart. Context problems must never block a thread from starting.
/// </summary>
public sealed class ProjectContextStore : IThreadInstructionComposer
{
    private const int MaximumContextFileCharacters = 16 * 1024;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private const string RulesSeed = """
        # GPTino working rules (사용자 편집용)

        Rules in this file are appended to every GPTino agent session's instructions
        for this project. Edit freely — changes apply from the next message you send.

        ## Conventions (examples — replace with yours)
        - Units and tolerance: follow the Rhino document settings; never change them.
        - Layer naming: keep generated objects on GPTino-managed layers.
        - Grasshopper: prefer small, labeled clusters of components over sprawl.
        """;

    private const string MemorySeed = """
        # GPTino project memory (append-only)

        Lessons this project's agent sessions should start warm with.
        Append one entry per non-obvious fix: symptom → cause → fix.

        <!-- example:
        ## Panel boundary rebuilds drift
        Symptom: rebuilt panel boundaries shift by ~2mm.
        Cause: the facade grid is anchored to a moved block instance.
        Fix: always re-read the anchor point from object 7F2A before regenerating.
        -->
        """;

    public ProjectContextStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ContextDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "context");
    }

    public string ContextDirectory { get; }

    public string RulesPath => Path.Combine(ContextDirectory, "rules.md");

    public string MemoryPath => Path.Combine(ContextDirectory, "MEMORY.md");

    public void EnsureScaffolded(
        Guid projectId,
        string projectName,
        string? rhinoPath,
        string? grasshopperPath)
    {
        Directory.CreateDirectory(ContextDirectory);
        WriteIfAbsent(
            Path.Combine(ContextDirectory, "project.json"),
            JsonSerializer.Serialize(
                new
                {
                    schema = "gptino-context-v1",
                    projectId,
                    projectName,
                    rhinoFile = rhinoPath,
                    grasshopperFile = grasshopperPath,
                    createdAt = DateTimeOffset.UtcNow
                },
                ManifestJsonOptions));
        WriteIfAbsent(RulesPath, RulesSeed);
        WriteIfAbsent(MemoryPath, MemorySeed);
    }

    public string Compose(string baseInstructions)
    {
        ArgumentNullException.ThrowIfNull(baseInstructions);
        try
        {
            var builder = new StringBuilder(baseInstructions);
            AppendSection(builder, "Project rules", RulesPath);
            AppendSection(builder, "Project memory", MemoryPath);
            return builder.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return baseInstructions;
        }
    }

    private static void AppendSection(StringBuilder builder, string title, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var content = File.ReadAllText(path).Trim();
        if (content.Length == 0)
        {
            return;
        }
        var truncated = content.Length > MaximumContextFileCharacters;
        if (truncated)
        {
            content = content[..MaximumContextFileCharacters];
        }
        builder.Append("\n\n## ").Append(title).Append(" (").Append(Path.GetFileName(path)).Append(")\n");
        builder.Append(content);
        if (truncated)
        {
            builder.Append("\n[Truncated: edit the file to stay under ")
                .Append(MaximumContextFileCharacters)
                .Append(" characters.]");
        }
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }
}
