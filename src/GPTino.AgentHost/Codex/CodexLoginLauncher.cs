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
        // can retry; the doubled outer quotes are how cmd's /k preserves a quoted path that may
        // contain spaces.
        var hasCli = CodexInstallation.TryLocateExecutable(_options, out var codexPath);
        try
        {
            var arguments = hasCli
                ? $"/k \"\"{codexPath}\" login\""
                : $"/k \"\"{WriteInstallScript()}\"\"";
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

    /// <summary>
    /// Emits the CLI-missing remediation as a batch script rather than a <c>/k</c> one-liner.
    /// The spawned cmd inherits AgentHost's PATH as captured at Rhino launch, so a Node.js
    /// installed mid-session is invisible to a bare <c>npm</c> — and a fresh install's
    /// <c>codex</c> shim likewise. The script probes the standard on-disk locations first,
    /// falls back to PATH, and names the real remediation (restart Rhino) when even that fails.
    /// </summary>
    private static string WriteInstallScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "gptino-codex-setup.cmd");
        File.WriteAllText(scriptPath, string.Join("\r\n", new[]
        {
            "@echo off",
            "echo Installing the Codex CLI with npm (requires Node.js - https://nodejs.org)",
            "set \"NPM_CMD=npm\"",
            "if exist \"%ProgramFiles%\\nodejs\\npm.cmd\" set \"NPM_CMD=%ProgramFiles%\\nodejs\\npm.cmd\"",
            "call \"%NPM_CMD%\" install -g @openai/codex",
            "if errorlevel 1 (",
            "  echo.",
            "  echo Install failed. If you just installed Node.js, restart Rhino and click the button again.",
            "  goto :eof",
            ")",
            "set \"CODEX_CMD=codex\"",
            "if exist \"%APPDATA%\\npm\\codex.cmd\" set \"CODEX_CMD=%APPDATA%\\npm\\codex.cmd\"",
            "call \"%CODEX_CMD%\" login",
            "",
        }));
        return scriptPath;
    }
}
