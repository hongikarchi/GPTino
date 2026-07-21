using GPTino.AgentHost.Security;

namespace GPTino.AgentHost.Hosting;

public sealed record SkillSummary(string Name, string Summary);

/// <summary>
/// Read-only library of vetted skill files shipped beside the AgentHost executable.
/// Skills are deterministic plumbing (bake pipelines, reference notes) that agent
/// sessions fetch on demand via the skill_read tool — only a one-line index rides in
/// the thread instructions, so skill bodies never pollute unrelated turns.
/// </summary>
public sealed class SkillLibrary
{
    private const int MaximumSkillBytes = 256 * 1024;

    public SkillLibrary(string? rootDirectory = null)
    {
        Root = Path.GetFullPath(rootDirectory ?? Path.Combine(AppContext.BaseDirectory, "skills"));
    }

    public string Root { get; }

    public IReadOnlyList<SkillSummary> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        return Directory.EnumerateFiles(Root)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => new SkillSummary(Path.GetFileName(path), ReadSummary(path)))
            .ToArray();
    }

    public string IndexText()
    {
        var skills = List();
        return skills.Count == 0
            ? string.Empty
            : string.Join('\n', skills.Select(skill => $"- {skill.Name} — {skill.Summary}"));
    }

    public object Read(string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Skill names never contain path separators.");
        }
        var path = ConstrainedPath.Resolve(Root, name, "Skill");
        if (!File.Exists(path))
        {
            var available = string.Join(", ", List().Select(skill => skill.Name));
            throw new FileNotFoundException(
                $"Skill '{name}' was not found. Available skills: {available}");
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumSkillBytes)
        {
            throw new InvalidOperationException($"Skill '{name}' exceeds the {MaximumSkillBytes / 1024} KiB limit.");
        }
        return new
        {
            name = Path.GetFileName(path),
            content = File.ReadAllText(path),
            bytes = info.Length
        };
    }

    private static string ReadSummary(string path)
    {
        foreach (var line in File.ReadLines(path).Take(5))
        {
            var trimmed = line.Trim().TrimStart('#', '"', '\'', '-', '!').Trim();
            if (trimmed.Length > 0 &&
                !trimmed.StartsWith("python", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Length > 140 ? trimmed[..140] : trimmed;
            }
        }
        return "No summary.";
    }
}
