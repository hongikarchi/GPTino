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
    /// Best-effort location of a launchable Codex CLI. Delegates to the shared
    /// <see cref="CodexExecutableResolver"/> so the "is Codex installed?" answer the UI shows always
    /// matches the binary execution will actually launch (same priority order, same npm-shim → native
    /// resolution). Returns false when nothing launchable is found rather than throwing, so callers
    /// can distinguish "not installed" from "installed but signed out".
    /// </summary>
    public static bool TryLocateExecutable(AgentHostOptions options, out string path)
    {
        if (CodexExecutableResolver.TryResolve(options, out var candidate))
        {
            path = candidate.Path;
            return true;
        }
        path = string.Empty;
        return false;
    }
}
