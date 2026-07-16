using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GPTino.History;

public sealed record ProjectHomePaths(
    string Root,
    string Workspace,
    string History,
    string Exports,
    string ManifestPath,
    string RuntimeRoot);

public static partial class ProjectHomeLayout
{
    public static ProjectHomePaths Resolve(
        string projectsRoot,
        string runtimeProjectsRoot,
        Guid projectId,
        string grasshopperPath,
        DateOnly createdOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeProjectsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(grasshopperPath);

        var stem = Path.GetFileNameWithoutExtension(grasshopperPath);
        var safeStem = SlugPart(stem);
        var shortId = projectId.ToString("N", null)[..8];
        var folderName = $"{createdOn:yyMMdd}-{safeStem}-{shortId}";
        var root = Path.GetFullPath(Path.Combine(projectsRoot, folderName));
        var runtimeRoot = Path.GetFullPath(Path.Combine(runtimeProjectsRoot, projectId.ToString("N", null)));

        EnsureChild(projectsRoot, root);
        EnsureChild(runtimeProjectsRoot, runtimeRoot);

        return new ProjectHomePaths(
            root,
            Path.Combine(root, "workspace"),
            Path.Combine(root, "history"),
            Path.Combine(root, "exports"),
            Path.Combine(root, "project.json"),
            runtimeRoot);
    }

    public static string StablePairFingerprint(string rhinoPath, string grasshopperPath)
    {
        var normalized = $"{Normalize(rhinoPath)}\n{Normalize(grasshopperPath)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string SlugPart(string value)
    {
        var cleaned = UnsafeNameCharacters().Replace(value.Trim(), "-");
        cleaned = RepeatedDashes().Replace(cleaned, "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "definition" : cleaned[..Math.Min(48, cleaned.Length)];
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static void EnsureChild(string parent, string candidate)
    {
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new HistoryPathException($"Path escapes configured root: {candidate}");
        }
    }

    [GeneratedRegex("[^\\p{L}\\p{Nd}._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeNameCharacters();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedDashes();
}
