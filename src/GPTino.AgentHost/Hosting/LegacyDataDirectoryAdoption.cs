using System.Text.Json;
using System.Text.RegularExpressions;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// One-time adoption of a legacy pair-fingerprint data root into the rhino-only data root.
/// Before the multi-Grasshopper change the data root fingerprint hashed the Rhino AND Grasshopper
/// paths; it now hashes the canonical Rhino path only, so an existing project would otherwise
/// restart with an empty root. When the new root has no runtime.db yet, the most recently active
/// legacy root whose context\project.json rhinoFile canonically equals this Rhino path is copied
/// in (runtime.db family, context\, attachments\, artifacts\ — never live-jobs.db, history\,
/// workspace\, or the instance lock). Adoption is all-or-nothing per candidate: everything is
/// staged first and the runtime.db family lands LAST, so a failed attempt leaves the new root
/// runtime.db-less and the next launch retries cleanly. Adoption only ever targets the default
/// fingerprint root — an explicit --data-directory (dev-mode sandboxes, benchmark runs) opts
/// out entirely. Every failure is non-fatal: the host continues with an empty root.
/// </summary>
public static partial class LegacyDataDirectoryAdoption
{
    private const string DatabaseFileName = "runtime.db";
    private const string LockFileName = ".gptino-instance.lock";
    private const string MarkerFileName = "adopted-from.txt";
    private const string StagingSuffix = ".adopting";

    private static readonly string[] DurableDirectoryNames = ["context", "attachments", "artifacts"];

    /// <summary>Runs after <see cref="RuntimeInstanceLock.Acquire"/> on the new root and before the
    /// SessionStore opens runtime.db. Returns true when a legacy root was adopted. Skipped entirely
    /// when the data directory was explicitly supplied: an explicit root never participates in the
    /// fingerprint scheme this migration exists for, and importing production data into a dev or
    /// benchmark sandbox would break the isolation those runs rely on.</summary>
    public static bool TryAdopt(AgentHostOptions options, ILogger? logger) =>
        TryAdopt(options, ProjectArchiveReader.DefaultProjectsParentDirectory(), logger);

    internal static bool TryAdopt(
        AgentHostOptions options,
        string projectsParentDirectory,
        ILogger? logger)
    {
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            logger?.LogDebug(
                "Skipping legacy data root adoption: the data directory was explicitly supplied.");
            return false;
        }
        return TryAdopt(options.ResolveDataDirectory(), options.RhinoPath, projectsParentDirectory, logger);
    }

    internal static bool TryAdopt(
        string newDataRoot,
        string? rhinoPath,
        string projectsParentDirectory,
        ILogger? logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newDataRoot) || string.IsNullOrWhiteSpace(projectsParentDirectory))
            {
                return false;
            }
            var canonicalRhino = AgentHostOptions.CanonicalDocumentIdentity(rhinoPath);
            if (canonicalRhino.Length == 0)
            {
                // An untitled document has no durable identity to match a legacy root against.
                return false;
            }
            var canonicalNewRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDataRoot));
            if (File.Exists(Path.Combine(canonicalNewRoot, DatabaseFileName)))
            {
                // The new root is already in use (or was already adopted); adoption is one-time.
                return false;
            }
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectsParentDirectory));
            if (!Directory.Exists(parent))
            {
                return false;
            }

            foreach (var candidate in FindMatchingLegacyRoots(canonicalNewRoot, canonicalRhino, parent, logger))
            {
                if (IsLiveLocked(candidate))
                {
                    logger?.LogInformation(
                        "Skipping legacy GPTino data root {Root}: another runtime holds its instance lock.",
                        candidate);
                    continue;
                }
                try
                {
                    CopyProjectData(candidate, canonicalNewRoot);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    logger?.LogWarning(
                        exception,
                        "Could not adopt legacy GPTino data root {Root}; trying the next candidate.",
                        candidate);
                    continue;
                }
                WriteMarkerSafely(canonicalNewRoot, candidate, logger);
                logger?.LogInformation(
                    "Adopted legacy GPTino data root {Source} into {Destination}.",
                    candidate,
                    canonicalNewRoot);
                return true;
            }
            return false;
        }
        catch (Exception exception)
        {
            // Adoption is best-effort by design: a fresh, empty data root is always an acceptable
            // outcome, so no failure here may prevent the AgentHost from starting.
            logger?.LogWarning(exception, "Legacy GPTino data root adoption failed; starting with an empty root.");
            return false;
        }
    }

    /// <summary>Matching legacy roots ordered most recently active first (runtime.db write time).</summary>
    private static IReadOnlyList<string> FindMatchingLegacyRoots(
        string canonicalNewRoot,
        string canonicalRhino,
        string projectsParentDirectory,
        ILogger? logger)
    {
        List<(string Root, DateTime LastWriteUtc)> matches = [];
        foreach (var directory in Directory.EnumerateDirectories(projectsParentDirectory))
        {
            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (!FingerprintPattern().IsMatch(Path.GetFileName(root)) ||
                    string.Equals(root, canonicalNewRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var databasePath = Path.Combine(root, DatabaseFileName);
                if (!File.Exists(databasePath) ||
                    TryReadManifestRhinoFile(root) is not { } rhinoFile ||
                    !string.Equals(
                        AgentHostOptions.CanonicalDocumentIdentity(rhinoFile),
                        canonicalRhino,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                matches.Add((root, File.GetLastWriteTimeUtc(databasePath)));
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A garbage manifest, an unreadable folder, or an invalid recorded path only
                // disqualifies that one candidate.
                logger?.LogDebug(
                    exception,
                    "Ignoring unreadable legacy GPTino data root candidate {Root}.",
                    directory);
            }
        }
        return matches
            .OrderByDescending(match => match.LastWriteUtc)
            .Select(match => match.Root)
            .ToArray();
    }

    private static string? TryReadManifestRhinoFile(string root)
    {
        var manifestPath = Path.Combine(root, "context", "project.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("rhinoFile", out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Cheap liveness probe: the owning AgentHost holds the lock file open with FileShare.None,
    /// so a share violation means the legacy root is still in use. A stale lock file left by a
    /// crashed process opens fine and does not block adoption.
    /// </summary>
    private static bool IsLiveLocked(string root)
    {
        var lockPath = Path.Combine(root, LockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void CopyProjectData(string sourceRoot, string destinationRoot)
    {
        // All-or-nothing per candidate: every payload is staged under a temp name first, staged
        // directories are renamed into place next, and the SQLite family's File.Move is the LAST,
        // committing step. Any failure deletes everything staged AND everything already committed
        // for this candidate, so the new root stays runtime.db-less — the one-time gate in
        // TryAdopt then naturally retries on the next launch, and a later candidate in the same
        // loop starts from a clean root instead of mixing two candidates' data.
        var stagedFiles = new List<(string Staged, string Final)>();
        var stagedDirectories = new List<(string Staged, string Final)>();
        var committedFiles = new List<string>();
        var committedDirectories = new List<string>();
        try
        {
            foreach (var name in new[] { DatabaseFileName, DatabaseFileName + "-wal", DatabaseFileName + "-shm" })
            {
                var source = Path.Combine(sourceRoot, name);
                if (!File.Exists(source))
                {
                    continue;
                }
                var stagedPath = Path.Combine(destinationRoot, name + StagingSuffix);
                // Registered BEFORE the copy so a copy that dies halfway is still cleaned up.
                stagedFiles.Add((stagedPath, Path.Combine(destinationRoot, name)));
                File.Copy(source, stagedPath, overwrite: true);
            }
            // Only these folders carry durable project state (context rules/memory, message
            // attachments, per-session artifact drafts). live-jobs.db, history\, workspace\ and
            // the instance lock are runtime-scoped and must never travel between roots.
            foreach (var directoryName in DurableDirectoryNames)
            {
                var source = Path.Combine(sourceRoot, directoryName);
                if (!Directory.Exists(source))
                {
                    continue;
                }
                var stagedPath = Path.Combine(destinationRoot, directoryName + StagingSuffix);
                DeleteDirectoryQuietly(stagedPath); // a stale staging dir from a crashed attempt
                // Registered BEFORE the copy so a copy that dies halfway is still cleaned up.
                stagedDirectories.Add((stagedPath, Path.Combine(destinationRoot, directoryName)));
                CopyDirectory(source, stagedPath);
            }

            foreach (var (stagedPath, finalPath) in stagedDirectories)
            {
                if (Directory.Exists(finalPath))
                {
                    Directory.Delete(finalPath, recursive: true);
                }
                Directory.Move(stagedPath, finalPath);
                committedDirectories.Add(finalPath);
            }
            foreach (var (stagedPath, finalPath) in stagedFiles)
            {
                File.Move(stagedPath, finalPath, overwrite: true);
                committedFiles.Add(finalPath);
            }
        }
        catch
        {
            foreach (var (stagedPath, _) in stagedFiles)
            {
                DeleteFileQuietly(stagedPath);
            }
            foreach (var (stagedPath, _) in stagedDirectories)
            {
                DeleteDirectoryQuietly(stagedPath);
            }
            foreach (var finalPath in committedFiles)
            {
                DeleteFileQuietly(finalPath);
            }
            foreach (var finalPath in committedDirectories)
            {
                DeleteDirectoryQuietly(finalPath);
            }
            throw;
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: true);
        }
        foreach (var subdirectory in sourceInfo.EnumerateDirectories())
        {
            CopyDirectory(subdirectory.FullName, Path.Combine(destination, subdirectory.Name));
        }
    }

    private static void WriteMarkerSafely(string destinationRoot, string sourceRoot, ILogger? logger)
    {
        try
        {
            File.WriteAllText(Path.Combine(destinationRoot, MarkerFileName), sourceRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The data itself was adopted; a missing marker only loses provenance.
            logger?.LogWarning(exception, "Could not write {Marker} in {Root}.", MarkerFileName, destinationRoot);
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{16}$")]
    private static partial Regex FingerprintPattern();
}
