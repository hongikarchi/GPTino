using GPTino.AgentHost.Security;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Read-only library of shipped reference data files (structural section/material
/// catalogs and similar tables) beside the AgentHost executable. Unlike SkillLibrary,
/// data files are NOT indexed into thread instructions — discovery is the data_read
/// tool description plus the not-found error's file listing. Agents read a file, pick
/// the rows they need, and inject them into script payloads; Rhino-side scripts never
/// depend on these paths. Subdirectories are allowed ("structural/sections.json");
/// traversal is still refused by ConstrainedPath. Distinct from the runtime data
/// directory (runtime.db, artifacts) — this is immutable shipped content under
/// BaseDirectory/data.
/// </summary>
public sealed class DataLibrary
{
    private const int MaximumDataBytes = 1024 * 1024;

    public DataLibrary(string? rootDirectory = null)
    {
        Root = Path.GetFullPath(rootDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"));
    }

    public string Root { get; }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public object Read(string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Replace('\\', '/');
        var path = ConstrainedPath.Resolve(Root, normalized, "Data file");
        if (!File.Exists(path))
        {
            var available = string.Join(", ", List());
            throw new FileNotFoundException(
                $"Data file '{name}' was not found. Available data files: {available}");
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumDataBytes)
        {
            throw new InvalidOperationException($"Data file '{name}' exceeds the {MaximumDataBytes / 1024} KiB limit.");
        }
        return new
        {
            name = normalized,
            content = File.ReadAllText(path),
            bytes = info.Length
        };
    }
}
