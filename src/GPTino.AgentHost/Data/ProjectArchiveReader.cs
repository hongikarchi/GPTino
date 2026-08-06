using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GPTino.AgentHost.Api;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Data;

/// <summary>
/// Read-only browser over every GPTino project data root on this machine, so a user whose
/// document identity changed (crash, autosave-restore, Save As before the live rebind) can
/// still open and read what earlier sessions did. Strictly an observer: it never creates
/// files in other roots, never takes their instance lock, and opens every runtime.db with
/// Mode=ReadOnly and pooling disabled so no handle outlives a request. A root that cannot
/// be read (concurrently locked, damaged, hand-edited) degrades to an unavailable entry
/// instead of failing the whole listing.
/// </summary>
public sealed partial class ProjectArchiveReader
{
    // The import export clamps at the same 1000-row window as the archive reads; the banner
    // discloses when older history falls outside it.
    private const int MaxImportWindow = 1000;

    private readonly string _projectsParentDirectory;
    private readonly string _currentDataDirectory;

    public ProjectArchiveReader(string projectsParentDirectory, string currentDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsParentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDataDirectory);
        _projectsParentDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectsParentDirectory));
        _currentDataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDataDirectory));
    }

    /// <summary>
    /// The default fingerprint parent. Derived from LocalApplicationData on purpose — even when
    /// the running host was pointed elsewhere via --data-directory, past projects live here.
    /// </summary>
    public static string DefaultProjectsParentDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPTino",
            "projects");

    public async Task<IReadOnlyList<ArchivedProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_projectsParentDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_projectsParentDirectory))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
                if (FingerprintPattern().IsMatch(name))
                {
                    roots[Path.GetFullPath(directory)] = name;
                }
            }
        }

        if (Directory.Exists(_currentDataDirectory))
        {
            roots[_currentDataDirectory] = CurrentRootName();
        }

        var projects = new List<ArchivedProject>(roots.Count);
        foreach (var (rootPath, fingerprint) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(await ReadProjectAsync(rootPath, fingerprint, cancellationToken).ConfigureAwait(false));
        }

        return projects
            .OrderByDescending(project => project.LastActivityAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public async Task<IReadOnlyList<ArchivedMessage>> ReadMessagesAsync(
        string fingerprint,
        Guid sessionId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var rootPath = ResolveRootPath(fingerprint)
            ?? throw new KeyNotFoundException($"Archive project '{fingerprint}' was not found.");
        var databasePath = Path.Combine(rootPath, "runtime.db");
        if (!File.Exists(databasePath))
        {
            throw new KeyNotFoundException($"Archive project '{fingerprint}' has no runtime database.");
        }

        try
        {
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
            if (!await SessionExistsAsync(connection, sessionId, cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException(
                    $"Session {sessionId:D} was not found in archive project '{fingerprint}'.");
            }

            // Newest window in ascending order, matching the live SessionStore read shape.
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,role,content,phase,created_at
                FROM (
                    SELECT id,role,content,phase,created_at
                    FROM messages
                    WHERE session_id=$session
                    ORDER BY id DESC
                    LIMIT $limit
                ) AS newest
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            var messages = new List<ArchivedMessage>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(new ArchivedMessage(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            }
            return messages;
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            throw new InvalidOperationException(
                $"The archive database for project '{fingerprint}' cannot be read right now: {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Reads one archived session for import into the live runtime: its name, last-active date,
    /// total message count, and the newest message window (clamped identically to the archive
    /// reads). Strictly read-only — the foreign root is opened Mode=ReadOnly/Pooling=false and is
    /// never written. Degrades exactly like <see cref="ReadMessagesAsync"/>: a missing project or
    /// session is a KeyNotFoundException (404); an unreadable database is an InvalidOperationException.
    /// </summary>
    public async Task<ArchivedSessionExport> ReadSessionForImportAsync(
        string fingerprint,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var rootPath = ResolveRootPath(fingerprint)
            ?? throw new KeyNotFoundException($"Archive project '{fingerprint}' was not found.");
        var databasePath = Path.Combine(rootPath, "runtime.db");
        if (!File.Exists(databasePath))
        {
            throw new KeyNotFoundException($"Archive project '{fingerprint}' has no runtime database.");
        }

        try
        {
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
            var header = await ReadSessionHeaderAsync(connection, sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"Session {sessionId:D} was not found in archive project '{fingerprint}'.");
            var total = await ReadSessionMessageCountAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            var messages = await ReadSessionWindowAsync(connection, sessionId, MaxImportWindow, cancellationToken).ConfigureAwait(false);
            var manifest = ReadManifest(rootPath);
            return new ArchivedSessionExport(
                fingerprint,
                manifest?.ProjectName,
                header.Name,
                header.UpdatedAt,
                total,
                messages);
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            throw new InvalidOperationException(
                $"The archive database for project '{fingerprint}' cannot be read right now: {exception.Message}",
                exception);
        }
    }

    private static async Task<(string Name, DateTimeOffset UpdatedAt)?> ReadSessionHeaderAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, updated_at FROM sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return (reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }

    private static async Task<int> ReadSessionMessageCountAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<ArchivedMessage>> ReadSessionWindowAsync(
        SqliteConnection connection,
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,role,content,phase,created_at
            FROM (
                SELECT id,role,content,phase,created_at
                FROM messages
                WHERE session_id=$session
                ORDER BY id DESC
                LIMIT $limit
            ) AS newest
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var messages = new List<ArchivedMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new ArchivedMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }
        return messages;
    }

    private async Task<ArchivedProject> ReadProjectAsync(
        string rootPath,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var current = string.Equals(rootPath, _currentDataDirectory, StringComparison.OrdinalIgnoreCase);
        var manifest = ReadManifest(rootPath);
        var databasePath = Path.Combine(rootPath, "runtime.db");
        if (File.Exists(databasePath))
        {
            try
            {
                var sessions = await ReadSessionSummariesAsync(databasePath, cancellationToken).ConfigureAwait(false);
                return new ArchivedProject(
                    fingerprint,
                    manifest?.ProjectName,
                    manifest?.RhinoFile,
                    manifest?.GrasshopperFile,
                    manifest?.CreatedAt,
                    sessions.Count == 0 ? null : sessions.Max(session => session.UpdatedAt),
                    sessions.Count,
                    current,
                    Available: true,
                    sessions);
            }
            catch (Exception exception) when (IsUnreadable(exception))
            {
                // Fall through to the unavailable shape below.
            }
        }

        return new ArchivedProject(
            fingerprint,
            manifest?.ProjectName,
            manifest?.RhinoFile,
            manifest?.GrasshopperFile,
            manifest?.CreatedAt,
            LastActivityAt: null,
            SessionCount: 0,
            current,
            Available: false,
            Array.Empty<ArchivedSession>());
    }

    private static async Task<IReadOnlyList<ArchivedSession>> ReadSessionSummariesAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Deleted (soft-deleted) sessions are still in the table, parked at deep-negative sort_order.
        // Surface them with a flag and list them AFTER the live ones so the archive can show the whole
        // project — the panel renders deleted rows distinctly and offers restore / delete-forever.
        command.CommandText = """
            SELECT s.id, s.name, s.state, s.updated_at,
                   (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.id),
                   (s.deleted_at IS NOT NULL)
            FROM sessions s
            ORDER BY (s.deleted_at IS NOT NULL), s.sort_order;
            """;
        var sessions = new List<ArchivedSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(new ArchivedSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetInt32(4),
                reader.GetInt64(5) != 0));
        }
        return sessions;
    }

    private string? ResolveRootPath(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (string.Equals(fingerprint, CurrentRootName(), StringComparison.Ordinal) &&
            Directory.Exists(_currentDataDirectory))
        {
            return _currentDataDirectory;
        }

        if (!FingerprintPattern().IsMatch(fingerprint))
        {
            throw new ArgumentException(
                "An archive fingerprint must be 16 hexadecimal characters.",
                nameof(fingerprint));
        }

        var candidate = Path.GetFullPath(Path.Combine(_projectsParentDirectory, fingerprint));
        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                _projectsParentDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An archive fingerprint must name a direct child of the projects directory.",
                nameof(fingerprint));
        }

        return Directory.Exists(candidate) ? candidate : null;
    }

    private string CurrentRootName() =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(_currentDataDirectory));

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ProjectManifest? ReadManifest(string rootPath)
    {
        var manifestPath = Path.Combine(rootPath, "context", "project.json");
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            return new ProjectManifest(
                ReadString(root, "projectName"),
                ReadString(root, "rhinoFile"),
                ReadString(root, "grasshopperFile"),
                root.TryGetProperty("createdAt", out var created) &&
                created.ValueKind == JsonValueKind.String &&
                created.TryGetDateTimeOffset(out var createdAt)
                    ? createdAt
                    : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// Everything a foreign, possibly concurrently-open or damaged database can throw at a
    /// pure reader. Deliberately excludes KeyNotFoundException (a valid 404) and cancellation.
    /// </summary>
    private static bool IsUnreadable(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or FormatException
            or InvalidCastException
            or OverflowException;

    [GeneratedRegex("^[0-9A-Fa-f]{16}$")]
    private static partial Regex FingerprintPattern();

    private sealed record ProjectManifest(
        string? ProjectName,
        string? RhinoFile,
        string? GrasshopperFile,
        DateTimeOffset? CreatedAt);
}
