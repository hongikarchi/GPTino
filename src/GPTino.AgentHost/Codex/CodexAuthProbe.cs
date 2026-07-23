using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Codex;

public enum CodexAuthStatus
{
    LoggedIn,
    LoggedOut,
    CliMissing,
}

public sealed record CodexAuthSnapshot(CodexAuthStatus Status, string Detail)
{
    /// <summary>The lowercase wire value the panel UI switches on.</summary>
    public string Wire => Status switch
    {
        CodexAuthStatus.LoggedIn => "logged-in",
        CodexAuthStatus.CliMissing => "cli-missing",
        _ => "logged-out",
    };
}

/// <summary>
/// Reports whether the local Codex CLI is installed and signed in, so the panel can show a
/// login indicator. This is a file-level heuristic (a non-empty <c>auth.json</c> under
/// CODEX_HOME); it does not validate the token with the server. Results are cached for a few
/// seconds so the frequent SSE re-projection does not stat the filesystem on every event.
/// </summary>
public sealed class CodexAuthProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);

    private readonly AgentHostOptions _options;
    private readonly object _gate = new();
    private CodexAuthSnapshot? _cached;
    private DateTime _checkedAtUtc = DateTime.MinValue;

    public CodexAuthProbe(AgentHostOptions options) => _options = options;

    public CodexAuthSnapshot Read()
    {
        lock (_gate)
        {
            if (_cached is not null && DateTime.UtcNow - _checkedAtUtc < CacheDuration)
            {
                return _cached;
            }
            _cached = Evaluate();
            _checkedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    private CodexAuthSnapshot Evaluate()
    {
        if (CodexInstallation.HasStoredCredentials())
        {
            return new CodexAuthSnapshot(CodexAuthStatus.LoggedIn, "Signed in to Codex.");
        }
        if (!CodexInstallation.TryLocateExecutable(_options, out _))
        {
            return new CodexAuthSnapshot(
                CodexAuthStatus.CliMissing,
                "Codex CLI was not found. Install it with npm, then sign in.");
        }
        return new CodexAuthSnapshot(
            CodexAuthStatus.LoggedOut,
            "Not signed in — click to run 'codex login'.");
    }
}
