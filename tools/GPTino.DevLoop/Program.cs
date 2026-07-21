using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GPTino.DevLoop;

internal static partial class Program
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RhinoLiveTimeout = TimeSpan.FromHours(2);
    private static readonly HashSet<string> SnapshotExcludedDirectories = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".references", ".vs", "artifacts", "bin", "dist", "node_modules", "obj",
        "TestResults"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var stage))
        {
            Console.Error.WriteLine(
                "Usage: GPTino.DevLoop verify --stage boundary|mcp|orchestrator|full|smoke|live-codex|package|rhino-live");
            return 2;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var stageLease = AcquireExclusiveStageLease(repositoryRoot);
        var runContext = CreateRun(repositoryRoot, stage);
        var run = runContext.Run;
        using var sourceLease = runContext.SourceLease;
        Console.WriteLine($"GPTino DevLoop run: {run.RunRoot}");

        try
        {
            if (stage == VerificationStage.Boundary)
            {
                RunBoundaryChecks(repositoryRoot);
            }
            else
            {
                foreach (var command in CreateCommands(stage, repositoryRoot, run.RunRoot))
                {
                    var result = await RunCommandAsync(command, repositoryRoot, run);
                    run.Commands.Add(result);
                    run.ActiveCommand = null;
                    WriteRunRecord(run);

                    if (result.ExitCode != 0 || result.TimedOut)
                    {
                        var signature = CreateErrorSignature(stage, result, repositoryRoot, run.RunRoot);
                        result.ErrorSignature = signature;
                        WriteRunRecord(run);
                        Console.Error.WriteLine($"FAILED: {signature}");
                        run.Status = "failed";
                        run.CompletedAtUtc = DateTimeOffset.UtcNow;
                        _ = RecordCompletedSourceEvidence(run, sourceLease);
                        WriteRunRecord(run);
                        return 1;
                    }
                }
            }

            var sourceEvidenceError = RecordCompletedSourceEvidence(run, sourceLease);
            if (sourceEvidenceError is not null)
            {
                throw new InvalidOperationException(
                    "Repository source changed while the verification stage was running: " +
                    sourceEvidenceError);
            }

            run.Status = "passed";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteRunRecord(run);
            Console.WriteLine($"PASSED: {stage.ToString().ToLowerInvariant()}");
            return 0;
        }
        catch (Exception exception)
        {
            run.Status = "failed";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.FatalError = Redact(exception.GetType().Name + ": " + exception.Message);
            if (run.CompletedSourceSnapshotSha256 is null)
            {
                _ = RecordCompletedSourceEvidence(run, sourceLease);
            }
            WriteRunRecord(run);
            Console.Error.WriteLine(run.FatalError);
            return 1;
        }
    }

    private static FileStream AcquireExclusiveStageLease(string repositoryRoot)
    {
        var artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "dev-loop"));
        AssertStrictDescendant(artifactRoot, repositoryRoot, "development artifact root");
        Directory.CreateDirectory(artifactRoot);
        AssertExistingPathHasNoReparsePoint(repositoryRoot, artifactRoot);
        var lockPath = Path.Combine(artifactRoot, ".gptino-devloop.lock");
        var lockInfo = new FileInfo(lockPath);
        lockInfo.Refresh();
        if (lockInfo.LinkTarget is not null ||
            (lockInfo.Exists && (lockInfo.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidOperationException("The DevLoop stage lock is a reparse point.");
        }
        FileStream lease;
        try
        {
            lease = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4 * 1024,
                FileOptions.WriteThrough);
            lockInfo.Refresh();
            if (lockInfo.LinkTarget is not null ||
                (lockInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                lease.Dispose();
                throw new InvalidOperationException("The DevLoop stage lock became a reparse point.");
            }
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another GPTino DevLoop stage already owns the shared build outputs.",
                exception);
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            var identity = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    processId = current.Id,
                    processStartTimeUtc = current.StartTime.ToUniversalTime(),
                    acquiredAtUtc = DateTimeOffset.UtcNow
                },
                JsonOptions);
            lease.SetLength(0);
            lease.Write(identity);
            lease.Flush(flushToDisk: true);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static bool TryParseArguments(string[] args, out VerificationStage stage)
    {
        stage = default;
        if (args.Length != 3 ||
            !string.Equals(args[0], "verify", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--stage", StringComparison.Ordinal))
        {
            return false;
        }
        var parsed = args[2].ToLowerInvariant() switch
        {
            "boundary" => VerificationStage.Boundary,
            "mcp" => VerificationStage.Mcp,
            "orchestrator" => VerificationStage.Orchestrator,
            "full" => VerificationStage.Full,
            "smoke" => VerificationStage.Smoke,
            "live-codex" => VerificationStage.LiveCodex,
            "package" => VerificationStage.Package,
            "rhino-live" => VerificationStage.RhinoLive,
            _ => (VerificationStage?)null
        };
        if (parsed is null)
        {
            return false;
        }
        stage = parsed.Value;
        return true;
    }

    private static RunContext CreateRun(string repositoryRoot, VerificationStage stage)
    {
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "dev-loop"));
        var runRoot = Path.GetFullPath(Path.Combine(artifactRoot, runId));
        AssertStrictDescendant(runRoot, artifactRoot, "run directory");
        AssertExistingPathHasNoReparsePoint(repositoryRoot, artifactRoot);
        Directory.CreateDirectory(runRoot);
        AssertExistingPathHasNoReparsePoint(repositoryRoot, runRoot);
        File.WriteAllText(
            Path.Combine(runRoot, ".gptino-owned-run"),
            "GPTino DevLoop owned run\n",
            new UTF8Encoding(false));

        foreach (var directory in new[]
                 {
                     "tmp", "dotnet-home", "nuget-packages", "npm-cache", "logs"
                 })
        {
            Directory.CreateDirectory(Path.Combine(runRoot, directory));
        }

        var sourceLease = SourceSnapshotLease.Acquire(repositoryRoot);
        var sourceSnapshot = sourceLease.InitialSnapshot;
        var record = new RunRecord
        {
            RunId = runId,
            Stage = stage.ToString().ToLowerInvariant(),
            RunRoot = runRoot,
            RepositoryRoot = repositoryRoot,
            StartedAtUtc = DateTimeOffset.UtcNow,
            SourceSnapshotSha256 = sourceSnapshot.Sha256,
            SourceFileCount = sourceSnapshot.FileCount,
            Status = "running"
        };
        try
        {
            WriteRunRecord(record);
            return new RunContext(record, sourceLease);
        }
        catch
        {
            sourceLease.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<CommandSpec> CreateCommands(
        VerificationStage stage,
        string repositoryRoot,
        string runRoot)
    {
        var agentTests = Path.Combine(
            repositoryRoot,
            "tests",
            "GPTino.AgentHost.Tests",
            "GPTino.AgentHost.Tests.csproj");
        var agentHostProject = Path.Combine(
            repositoryRoot,
            "src",
            "GPTino.AgentHost",
            "GPTino.AgentHost.csproj");
        var smokeBridgeProject = Path.Combine(
            repositoryRoot,
            "tools",
            "GPTino.SmokeBridge",
            "GPTino.SmokeBridge.csproj");
        var solution = Path.Combine(repositoryRoot, "GPTino.sln");
        var liveE2E = Path.Combine(
            repositoryRoot,
            "tools",
            "GPTino.LiveE2E",
            "GPTino.LiveE2E.csproj");
        var packageRoot = Path.Combine(runRoot, "package");
        var smokeAgentRoot = Path.Combine(runRoot, "smoke-payload", "agent");
        var smokeBridgeRoot = Path.Combine(runRoot, "smoke-payload", "bridge");
        var smokeAgentExecutable = Path.Combine(smokeAgentRoot, "GPTino.AgentHost.exe");
        var smokeBridgeExecutable = Path.Combine(smokeBridgeRoot, "GPTino.SmokeBridge.exe");
        var commands = new List<CommandSpec>();

        switch (stage)
        {
            case VerificationStage.Mcp:
                commands.Add(Dotnet("restore", [agentTests], LongTimeout));
                AddRepeatedTests(
                    commands,
                    "mcp-protocol",
                    agentTests,
                    "FullyQualifiedName~CodexAppServerClientProtocolTests",
                    10);
                break;

            case VerificationStage.Orchestrator:
                commands.Add(Dotnet("restore", [agentTests], LongTimeout));
                AddRepeatedTests(
                    commands,
                    "session-orchestrator",
                    agentTests,
                    "FullyQualifiedName~SessionOrchestratorTests",
                    10);
                break;

            case VerificationStage.Full:
                commands.Add(Dotnet("restore", [solution], LongTimeout));
                for (var iteration = 1; iteration <= 3; iteration++)
                {
                    commands.Add(Dotnet(
                        $"full-tests-{iteration}",
                        [
                            "test", solution, "--configuration", "Release", "--no-restore",
                            "--logger", "console;verbosity=minimal"
                        ],
                        LongTimeout));
                }
                commands.Add(new CommandSpec(
                    "panel-tests",
                    OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    ["test"],
                    Path.Combine(repositoryRoot, "ui", "panel"),
                    ShortTimeout));
                commands.Add(new CommandSpec(
                    "panel-build",
                    OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    ["run", "build"],
                    Path.Combine(repositoryRoot, "ui", "panel"),
                    ShortTimeout));
                break;

            case VerificationStage.Smoke:
                commands.Add(Dotnet("restore", [solution], LongTimeout));
                commands.Add(new CommandSpec(
                    "panel-build",
                    OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    ["run", "build"],
                    Path.Combine(repositoryRoot, "ui", "panel"),
                    ShortTimeout));
                commands.Add(Dotnet(
                    "publish-agenthost",
                    [
                        "publish", agentHostProject, "--configuration", "Release", "--no-restore",
                        "--output", smokeAgentRoot
                    ],
                    LongTimeout));
                var smokeCodexExecutable = ResolveLiveCodexExecutable();
                for (var iteration = 1; iteration <= 3; iteration++)
                {
                    commands.Add(PowerShell(
                        $"codex-smoke-{iteration}",
                        Path.Combine(repositoryRoot, "scripts", "smoke-agenthost.ps1"),
                        [
                            "-Configuration", "Release",
                            "-AgentHostExecutable", smokeAgentExecutable,
                            "-CodexExecutable", smokeCodexExecutable
                        ],
                        repositoryRoot,
                        ShortTimeout));
                }
                break;

            case VerificationStage.LiveCodex:
                commands.Add(Dotnet("restore", [solution], LongTimeout));
                commands.Add(new CommandSpec(
                    "panel-build",
                    OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    ["run", "build"],
                    Path.Combine(repositoryRoot, "ui", "panel"),
                    ShortTimeout));
                commands.Add(Dotnet(
                    "publish-agenthost",
                    [
                        "publish", agentHostProject, "--configuration", "Release", "--no-restore",
                        "--output", smokeAgentRoot
                    ],
                    LongTimeout));
                commands.Add(Dotnet(
                    "publish-smoke-bridge",
                    [
                        "publish", smokeBridgeProject, "--configuration", "Release", "--no-restore",
                        "--output", smokeBridgeRoot
                    ],
                    LongTimeout));
                var codexExecutable = ResolveLiveCodexExecutable();
                for (var iteration = 1; iteration <= 3; iteration++)
                {
                    commands.Add(PowerShell(
                        $"live-codex-{iteration}",
                        Path.Combine(repositoryRoot, "scripts", "smoke-agenthost.ps1"),
                        [
                            "-Configuration", "Release",
                            "-AgentHostExecutable", smokeAgentExecutable,
                            "-CodexExecutable", codexExecutable,
                            "-LiveCodexTurn",
                            "-LiveCodexTurnTimeoutSeconds", "240",
                            "-SmokeBridgeExecutable", smokeBridgeExecutable
                        ],
                        repositoryRoot,
                        TimeSpan.FromMinutes(6)));
                }
                break;

            case VerificationStage.Package:
                commands.Add(Dotnet("restore", [solution], LongTimeout));
                commands.Add(PowerShell(
                    "package",
                    Path.Combine(repositoryRoot, "scripts", "build-package.ps1"),
                    ["-Configuration", "Release", "-OutputRoot", packageRoot],
                    repositoryRoot,
                    LongTimeout));
                break;

            case VerificationStage.RhinoLive:
                commands.Add(Dotnet("restore", [solution], LongTimeout));
                commands.Add(PowerShell(
                    "package-live-yak",
                    Path.Combine(repositoryRoot, "scripts", "build-package.ps1"),
                    [
                        "-Configuration", "Release",
                        "-OutputRoot", packageRoot,
                        "-BuildYak"
                    ],
                    repositoryRoot,
                    RhinoLiveTimeout));
                commands.Add(Dotnet(
                    "restore-live-e2e",
                    ["restore", liveE2E],
                    LongTimeout));
                commands.Add(Dotnet(
                    "build-live-e2e",
                    [
                        "build", liveE2E,
                        "--configuration", "Release",
                        "--no-restore"
                    ],
                    LongTimeout));
                commands.Add(Dotnet(
                    "rhino-live-e2e",
                    [
                        "run", "--project", liveE2E,
                        "--configuration", "Release",
                        "--no-build", "--no-restore", "--",
                        "verify", "--run-root", runRoot
                    ],
                    RhinoLiveTimeout));
                break;

            default:
                throw new InvalidOperationException($"No command set exists for stage {stage}.");
        }

        ValidatePackageRestorePlan(stage, commands);
        return commands;
    }

    private static void ValidatePackageRestorePlan(
        VerificationStage stage,
        IReadOnlyCollection<CommandSpec> commands)
    {
        if (stage is not (VerificationStage.Package or VerificationStage.RhinoLive))
        {
            return;
        }

        var packageCommand = commands.Single(command =>
            command.Name is "package" or "package-live-yak");
        if (packageCommand.Arguments.Contains("-SkipRestore", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Package and rhino-live stages must let build-package.ps1 restore with the manifest version.");
        }
    }

    private static void AddRepeatedTests(
        ICollection<CommandSpec> commands,
        string name,
        string project,
        string filter,
        int count)
    {
        for (var iteration = 1; iteration <= count; iteration++)
        {
            commands.Add(Dotnet(
                $"{name}-{iteration}",
                [
                    "test", project, "--configuration", "Release", "--no-restore",
                    "--filter", filter, "--logger", "console;verbosity=minimal"
                ],
                ShortTimeout));
        }
    }

    private static CommandSpec Dotnet(string name, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var effectiveArguments = string.Equals(name, "restore", StringComparison.Ordinal)
            ? new[] { "restore" }.Concat(arguments).ToArray()
            : arguments;
        return new CommandSpec(
            name,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            effectiveArguments,
            string.Empty,
            timeout);
    }

    private static CommandSpec PowerShell(
        string name,
        string script,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout) =>
        new(
            name,
            OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments],
            workingDirectory,
            timeout);

    private static async Task<CommandResult> RunCommandAsync(
        CommandSpec command,
        string repositoryRoot,
        RunRecord run)
    {
        var workingDirectory = string.IsNullOrEmpty(command.WorkingDirectory)
            ? repositoryRoot
            : Path.GetFullPath(command.WorkingDirectory);
        AssertPathInsideRepository(workingDirectory, repositoryRoot, "working directory");

        var executable = ResolveExecutable(command.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        RemoveGptinoEnvironment(startInfo);
        startInfo.Environment["TEMP"] = Path.Combine(run.RunRoot, "tmp");
        startInfo.Environment["TMP"] = Path.Combine(run.RunRoot, "tmp");
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(run.RunRoot, "dotnet-home");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(run.RunRoot, "nuget-packages");
        startInfo.Environment["NPM_CONFIG_CACHE"] = Path.Combine(run.RunRoot, "npm-cache");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start fixed command {command.Name}.");
        }
        process.StandardInput.Close();
        var processStartTimeUtc = process.StartTime.ToUniversalTime();
        run.ActiveCommand = new ActiveCommandRecord
        {
            Name = command.Name,
            ExecutablePath = Path.GetFullPath(executable),
            ProcessId = process.Id,
            ProcessStartTimeUtc = processStartTimeUtc,
            StartedAtUtc = startedAt
        };
        try
        {
            WriteRunRecord(run);
        }
        catch
        {
            KillExactProcessTreeIfAlive(process, processStartTimeUtc, executable);
            throw;
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;

        using var timeout = new CancellationTokenSource(command.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
            process.Refresh();
            KillExactProcessTreeIfAlive(process, processStartTimeUtc, executable);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var output = Redact(await outputTask);
        var error = Redact(await errorTask);
        var logPrefix = Path.Combine(run.RunRoot, "logs", $"{run.Commands.Count + 1:D2}-{command.Name}");
        File.WriteAllText(logPrefix + ".stdout.log", output, new UTF8Encoding(false));
        File.WriteAllText(logPrefix + ".stderr.log", error, new UTF8Encoding(false));

        return new CommandResult
        {
            Name = command.Name,
            Executable = Path.GetFullPath(executable),
            Arguments = command.Arguments.ToArray(),
            WorkingDirectory = workingDirectory,
            ProcessId = process.Id,
            ProcessStartTimeUtc = processStartTimeUtc,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            StdoutLog = Path.GetRelativePath(run.RunRoot, logPrefix + ".stdout.log"),
            StderrLog = Path.GetRelativePath(run.RunRoot, logPrefix + ".stderr.log")
        };
    }

    private static void RunBoundaryChecks(string repositoryRoot)
    {
        var agents = File.ReadAllText(Path.Combine(repositoryRoot, "AGENTS.md"));
        foreach (var required in new[]
                 {
                     "Keep all development writes under this repository",
                     ".gptino-owned-run",
                     "Stop after three repair attempts",
                     "Only the primary agent may edit files"
                 })
        {
            if (!agents.Contains(required, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"AGENTS.md is missing safety rule: {required}");
            }
        }

        var sourceFiles = EnumerateSourceFiles(repositoryRoot).ToArray();
        foreach (var file in sourceFiles.Where(path => path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            if (PowerShellHomeVariableRegex().IsMatch(text))
            {
                throw new InvalidOperationException(
                    $"Reserved PowerShell HOME variable found in {Path.GetRelativePath(repositoryRoot, file)}.");
            }
            if (!RecursiveRemoveItemRegex().IsMatch(text))
            {
                continue;
            }

            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            var guardedBuild = relative == "scripts/build-package.ps1" &&
                               text.Contains("Assert-GeneratedPath", StringComparison.Ordinal) &&
                               text.Contains("artifactPrefix", StringComparison.Ordinal);
            if (!guardedBuild)
            {
                throw new InvalidOperationException($"Unguarded recursive Remove-Item found in {relative}.");
            }
        }

        var processGlobalSqliteCleanup = "SqliteConnection." + "ClearAllPools";
        foreach (var file in sourceFiles.Where(path =>
                     path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            if (text.Contains(processGlobalSqliteCleanup, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Process-global SQLite pool cleanup is unsafe in parallel tests: " +
                    Path.GetRelativePath(repositoryRoot, file));
            }
            if (RecursiveDirectoryDeleteRegex().IsMatch(text))
            {
                var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
                var guardedReservationCleanup =
                    relative == "src/GPTino.AgentHost/Runtime/LiveDocumentBackend.cs" &&
                    text.Contains("ConstrainedPath.RejectExistingReparsePoints", StringComparison.Ordinal) &&
                    text.Contains(".gptino-owned-reserved-job", StringComparison.Ordinal);
                if (!guardedReservationCleanup)
                {
                    throw new InvalidOperationException(
                        $"Unguarded recursive Directory.Delete found in {relative}.");
                }
            }
        }

        foreach (var file in sourceFiles.Where(path =>
                     Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat" or ".sh"))
        {
            if (DestructiveShellDeleteRegex().IsMatch(File.ReadAllText(file)))
            {
                throw new InvalidOperationException(
                    $"Destructive shell cleanup found in {Path.GetRelativePath(repositoryRoot, file)}.");
            }
        }

        foreach (var file in sourceFiles.Where(path =>
                     Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".props" or ".targets"))
        {
            if (DestructiveMsBuildDeleteRegex().IsMatch(File.ReadAllText(file)))
            {
                throw new InvalidOperationException(
                    $"MSBuild cleanup target found in {Path.GetRelativePath(repositoryRoot, file)}.");
            }
        }

        var smoke = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-agenthost.ps1"));
        if (!smoke.Contains("Stop-OwnedProcess", StringComparison.Ordinal) ||
            !smoke.Contains("-OwnedProcess", StringComparison.Ordinal) ||
            !smoke.Contains("New-OwnedProcessIdentity", StringComparison.Ordinal) ||
            !smoke.Contains("ProcessStartTimeUtc", StringComparison.Ordinal) ||
            !smoke.Contains("-OwnedIdentity", StringComparison.Ordinal) ||
            !smoke.Contains("Assert-NoExistingReparsePoint", StringComparison.Ordinal) ||
            !smoke.Contains("owned-processes", StringComparison.Ordinal) ||
            !smoke.Contains("CodexAppServer", StringComparison.Ordinal) ||
            !smoke.Contains("Resolve-CodexNativeExecutable", StringComparison.Ordinal) ||
            !smoke.Contains("$devLoopRoot", StringComparison.Ordinal) ||
            smoke.Contains("taskkill.exe", StringComparison.OrdinalIgnoreCase) ||
            RecursiveRemoveItemRegex().IsMatch(smoke))
        {
            throw new InvalidOperationException(
                "Smoke evidence or process cleanup is not exact PID/start-time owned.");
        }

        var liveE2E = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "GPTino.LiveE2E", "Program.cs"));
        var readySignal = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "GPTino.AgentHost", "Hosting", "ReadySignalService.cs"));
        if (!liveE2E.Contains("processStartTimeUtc", StringComparison.Ordinal) ||
            !liveE2E.Contains("OwnedProcesses.Add", StringComparison.Ordinal) ||
            !liveE2E.Contains("GetParentProcessId", StringComparison.Ordinal) ||
            !liveE2E.Contains("_stagedAgentHostExecutable", StringComparison.Ordinal) ||
            !liveE2E.Contains("RequireContentEqual", StringComparison.Ordinal) ||
            !liveE2E.Contains("ReadDirectoryEvidence", StringComparison.Ordinal) ||
            !liveE2E.Contains("StagedRuntimePayloadSha256", StringComparison.Ordinal) ||
            !liveE2E.Contains("InstalledRuntimePayloadSha256", StringComparison.Ordinal) ||
            !readySignal.Contains("processStartTimeUtc", StringComparison.Ordinal) ||
            !readySignal.Contains("FileOptions.WriteThrough", StringComparison.Ordinal) ||
            !readySignal.Contains("File.Move(temporaryPath, endpointPath, overwrite: true)",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live E2E child discovery does not persist and verify exact process start times.");
        }

        var devLoop = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "GPTino.DevLoop", "Program.cs"));
        if (!devLoop.Contains("ActiveCommand", StringComparison.Ordinal) ||
            !devLoop.Contains("SourceSnapshotSha256", StringComparison.Ordinal) ||
            !devLoop.Contains("SourceSnapshotLease", StringComparison.Ordinal) ||
            !devLoop.Contains("FileSystemWatcher", StringComparison.Ordinal) ||
            !devLoop.Contains("WaitForWatcherQuiescence", StringComparison.Ordinal) ||
            !devLoop.Contains("AcquireExclusiveStageLease", StringComparison.Ordinal) ||
            !devLoop.Contains("FileShare.None", StringComparison.Ordinal) ||
            !devLoop.Contains("The DevLoop stage lock is a reparse point", StringComparison.Ordinal) ||
            !devLoop.Contains("smoke-payload", StringComparison.Ordinal) ||
            !devLoop.Contains("-AgentHostExecutable", StringComparison.Ordinal) ||
            !devLoop.Contains("-SmokeBridgeExecutable", StringComparison.Ordinal) ||
            !devLoop.Contains("File.Move(temporaryPath, path, overwrite: true)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DevLoop does not durably bind active process ownership and source evidence.");
        }

        const string underscoreSecret = "underscore-secret-value";
        const string colonSecret = "colon-secret-value";
        var redacted = Redact(
            $"GPTINO_FUTURE_TOKEN={underscoreSecret} GPTino:FutureCredential={colonSecret}");
        if (redacted.Contains(underscoreSecret, StringComparison.Ordinal) ||
            redacted.Contains(colonSecret, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DevLoop output redaction does not cover every GPTino secret key form.");
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repositoryRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or ".references" or "artifacts" or "bin" or "obj" or "node_modules" or ".vs")
                {
                    continue;
                }
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is
                    ".cs" or ".ps1" or ".md" or ".yml" or ".yaml" or ".json" or
                    ".csproj" or ".props" or ".targets" or ".cmd" or ".bat" or ".sh")
                {
                    yield return file;
                }
            }
        }
    }

    private static string CreateErrorSignature(
        VerificationStage stage,
        CommandResult result,
        string repositoryRoot,
        string runRoot)
    {
        var stderr = File.ReadAllText(Path.Combine(runRoot, result.StderrLog));
        var stdout = File.ReadAllText(Path.Combine(runRoot, result.StdoutLog));
        var lines = (stderr + "\n" + stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var core = lines.FirstOrDefault(line => ErrorLineRegex().IsMatch(line)) ??
                   lines.LastOrDefault() ??
                   (result.TimedOut ? "timeout" : "no output");
        core = core.Replace(repositoryRoot, "<repo>", StringComparison.OrdinalIgnoreCase);
        core = GuidRegex().Replace(core, "<guid>");
        core = WhitespaceRegex().Replace(core, " ").Trim();
        return $"{stage.ToString().ToLowerInvariant()}|{result.Name}|{core}|exit={result.ExitCode}";
    }

    private static string Redact(string value)
    {
        value = GptinoSecretRegex().Replace(value, "$1<redacted>");
        value = BearerRegex().Replace(value, "$1<redacted>");
        return JsonSecretRegex().Replace(value, "$1<redacted>$2");
    }

    private static void WriteRunRecord(RunRecord run)
    {
        var path = Path.Combine(run.RunRoot, "run.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(run, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void KillExactProcessTreeIfAlive(
        Process process,
        DateTime processStartTimeUtc,
        string executablePath)
    {
        process.Refresh();
        if (process.HasExited)
        {
            return;
        }
        var actualExecutable = process.MainModule?.FileName;
        if (process.StartTime.ToUniversalTime() != processStartTimeUtc ||
            string.IsNullOrWhiteSpace(actualExecutable) ||
            !string.Equals(
                Path.GetFullPath(actualExecutable),
                Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to stop a verification process whose exact identity changed.");
        }
        process.Kill(entireProcessTree: true);
    }

    private static string? RecordCompletedSourceEvidence(
        RunRecord run,
        SourceSnapshotLease sourceLease)
    {
        try
        {
            var completedSnapshot = sourceLease.CaptureCompletion();
            run.CompletedSourceSnapshotSha256 = completedSnapshot.Sha256;
            run.CompletedSourceFileCount = completedSnapshot.FileCount;
            var issue = sourceLease.Mutation;
            if (!string.Equals(
                    run.SourceSnapshotSha256,
                    completedSnapshot.Sha256,
                    StringComparison.Ordinal) ||
                run.SourceFileCount != completedSnapshot.FileCount)
            {
                issue ??= "the completed source hash or file set differs from the leased start snapshot";
            }
            run.SourceEvidenceError = issue;
            return issue;
        }
        catch (Exception exception)
        {
            var issue = Redact(exception.GetType().Name + ": " + exception.Message);
            run.SourceEvidenceError = issue;
            return issue;
        }
    }

    private static SourceSnapshot CaptureUnlockedSourceSnapshot(string repositoryRoot)
    {
        var files = EnumerateSnapshotFiles(repositoryRoot)
            .OrderBy(
                path => Path.GetRelativePath(repositoryRoot, path),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            var length = new FileInfo(file).Length;
            var header = Encoding.UTF8.GetBytes(
                relative + "\0" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
            hash.AppendData(header);
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        return new SourceSnapshot(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), files.Length);
    }

    private static IEnumerable<string> EnumerateSnapshotFiles(string repositoryRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (SnapshotExcludedDirectories.Contains(Path.GetFileName(child)) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSnapshotDirectories(string repositoryRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.TryPop(out var directory))
        {
            yield return directory;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (SnapshotExcludedDirectories.Contains(Path.GetFileName(child)) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static bool IsSnapshotPath(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path));
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }
        return !relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(SnapshotExcludedDirectories.Contains);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Path.GetFullPath(Directory.GetCurrentDirectory()));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GPTino.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("GPTino.sln was not found above the current directory.");
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return File.Exists(executable)
                ? executable
                : throw new FileNotFoundException("A fixed verification executable was not found.", executable);
        }

        foreach (var value in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(item => item.Trim().Trim('"')))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(value, executable));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("A fixed verification executable was not found.", executable);
    }

    private static string ResolveLiveCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ResolveConfiguredCodex(configured);
        }

        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            ".sandbox-bin",
            "codex.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (var value in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(item => item.Trim().Trim('"')))
        {
            string directory;
            try
            {
                directory = Path.GetFullPath(value);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            var executable = Path.Combine(directory, "codex.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
            if (File.Exists(Path.Combine(directory, "codex.cmd")) &&
                ResolveNpmNativeExecutable(directory) is { } npmNative)
            {
                return npmNative;
            }
        }

        throw new FileNotFoundException(
            "Codex CLI was not found. Complete 'codex login' or set CODEX_EXECUTABLE to codex.exe (an npm codex.cmd shim is also accepted).");
    }

    private static string ResolveConfiguredCodex(string configured)
    {
        var path = Path.GetFullPath(configured);
        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        if (File.Exists(path) &&
            Path.GetFileName(path).StartsWith("codex.", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveNpmNativeExecutable(Path.GetDirectoryName(path)!)
                ?? throw new FileNotFoundException(
                    "The Codex npm shim exists, but its platform-native codex.exe was not found.",
                    path);
        }
        throw new FileNotFoundException(
            "The configured Codex executable was not found or is not codex.exe.",
            path);
    }

    private static string? ResolveNpmNativeExecutable(string npmDirectory)
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
            Path.Combine(
                packageRoot,
                "node_modules",
                "@openai",
                $"codex-win32-{packageSuffix}",
                "vendor",
                target,
                "bin",
                "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "bin", "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "codex.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void RemoveGptinoEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(IsGptinoEnvironmentKey)
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
    }

    private static bool IsGptinoEnvironmentKey(string key) =>
        key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("GPTINO:", StringComparison.OrdinalIgnoreCase);

    private static void AssertPathInsideRepository(string path, string repositoryRoot, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            AssertStrictDescendant(fullPath, root, label);
        }
    }

    private static void AssertStrictDescendant(string path, string root, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {label} escaped its allowed root.");
        }
    }

    private static void AssertExistingPathHasNoReparsePoint(string repositoryRoot, string path)
    {
        AssertPathInsideRepository(path, repositoryRoot, "artifact path");
        var current = new DirectoryInfo(Path.GetFullPath(path));
        var stop = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null &&
               !string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop,
                   StringComparison.OrdinalIgnoreCase))
        {
            current.Refresh();
            if (current.LinkTarget is not null ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidOperationException("A development artifact path contains a reparse point.");
            }
            current = current.Parent;
        }
    }

    [GeneratedRegex(@"(?i)\$home\b")]
    private static partial Regex PowerShellHomeVariableRegex();

    [GeneratedRegex(@"(?is)Remove-Item\b.{0,500}?-Recurse\b|-Recurse\b.{0,500}?Remove-Item\b")]
    private static partial Regex RecursiveRemoveItemRegex();

    [GeneratedRegex(@"Directory\.Delete\s*\([^;]*(?:recursive\s*:\s*true|,\s*true\s*\))")]
    private static partial Regex RecursiveDirectoryDeleteRegex();

    [GeneratedRegex(@"(?i)\b(?:rmdir|rd)\s+/s\b|\brm\s+-(?:rf|fr)\b")]
    private static partial Regex DestructiveShellDeleteRegex();

    [GeneratedRegex(@"(?i)<\s*(?:RemoveDir|Delete)\b")]
    private static partial Regex DestructiveMsBuildDeleteRegex();

    [GeneratedRegex(
        @"(?i)(GPTINO(?:_|:)[A-Z0-9_:]*(?:TOKEN|SECRET|KEY|PASSWORD|CREDENTIAL)[A-Z0-9_:]*\s*[:=]\s*)\S+")]
    private static partial Regex GptinoSecretRegex();

    [GeneratedRegex(@"(?i)(authorization\s*:\s*bearer\s+)\S+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)(\\\"(?:secret|token|apiKey)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)(^error\b|:\s*error\b|cannot find|failed|exception|timed out|does not match|refusing|missing)")]
    private static partial Regex ErrorLineRegex();

    private enum VerificationStage
    {
        Boundary,
        Mcp,
        Orchestrator,
        Full,
        Smoke,
        LiveCodex,
        Package,
        RhinoLive
    }

    private sealed record CommandSpec(
        string Name,
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        TimeSpan Timeout);

    private sealed record RunContext(RunRecord Run, SourceSnapshotLease SourceLease);

    private sealed class SourceSnapshotLease : IDisposable
    {
        private readonly string _repositoryRoot;
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly List<LeasedSourceFile> _files = [];
        private readonly object _mutationGate = new();
        private long _watcherEventSequence;
        private string? _mutation;
        private bool _disposed;

        private SourceSnapshotLease(string repositoryRoot)
        {
            _repositoryRoot = repositoryRoot;
        }

        public SourceSnapshot InitialSnapshot { get; private set; } = null!;

        public string? Mutation
        {
            get
            {
                lock (_mutationGate)
                {
                    return _mutation;
                }
            }
        }

        public static SourceSnapshotLease Acquire(string repositoryRoot)
        {
            var lease = new SourceSnapshotLease(repositoryRoot);
            try
            {
                lease.StartWatchers();
                foreach (var file in EnumerateSnapshotFiles(repositoryRoot)
                             .OrderBy(
                                 path => Path.GetRelativePath(repositoryRoot, path),
                                 StringComparer.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
                    lease._files.Add(new LeasedSourceFile(
                        relative,
                        new FileStream(
                            file,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            64 * 1024,
                            FileOptions.SequentialScan)));
                }
                lease.InitialSnapshot = lease.HashLeasedFiles();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public SourceSnapshot CaptureCompletion()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WaitForWatcherQuiescence();
            var snapshot = CaptureUnlockedSourceSnapshot(_repositoryRoot);
            WaitForWatcherQuiescence();
            return snapshot;
        }

        private void WaitForWatcherQuiescence()
        {
            var total = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            var observedSequence = Interlocked.Read(ref _watcherEventSequence);
            while (quiet.Elapsed < TimeSpan.FromMilliseconds(500))
            {
                if (total.Elapsed >= TimeSpan.FromSeconds(3))
                {
                    MarkMutation("source watcher did not become quiescent before evidence capture");
                    return;
                }
                Thread.Sleep(25);
                var currentSequence = Interlocked.Read(ref _watcherEventSequence);
                if (currentSequence != observedSequence)
                {
                    observedSequence = currentSequence;
                    quiet.Restart();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            foreach (var file in _files)
            {
                file.Stream.Dispose();
            }
        }

        private void StartWatchers()
        {
            foreach (var directory in EnumerateSnapshotDirectories(_repositoryRoot))
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                        NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                _watchers.Add(watcher);
            }
        }

        private SourceSnapshot HashLeasedFiles()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            foreach (var file in _files)
            {
                file.Stream.Position = 0;
                var header = Encoding.UTF8.GetBytes(
                    file.RelativePath + "\0" +
                    file.Stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
                hash.AppendData(header);
                int read;
                while ((read = file.Stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }
            return new SourceSnapshot(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                _files.Count);
        }

        private void OnChanged(object sender, FileSystemEventArgs eventArgs)
        {
            try
            {
                if (IsSnapshotPath(_repositoryRoot, eventArgs.FullPath))
                {
                    MarkMutation(
                        $"{eventArgs.ChangeType}: " +
                        Path.GetRelativePath(_repositoryRoot, eventArgs.FullPath).Replace('\\', '/'));
                }
            }
            catch (Exception exception)
            {
                MarkMutation("source watcher could not classify an event: " + exception.GetType().Name);
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs eventArgs)
        {
            OnChanged(sender, eventArgs);
            try
            {
                if (IsSnapshotPath(_repositoryRoot, eventArgs.OldFullPath))
                {
                    MarkMutation(
                        "RenamedFrom: " +
                        Path.GetRelativePath(_repositoryRoot, eventArgs.OldFullPath).Replace('\\', '/'));
                }
            }
            catch (Exception exception)
            {
                MarkMutation("source watcher could not classify a rename: " + exception.GetType().Name);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs eventArgs) =>
            MarkMutation("source watcher failed: " + eventArgs.GetException().GetType().Name);

        private void MarkMutation(string message)
        {
            Interlocked.Increment(ref _watcherEventSequence);
            lock (_mutationGate)
            {
                _mutation ??= message;
            }
        }

        private sealed record LeasedSourceFile(string RelativePath, FileStream Stream);
    }

    private sealed class RunRecord
    {
        public required string RunId { get; init; }
        public required string Stage { get; init; }
        public required string RunRoot { get; init; }
        public required string RepositoryRoot { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public required string SourceSnapshotSha256 { get; init; }
        public required int SourceFileCount { get; init; }
        public string? CompletedSourceSnapshotSha256 { get; set; }
        public int? CompletedSourceFileCount { get; set; }
        public string? SourceEvidenceError { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public required string Status { get; set; }
        public string? FatalError { get; set; }
        public ActiveCommandRecord? ActiveCommand { get; set; }
        public List<CommandResult> Commands { get; } = [];
    }

    private sealed record SourceSnapshot(string Sha256, int FileCount);

    private sealed class ActiveCommandRecord
    {
        public required string Name { get; init; }
        public required string ExecutablePath { get; init; }
        public required int ProcessId { get; init; }
        public required DateTime ProcessStartTimeUtc { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
    }

    private sealed class CommandResult
    {
        public required string Name { get; init; }
        public required string Executable { get; init; }
        public required string[] Arguments { get; init; }
        public required string WorkingDirectory { get; init; }
        public required int ProcessId { get; init; }
        public required DateTime ProcessStartTimeUtc { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public required DateTimeOffset CompletedAtUtc { get; init; }
        public required int ExitCode { get; init; }
        public required bool TimedOut { get; init; }
        public required string StdoutLog { get; init; }
        public required string StderrLog { get; init; }
        public string? ErrorSignature { get; set; }
    }
}
