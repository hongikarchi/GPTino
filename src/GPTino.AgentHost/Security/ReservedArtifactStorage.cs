namespace GPTino.AgentHost.Security;

internal static class ReservedArtifactStorage
{
    public const string Namespace = ".gptino-reserved";

    public static string JobRelativePath(Guid jobId, int operationIndex) =>
        Path.Combine(
            Namespace,
            "jobs",
            jobId.ToString("N"),
            "operations",
            $"{operationIndex:D4}.json");

    public static string JobRoot(string sessionRoot, Guid jobId) =>
        ConstrainedPath.Resolve(
            sessionRoot,
            Path.Combine(Namespace, "jobs", jobId.ToString("N")),
            "Reserved artifact");

    public static bool IsReservedPath(string sessionRoot, string resolvedPath)
    {
        var reservedRoot = Path.GetFullPath(Path.Combine(sessionRoot, Namespace))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(resolvedPath);
        if (fullPath.StartsWith(reservedRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullPath,
                reservedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(sessionRoot), fullPath);
        var firstSegment = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var windowsNormalized = firstSegment?.TrimEnd(' ', '.');
        return string.Equals(windowsNormalized, Namespace, StringComparison.OrdinalIgnoreCase) ||
            windowsNormalized?.StartsWith(Namespace + ":", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static void RejectUserPath(string sessionRoot, string resolvedPath)
    {
        if (IsReservedPath(sessionRoot, resolvedPath))
        {
            throw new InvalidOperationException(
                $"'{Namespace}' is reserved for immutable broker-owned payloads.");
        }
    }
}
