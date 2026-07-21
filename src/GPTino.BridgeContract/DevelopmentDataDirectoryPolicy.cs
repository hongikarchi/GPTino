namespace GPTino.BridgeContract;

public static class DevelopmentDataDirectoryPolicy
{
    public const string ModeEnvironmentVariable = "GPTINO_DEV_MODE";
    public const string DataDirectoryEnvironmentVariable = "GPTINO_DEV_DATA_DIRECTORY";
    public const string OwnedRunMarker = ".gptino-owned-run";

    public static string? ResolveFromEnvironment()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ModeEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var configured = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{DataDirectoryEnvironmentVariable} is required when {ModeEnvironmentVariable}=1.");
        }
        return Validate(configured);
    }

    public static string Validate(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var fullPath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(root) ||
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, profile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The GPTino development data directory is too broad.");
        }

        DirectoryInfo? runRoot = null;
        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, OwnedRunMarker)))
            {
                runRoot = current;
                break;
            }
        }
        if (runRoot is null)
        {
            throw new InvalidOperationException("The GPTino development run marker was not found.");
        }

        var devLoop = runRoot.Parent;
        var artifacts = devLoop?.Parent;
        var repository = artifacts?.Parent;
        if (devLoop is null || artifacts is null || repository is null ||
            !string.Equals(devLoop.Name, "dev-loop", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifacts.Name, "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(repository.FullName, "GPTino.sln")))
        {
            throw new InvalidOperationException(
                "The GPTino development run is not inside a GPTino repository artifact root.");
        }

        var runPrefix = runRoot.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The GPTino development data directory must be below the marked run directory.");
        }

        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The GPTino development data directory contains a reparse point.");
            }
            if (string.Equals(current.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        throw new InvalidOperationException(
            "The GPTino development data directory escaped its repository.");
    }
}
