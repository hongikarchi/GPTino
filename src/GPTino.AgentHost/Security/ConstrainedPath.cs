namespace GPTino.AgentHost.Security;

internal static class ConstrainedPath
{
    public static string Resolve(string root, string? relativePath, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"{subject} path must be relative.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{subject} path escapes managed storage.");
        }

        RejectExistingReparsePoints(fullRoot, destination, subject);
        return destination;
    }

    public static void RejectExistingReparsePoints(string root, string destination, string subject)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullDestination = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(fullRoot, fullDestination);
        var current = fullRoot;
        RejectIfReparsePoint(current, subject);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectIfReparsePoint(current, subject);
        }
    }

    private static void RejectIfReparsePoint(string path, string subject)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{subject} path crosses a reparse point.");
        }
    }
}
