using System.Diagnostics;
using GPTino.AgentHost.Hosting;
using Microsoft.Extensions.Logging;

namespace GPTino.AgentHost.Codex;

/// <summary>
/// Opens a visible console that remediates whichever Codex auth state the panel is blocked on:
/// with the CLI installed it runs <c>codex login</c> (browser OAuth); with the CLI missing it
/// first installs it via <c>npm install -g</c> and chains into login. AgentHost itself runs
/// windowless with redirected stdio, so — like the per-session
/// <see cref="Hosting.TerminalLauncher"/> — this must spawn a separate process that owns its own
/// console rather than reusing AgentHost's std streams. The spawned shell inherits AgentHost's
/// environment (including any <c>CODEX_HOME</c>), so it writes credentials to the same store the
/// Codex app-server reads.
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
        // `cmd /k ...` — /k keeps the window open after the flow so the user sees the result and
        // can retry. With the CLI installed, the doubled outer quotes are how cmd's /k preserves a
        // quoted executable path that may contain spaces. With it missing, the terminal installs
        // the CLI and chains into login; if npm itself is absent, cmd's own "'npm' is not
        // recognized" plus the echoed Node.js hint stay visible in the open window — that IS the
        // guidance surface, and the panel's login gate stays up for a retry after installing Node.
        var hasCli = CodexInstallation.TryLocateExecutable(_options, out var codexPath);
        var arguments = hasCli
            ? $"/k \"\"{codexPath}\" login\""
            : "/k \"echo Installing the Codex CLI with npm (requires Node.js - https://nodejs.org) & npm install -g @openai/codex && codex login\"";
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var process = Process.Start(startInfo);
            message = hasCli
                ? "Opened a terminal running 'codex login'."
                : "Opened a terminal installing the Codex CLI, then running 'codex login'.";
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
