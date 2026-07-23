using System.Diagnostics;
using GPTino.AgentHost.Hosting;
using Microsoft.Extensions.Logging;

namespace GPTino.AgentHost.Codex;

/// <summary>
/// Opens a visible console running <c>codex login</c> so the user can complete the browser OAuth
/// flow from the panel. AgentHost itself runs windowless with redirected stdio, so — like the
/// per-session <see cref="Hosting.TerminalLauncher"/> — this must spawn a separate process that
/// owns its own console rather than reusing AgentHost's std streams. The spawned shell inherits
/// AgentHost's environment (including any <c>CODEX_HOME</c>), so it writes credentials to the
/// same store the Codex app-server reads.
/// </summary>
public sealed class CodexLoginLauncher
{
    private readonly AgentHostOptions _options;
    private readonly ILogger<CodexLoginLauncher>? _logger;

    public CodexLoginLauncher(AgentHostOptions options, ILogger<CodexLoginLauncher>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public bool TryLaunch(out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Opening a Codex login terminal is only supported on Windows.";
            return false;
        }
        if (!CodexInstallation.TryLocateExecutable(_options, out var codexPath))
        {
            message = "Codex CLI was not found. Install it with npm, then run 'codex login'.";
            return false;
        }
        try
        {
            // `cmd /k ""<path>" login"` — the doubled outer quotes are how cmd's /k preserves a
            // quoted executable path that may contain spaces; /k keeps the window open after the
            // OAuth flow so the user sees the result and can retry.
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/k \"\"{codexPath}\" login\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var process = Process.Start(startInfo);
            message = "Opened a terminal running 'codex login'.";
            return true;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not open the Codex login terminal.");
            message = $"Could not open the login terminal: {exception.Message}";
            return false;
        }
    }
}
