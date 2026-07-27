using System.Diagnostics;
using System.Runtime.InteropServices;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Codex;

/// <summary>One located Codex CLI candidate: the native executable and where it came from.</summary>
public sealed record CodexCandidate(string Path, string Source);

/// <summary>
/// Single source of truth for locating the Codex CLI, shared by the app-server client (execution)
/// and the install probe (UI "is Codex installed?"). Keeping one ordered enumeration prevents the
/// drift where the UI reports one binary as installed while execution launches a different one.
///
/// Priority (first match wins):
///   1. explicit app setting (<c>--codex-executable</c> / <see cref="AgentHostOptions.CodexExecutable"/>)
///   2. <c>CODEX_EXECUTABLE</c> environment variable
///   3. a <c>codex</c> on PATH — i.e. the install a terminal would run
///   4. the global npm install's platform-native codex.exe
///   5. <c>~/.codex/.sandbox-bin/codex.exe</c> — LAST-resort compatibility fallback
///
/// The bundled <c>.sandbox-bin</c> copy used to be preferred, which pinned GPTino to an older CLI
/// than the one the user's terminal picked up; it is now the final fallback so GPTino and the
/// terminal converge on the same install. Every candidate resolves an npm shim (codex.cmd) to its
/// platform-native codex.exe. Enumeration is pure file-existence (no process spawn) so the UI probe
/// stays cheap; version discovery is a separate best-effort call used only for logging.
/// </summary>
public static class CodexExecutableResolver
{
    public static IReadOnlyList<CodexCandidate> EnumerateCandidates(AgentHostOptions options)
    {
        var results = new List<CodexCandidate>();

        void Add(string? rawPath, string source)
        {
            var native = ResolveNative(rawPath);
            if (native is not null &&
                !results.Any(existing => string.Equals(existing.Path, native, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new CodexCandidate(native, source));
            }
        }

        // An explicit choice is AUTHORITATIVE: if the app setting or CODEX_EXECUTABLE is set, that is
        // the only candidate — it must resolve or nothing does. We never silently fall back to a
        // discovered binary, because pointing execution at a DIFFERENT install than the one the user
        // named is exactly the drift this resolver exists to prevent (and the install probe then
        // reports "not found", so the UI and execution still agree).
        if (!string.IsNullOrWhiteSpace(options.CodexExecutable))
        {
            Add(options.CodexExecutable, "app setting");
            return results;
        }
        var configuredEnv = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configuredEnv))
        {
            Add(configuredEnv, "CODEX_EXECUTABLE");
            return results;
        }

        // No explicit choice — discover, preferring the install a terminal would run.
        foreach (var onPath in EnumeratePathCodex())
        {
            Add(onPath, "PATH (terminal)");
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmDirectory = Path.Combine(roaming, "npm");
        Add(ResolveNpmNativeExecutable(npmDirectory), "global npm");
        Add(Path.Combine(npmDirectory, "codex.cmd"), "global npm");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe"), "bundled (.sandbox-bin)");

        return results;
    }

    public static bool TryResolve(AgentHostOptions options, out CodexCandidate candidate)
    {
        candidate = EnumerateCandidates(options).FirstOrDefault()!;
        return candidate is not null;
    }

    /// <summary>
    /// Best-effort <c>codex --version</c> for logging/diagnostics. Returns null on any failure — a
    /// missing version never blocks launch; the App Server handshake is the real compatibility gate.
    /// </summary>
    public static string? ProbeVersion(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            var text = output.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePathCodex()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(value => value.Trim().Trim('"'))
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string full;
            try
            {
                full = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            // codex.exe is directly launchable; codex.cmd is the npm shim (resolved to native later).
            foreach (var name in new[] { "codex.exe", "codex.cmd" })
            {
                var executable = Path.Combine(full, name);
                if (File.Exists(executable))
                {
                    yield return executable;
                }
            }
        }
    }

    /// <summary>
    /// Resolves a raw path to a launchable native codex.exe: a direct codex.exe is returned as-is; an
    /// npm shim (codex.cmd / codex.*) is mapped to its platform-native codex.exe; anything else (or a
    /// missing file) yields null.
    /// </summary>
    private static string? ResolveNative(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }
        string path;
        try
        {
            path = Path.GetFullPath(rawPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
        if (!File.Exists(path))
        {
            return null;
        }
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        if (fileName.StartsWith("codex.", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveNpmNativeExecutable(Path.GetDirectoryName(path)!);
        }
        return null;
    }

    private static string? ResolveNpmNativeExecutable(string? npmDirectory)
    {
        if (string.IsNullOrWhiteSpace(npmDirectory) || !Directory.Exists(npmDirectory))
        {
            return null;
        }
        var (packageSuffix, target) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ("x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("arm64", "aarch64-pc-windows-msvc"),
            _ => (string.Empty, string.Empty)
        };
        if (packageSuffix.Length == 0)
        {
            return null;
        }
        var packageRoot = Path.Combine(npmDirectory, "node_modules", "@openai", "codex");
        var candidates = new[]
        {
            Path.Combine(packageRoot, "node_modules", "@openai", $"codex-win32-{packageSuffix}", "vendor", target, "bin", "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "bin", "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "codex.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
