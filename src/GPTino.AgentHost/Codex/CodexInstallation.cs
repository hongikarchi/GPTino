using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Codex;

/// <summary>
/// Read-only helpers for locating the local Codex CLI install and its on-disk user state.
/// GPTino delegates all Codex authentication to the CLI's own credential store (CODEX_HOME,
/// default <c>~/.codex</c>) and never writes it. This mirrors the executable resolution order
/// in <see cref="CodexAppServerClient"/> but only needs a yes/no answer, so it also accepts the
/// npm shim (<c>codex.cmd</c>) as evidence of an install and never throws.
/// </summary>
public static class CodexInstallation
{
    public static string ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                return Path.GetFullPath(configured);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Fall back to the default location below.
            }
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex");
    }

    public static string AuthFilePath() => Path.Combine(ResolveCodexHome(), "auth.json");

    /// <summary>
    /// True when a non-empty <c>auth.json</c> is present in the Codex home — evidence of a
    /// completed <c>codex login</c>. This is a file-level heuristic; it does not confirm the
    /// stored token is still valid with the server.
    /// </summary>
    public static bool HasStoredCredentials()
    {
        try
        {
            var authFile = AuthFilePath();
            return File.Exists(authFile) && new FileInfo(authFile).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort location of a launchable Codex CLI. Returns false when nothing is found rather
    /// than throwing, so callers can distinguish "not installed" from "installed but signed out".
    /// </summary>
    public static bool TryLocateExecutable(AgentHostOptions options, out string path)
    {
        path = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.CodexExecutable) && File.Exists(options.CodexExecutable))
            {
                path = Path.GetFullPath(options.CodexExecutable);
                return true;
            }
            var configured = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                path = Path.GetFullPath(configured);
                return true;
            }
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bundled = Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe");
            if (File.Exists(bundled))
            {
                path = bundled;
                return true;
            }
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var candidate in new[]
                     {
                         Path.Combine(roaming, "npm", "codex.cmd"),
                         Path.Combine(roaming, "npm", "codex.exe"),
                     })
            {
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = directory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }
                string full;
                try
                {
                    full = Path.GetFullPath(trimmed);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    continue;
                }
                foreach (var name in new[] { "codex.exe", "codex.cmd" })
                {
                    var executable = Path.Combine(full, name);
                    if (File.Exists(executable))
                    {
                        path = executable;
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Treat any probing failure as "not found".
        }
        return false;
    }
}
