using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GPTino.LiveE2E;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(args[0], "verify", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--run-root", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Usage: GPTino.LiveE2E verify --run-root <DevLoop-run-root>");
            return 2;
        }

        LiveHarness? harness = null;
        var exitCode = 1;
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var runRoot = ValidateRunRoot(repositoryRoot, args[2]);
            harness = new LiveHarness(repositoryRoot, runRoot);
            await harness.RunAsync().ConfigureAwait(false);
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GPTino live E2E failed: {exception.Message}");
            if (harness is not null)
            {
                await harness.RecordFailureAsync(exception).ConfigureAwait(false);
            }
            return 1;
        }
        finally
        {
            if (harness is not null)
            {
                await harness.DisposeAsync().ConfigureAwait(false);
            }
        }
        return exitCode == 0 && harness?.IsPassed == true ? 0 : 1;
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
        throw new DirectoryNotFoundException("GPTino.sln was not found above the working directory.");
    }

    private static string ValidateRunRoot(string repositoryRoot, string value)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "dev-loop"));
        var runRoot = Path.GetFullPath(value);
        RequireStrictDescendant(runRoot, allowedRoot, "live run root");
        if (!File.Exists(Path.Combine(runRoot, ".gptino-owned-run")))
        {
            throw new InvalidOperationException("The live run root has no DevLoop ownership marker.");
        }
        RejectReparsePoints(repositoryRoot, runRoot);
        return runRoot;
    }

    private static void RequireStrictDescendant(string path, string root, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {label} escaped its allowed root.");
        }
    }

    private static void RejectReparsePoints(string repositoryRoot, string path)
    {
        var stop = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null &&
               !string.Equals(
                   current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                   stop,
                   StringComparison.OrdinalIgnoreCase))
        {
            current.Refresh();
            if (current.LinkTarget is not null ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidOperationException("A live E2E path contains a reparse point.");
            }
            current = current.Parent;
        }
    }

    private static void RejectFileReparsePoint(string path, string label)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (info.LinkTarget is not null ||
            (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidOperationException($"The {label} is a reparse point.");
        }
    }

    private static int GetParentProcessId(Process process)
    {
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"Could not read the parent identity for process {process.Id} (NTSTATUS 0x{status:X8}).");
        }
        return checked((int)information.InheritedFromUniqueProcessId.ToInt64());
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private sealed class LiveHarness : IAsyncDisposable
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BridgeTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan QueueTimeout = TimeSpan.FromMinutes(5);
        private const int MaximumApiResponseBytes = 16 * 1024 * 1024;
        private const int MessagePageSize = 250;
        private const int MaximumMessagePages = 20;

        private readonly string _repositoryRoot;
        private readonly string _runRoot;
        private readonly string _evidenceRoot;
        private readonly string _runtimeRoot;
        private readonly string _rhinoSource;
        private readonly string _grasshopperSource;
        private readonly string _rhinoCopy;
        private readonly string _grasshopperCopy;
        private readonly string _stagedPackageRuntimeRoot;
        private readonly string _stagedAgentHostExecutable;
        private readonly string _stagedTerminalExecutable;
        private readonly string _installedPackageRoot;
        private readonly string _rhinoExecutable = @"C:\Program Files\Rhino 8\System\Rhino.exe";
        private readonly string _yakExecutable = @"C:\Program Files\Rhino 8\System\Yak.exe";
        private readonly string _codexExecutable;
        private readonly LiveRunReport _report;
        private FileEvidence? _rhinoBaseline;
        private FileEvidence? _grasshopperBaseline;
        private Process? _rhinoProcess;
        private OwnedProcess? _rhinoIdentity;
        private OwnedProcess? _agentIdentity;
        private OwnedProcess? _terminalIdentity;
        private HttpClient? _api;
        private string? _apiToken;
        private bool _disposed;
        private bool _failureRecorded;

        public LiveHarness(string repositoryRoot, string runRoot)
        {
            _repositoryRoot = repositoryRoot;
            _runRoot = runRoot;
            _evidenceRoot = Path.Combine(runRoot, "live-e2e");
            _runtimeRoot = Path.Combine(runRoot, "runtime");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _rhinoSource = Path.Combine(desktop, "Untitled.3dm");
            _grasshopperSource = Path.Combine(desktop, "unnamed.gh");
            var modelRoot = Path.Combine(_evidenceRoot, "models");
            _rhinoCopy = Path.Combine(modelRoot, "GPTino-E2E.3dm");
            _grasshopperCopy = Path.Combine(modelRoot, "GPTino-E2E.gh");
            _stagedPackageRuntimeRoot = Path.Combine(
                runRoot,
                "package",
                "yak",
                "GPTino",
                "net8.0");
            var stagedAgentRoot = Path.Combine(_stagedPackageRuntimeRoot, "agent");
            _stagedAgentHostExecutable = Path.Combine(stagedAgentRoot, "GPTino.AgentHost.exe");
            _stagedTerminalExecutable = Path.Combine(stagedAgentRoot, "GPTino.Terminal.exe");
            _installedPackageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "McNeel",
                "Rhinoceros",
                "packages",
                "8.0",
                "GPTino");
            _codexExecutable = ResolveCodexExecutable();
            _report = new LiveRunReport
            {
                RunRoot = runRoot,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = "running"
            };
        }

        public bool IsPassed => string.Equals(_report.Status, "passed", StringComparison.Ordinal);

        public async Task RunAsync()
        {
            RequireWindowsAndFixedInputs();
            CreateOwnedEvidenceRoot();
            await WriteReportAsync().ConfigureAwait(false);
            CopySourceModels();
            EnsureNoRhinoIsRunning();
            await InstallPackageAsync().ConfigureAwait(false);
            EnsureNoRhinoIsRunning();
            await LaunchRhinoAsync().ConfigureAwait(false);
            var endpoint = await WaitForEndpointAsync().ConfigureAwait(false);
            ConfigureApi(endpoint.BaseUri);
            await WaitForBridgeAsync(endpoint.ProcessId).ConfigureAwait(false);
            await VerifyBoundCopiesAsync().ConfigureAwait(false);

            var ids = new GeometryIds(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid());
            _report.GeometryIds = ids;

            var grasshopperSession = await CreateSessionAsync("E2E Grasshopper Cylinder")
                .ConfigureAwait(false);
            await SetModelProfileAsync(grasshopperSession, "deep").ConfigureAwait(false);
            await RunWithRepairsAsync(
                grasshopperSession,
                BuildGrasshopperPrompt(ids),
                "Repair the Grasshopper cylinder so every stated invariant is exact.",
                () => VerifyGrasshopperAsync(ids),
                "grasshopper-cylinder").ConfigureAwait(false);
            await RequireEffectiveProfileAsync(grasshopperSession, "HighAssurance")
                .ConfigureAwait(false);

            var rhinoSession = await CreateSessionAsync("E2E Rhino Sphere").ConfigureAwait(false);
            await SetModelProfileAsync(rhinoSession, "deep").ConfigureAwait(false);
            await RunWithRepairsAsync(
                rhinoSession,
                BuildRhinoPrompt(ids.SphereObjectId),
                "Repair the Rhino sphere so its identity, attributes, and dimensions are exact.",
                () => VerifyRhinoSphereAsync(ids.SphereObjectId),
                "rhino-sphere").ConfigureAwait(false);
            await RequireEffectiveProfileAsync(rhinoSession, "HighAssurance")
                .ConfigureAwait(false);

            await VerifyPriorityAndConflictAsync(ids.PythonObjectId).ConfigureAwait(false);
            await VerifyParallelReadsAsync(ids.PythonObjectId).ConfigureAwait(false);
            await VerifyTerminalLaunchAsync(grasshopperSession).ConfigureAwait(false);

            _report.Status = "passed";
            _report.CompletedAtUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync().ConfigureAwait(false);
            Console.WriteLine("GPTino live Rhino/Grasshopper E2E passed.");
        }

        public async Task RecordFailureAsync(Exception exception)
        {
            if (_failureRecorded)
            {
                return;
            }
            _failureRecorded = true;
            _report.Status = "failed";
            _report.Error = exception.GetType().Name + ": " + exception.Message;
            _report.CompletedAtUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Exception? shutdownFailure = null;
            try
            {
                await StopOwnedProcessesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                shutdownFailure = exception;
                _report.Steps.Add("Owned-process shutdown failed: " + exception.Message);
            }

            try
            {
                VerifyOriginalSourcesUnchanged();
            }
            catch (Exception exception)
            {
                shutdownFailure = shutdownFailure is null
                    ? exception
                    : new AggregateException(shutdownFailure, exception);
                _report.Steps.Add("Original source verification failed: " + exception.Message);
            }

            _api?.Dispose();
            _api = null;
            if (shutdownFailure is not null && _report.Status == "passed")
            {
                _report.Status = "failed";
                _report.Error = shutdownFailure.GetType().Name + ": " + shutdownFailure.Message;
            }
            _report.CompletedAtUtc ??= DateTimeOffset.UtcNow;
            await WriteReportAsync().ConfigureAwait(false);
        }

        private void RequireWindowsAndFixedInputs()
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The live E2E harness requires Windows.");
            }
            foreach (var path in new[]
                     {
                         _rhinoSource, _grasshopperSource, _rhinoExecutable,
                         _yakExecutable, _codexExecutable,
                         _stagedAgentHostExecutable, _stagedTerminalExecutable
                     })
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("A fixed live E2E input is missing.", path);
                }
            }
            if (!Directory.Exists(_stagedPackageRuntimeRoot))
            {
                throw new DirectoryNotFoundException(
                    $"The staged package runtime payload is missing: {_stagedPackageRuntimeRoot}");
            }
        }

        private static string ResolveCodexExecutable()
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
                "Codex CLI was not found. Complete 'codex login' or set CODEX_EXECUTABLE to codex.exe.");
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

        private void CreateOwnedEvidenceRoot()
        {
            RequireStrictDescendant(_evidenceRoot, _runRoot, "live evidence root");
            var modelRoot = Path.GetDirectoryName(_rhinoCopy)
                ?? throw new InvalidOperationException("The live model root is invalid.");
            RejectReparsePoints(_repositoryRoot, _evidenceRoot);
            RejectReparsePoints(_repositoryRoot, modelRoot);
            Directory.CreateDirectory(_evidenceRoot);
            RejectReparsePoints(_repositoryRoot, _evidenceRoot);
            Directory.CreateDirectory(modelRoot);
            RejectReparsePoints(_repositoryRoot, modelRoot);
            File.WriteAllText(
                Path.Combine(_evidenceRoot, ".gptino-owned-live-e2e"),
                "GPTino live E2E owned evidence\n",
                new UTF8Encoding(false));
        }

        private void CopySourceModels()
        {
            _rhinoBaseline = ReadEvidence(_rhinoSource);
            _grasshopperBaseline = ReadEvidence(_grasshopperSource);
            File.Copy(_rhinoSource, _rhinoCopy, overwrite: false);
            File.Copy(_grasshopperSource, _grasshopperCopy, overwrite: false);
            RequireContentEqual(_rhinoBaseline, ReadEvidence(_rhinoCopy), "Rhino model copy");
            RequireContentEqual(
                _grasshopperBaseline,
                ReadEvidence(_grasshopperCopy),
                "Grasshopper model copy");
            File.WriteAllText(
                Path.Combine(_evidenceRoot, "source-baseline.json"),
                JsonSerializer.Serialize(
                    new { rhino = _rhinoBaseline, grasshopper = _grasshopperBaseline },
                    JsonOptions),
                new UTF8Encoding(false));
            _report.Steps.Add("Copied fixed Desktop source models into the owned run directory.");
        }

        private async Task InstallPackageAsync()
        {
            var packageRoot = Path.Combine(_runRoot, "package");
            RequireStrictDescendant(packageRoot, _runRoot, "package root");
            var packages = Directory.Exists(packageRoot)
                ? Directory.EnumerateFiles(packageRoot, "*.yak", SearchOption.AllDirectories).ToArray()
                : [];
            if (packages.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one run-owned Yak package, found {packages.Length}.");
            }

            var listed = await RunProcessAsync(
                "yak-list",
                _yakExecutable,
                ["list"],
                ProcessTimeout).ConfigureAwait(false);
            if (listed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.TrimStart().StartsWith("GPTino ", StringComparison.OrdinalIgnoreCase)))
            {
                await RunProcessAsync(
                    "yak-uninstall-gptino",
                    _yakExecutable,
                    ["uninstall", "GPTino"],
                    ProcessTimeout).ConfigureAwait(false);
            }
            await RunProcessAsync(
                "yak-install-gptino",
                _yakExecutable,
                ["install", packages[0]],
                ProcessTimeout).ConfigureAwait(false);
            _report.PackageSha256 = ReadEvidence(packages[0]).Sha256;
            _report.Steps.Add("Installed the single run-owned GPTino Yak package.");
        }

        private static void EnsureNoRhinoIsRunning()
        {
            using var existing = new ProcessCollection(Process.GetProcessesByName("Rhino"));
            if (existing.Items.Count != 0)
            {
                throw new InvalidOperationException(
                    "Refusing live E2E because an existing Rhino process is already running.");
            }
        }

        private async Task LaunchRhinoAsync()
        {
            _apiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var startInfo = new ProcessStartInfo
            {
                FileName = _rhinoExecutable,
                WorkingDirectory = _evidenceRoot,
                UseShellExecute = false
            };
            RemoveGptinoEnvironment(startInfo);
            // Rhino's /runscript parser requires one raw argument string. Array-style process
            // arguments cause Rhino 8 to ignore the script, and nested file-path quotes must be
            // doubled inside the quoted startup macro.
            RhinoLaunchArguments.Configure(startInfo, _grasshopperCopy, _rhinoCopy);
            startInfo.Environment["GPTINO_DEV_MODE"] = "1";
            startInfo.Environment["GPTINO_DEV_DATA_DIRECTORY"] = _runtimeRoot;
            startInfo.Environment["GPTINO_API_TOKEN"] = _apiToken;
            startInfo.Environment["CODEX_EXECUTABLE"] = _codexExecutable;

            _rhinoProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Rhino did not start.");
            _rhinoIdentity = CaptureOwnedProcess(_rhinoProcess, _rhinoExecutable);
            RequireExecutable(_rhinoIdentity, _rhinoExecutable, "Rhino");
            _report.RhinoProcessId = _rhinoIdentity.ProcessId;
            _report.OwnedProcesses.Add(_rhinoIdentity);
            _report.Steps.Add($"Started one owned Rhino process {_rhinoIdentity.ProcessId}.");
            await WriteReportAsync().ConfigureAwait(false);
        }

        private async Task<EndpointEvidence> WaitForEndpointAsync()
        {
            var path = Path.Combine(_runtimeRoot, "endpoint.json");
            var deadline = DateTimeOffset.UtcNow + BridgeTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_rhinoProcess?.HasExited == true)
                {
                    throw new InvalidOperationException(
                        $"Rhino exited before AgentHost became ready (code {_rhinoProcess.ExitCode}).");
                }
                if (File.Exists(path))
                {
                    RejectReparsePoints(_repositoryRoot, path);
                    RejectFileReparsePoint(path, "AgentHost endpoint discovery file");
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
                        var root = document.RootElement;
                        var uri = new Uri(root.GetProperty("uiBaseUrl").GetString()!, UriKind.Absolute);
                        var processId = root.GetProperty("processId").GetInt32();
                        var processStartTimeUtc = root.GetProperty("processStartTimeUtc")
                            .GetDateTime()
                            .ToUniversalTime();
                        var startedAt = root.GetProperty("startedAt").GetDateTimeOffset();
                        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback ||
                            startedAt < _rhinoIdentity!.StartedAtUtc.AddSeconds(-2))
                        {
                            throw new InvalidDataException("AgentHost endpoint evidence is not owned by this live run.");
                        }
                        using var process = Process.GetProcessById(processId);
                        var identity = CaptureOwnedProcess(process);
                        if (identity.StartedAtUtc != processStartTimeUtc ||
                            identity.StartedAtUtc < _rhinoIdentity.StartedAtUtc.AddSeconds(-2) ||
                            startedAt < new DateTimeOffset(identity.StartedAtUtc, TimeSpan.Zero) ||
                            GetParentProcessId(process) != _rhinoIdentity.ProcessId ||
                            !MatchesOwnedProcess(_rhinoProcess!, _rhinoIdentity) ||
                            !string.Equals(identity.Name, "GPTino.AgentHost", StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(
                                Path.GetFileName(identity.ExecutablePath),
                                "GPTino.AgentHost.exe",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Endpoint process is not GPTino.AgentHost.");
                        }
                        RequireStrictDescendant(
                            identity.ExecutablePath,
                            _installedPackageRoot,
                            "installed AgentHost executable");
                        var installedAgentRoot = Path.GetDirectoryName(identity.ExecutablePath)
                            ?? throw new InvalidDataException("Installed AgentHost directory is missing.");
                        var installedRuntimeRoot = Directory.GetParent(installedAgentRoot)?.FullName
                            ?? throw new InvalidDataException("Installed package runtime directory is missing.");
                        RequireStrictDescendant(
                            installedRuntimeRoot,
                            _installedPackageRoot,
                            "installed package runtime payload");
                        var stagedPayload = ReadDirectoryEvidence(_stagedPackageRuntimeRoot);
                        var installedPayload = ReadDirectoryEvidence(installedRuntimeRoot);
                        RequireDirectoryContentEqual(
                            stagedPayload,
                            installedPayload,
                            "installed package runtime payload");
                        _report.StagedRuntimePayloadSha256 = stagedPayload.Sha256;
                        _report.InstalledRuntimePayloadSha256 = installedPayload.Sha256;
                        _report.RuntimePayloadFileCount = installedPayload.FileCount;
                        _agentIdentity = identity;
                        _report.AgentHostProcessId = processId;
                        _report.OwnedProcesses.Add(identity);
                        await WriteReportAsync().ConfigureAwait(false);
                        return new EndpointEvidence(uri, processId, processStartTimeUtc, startedAt);
                    }
                    catch (Exception exception) when (_agentIdentity is null &&
                        exception is (IOException or JsonException or InvalidOperationException or ArgumentException or
                            KeyNotFoundException or System.ComponentModel.Win32Exception))
                    {
                    }
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            throw new TimeoutException("AgentHost endpoint.json did not become valid in time.");
        }

        private void ConfigureApi(Uri baseUri)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            _api = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute),
                Timeout = TimeSpan.FromMinutes(2)
            };
            _api.DefaultRequestHeaders.Add("X-GPTino-Token", _apiToken);
        }

        private async Task WaitForBridgeAsync(int expectedAgentProcessId)
        {
            var deadline = DateTimeOffset.UtcNow + BridgeTimeout;
            string? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var health = await SendJsonAsync(HttpMethod.Get, "api/v1/health").ConfigureAwait(false);
                    last = health.GetRawText();
                    if (health.GetProperty("processId").GetInt32() != expectedAgentProcessId)
                    {
                        throw new InvalidDataException("Health response came from a different AgentHost process.");
                    }
                    if (health.GetProperty("bridgeConnected").GetBoolean())
                    {
                        _report.Steps.Add("Authenticated AgentHost and explicit document bridge are connected.");
                        return;
                    }
                }
                catch (HttpRequestException exception)
                {
                    last = exception.Message;
                }
                await Task.Delay(750).ConfigureAwait(false);
            }
            throw new TimeoutException($"Document bridge did not connect in time. Last health: {last}");
        }

        private async Task VerifyBoundCopiesAsync()
        {
            var runtime = await ReadRuntimeAsync().ConfigureAwait(false);
            RequirePathEqual(runtime.GetProperty("rhinoFile").GetString(), _rhinoCopy, "Rhino");
            RequirePathEqual(runtime.GetProperty("grasshopperFile").GetString(), _grasshopperCopy, "Grasshopper");
            if (!string.Equals(runtime.GetProperty("health").GetString(), "connected", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Runtime is not connected to the copied document pair.");
            }
        }

        private async Task<Guid> CreateSessionAsync(string name)
        {
            var response = await SendJsonAsync(
                HttpMethod.Post,
                "api/v1/sessions",
                new { name, role = "modeler", modelProfile = "auto" }).ConfigureAwait(false);
            var id = response.GetProperty("id").GetGuid();
            _report.SessionIds.Add(id);
            return id;
        }

        private Task SetModelProfileAsync(Guid sessionId, string profile) =>
            SendNoContentAsync(
                HttpMethod.Put,
                $"api/v1/sessions/{sessionId:D}/model",
                new { modelProfile = profile });

        private async Task RunWithRepairsAsync(
            Guid sessionId,
            string initialPrompt,
            string repairInstruction,
            Func<Task> verifier,
            string label)
        {
            InvalidDataException? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var prompt = attempt == 1
                    ? initialPrompt
                    : $"{repairInstruction}\n" +
                      "The independent verifier reports that one or more stated live-state invariants are still false.\n" +
                      "Inspect the current bound documents, correct only this task, and verify it again before replying.";
                await SendPromptAndWaitAsync(sessionId, prompt, $"{label}-{attempt}")
                    .ConfigureAwait(false);
                try
                {
                    await verifier().ConfigureAwait(false);
                    _report.Steps.Add($"{label} passed on attempt {attempt}.");
                    return;
                }
                catch (InvalidDataException exception)
                {
                    last = exception;
                    _report.Steps.Add(
                        $"{label} attempt {attempt} failed independent verification: " +
                        Truncate(exception.Message, 500));
                }
            }
            throw new InvalidOperationException($"{label} failed after three bounded attempts.", last);
        }

        private async Task SendPromptAndWaitAsync(Guid sessionId, string prompt, string clientMessageId)
        {
            var before = await SendJsonAsync(
                HttpMethod.Get,
                $"api/v1/sessions/{sessionId:D}/messages?after=0&limit=1").ConfigureAwait(false);
            var baselineMessageId = before.EnumerateArray()
                .Select(item => item.GetProperty("id").GetInt64())
                .DefaultIfEmpty(0)
                .Max();
            await SendJsonAsync(
                HttpMethod.Post,
                $"api/v1/sessions/{sessionId:D}/messages",
                new { content = prompt, clientMessageId }).ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow + TurnTimeout;
            IReadOnlyList<JsonElement> messages = [];
            while (DateTimeOffset.UtcNow < deadline)
            {
                var runtime = await ReadRuntimeAsync().ConfigureAwait(false);
                var session = FindSession(runtime, sessionId);
                messages = await ReadMessagesAfterAsync(sessionId, baselineMessageId).ConfigureAwait(false);
                var hasAssistant = messages.Any(item =>
                    string.Equals(item.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal));
                var error = messages.LastOrDefault(item =>
                    string.Equals(item.GetProperty("role").GetString(), "system", StringComparison.Ordinal) &&
                    item.TryGetProperty("phase", out var phase) &&
                    string.Equals(phase.GetString(), "error", StringComparison.Ordinal));
                var status = session.GetProperty("status").GetString();
                if (error.ValueKind != JsonValueKind.Undefined || string.Equals(status, "blocked", StringComparison.Ordinal))
                {
                    await SaveSessionMessagesAsync(sessionId, messages).ConfigureAwait(false);
                    var detail = error.ValueKind == JsonValueKind.Undefined
                        ? "Session entered blocked state."
                        : error.GetProperty("content").GetString();
                    throw new InvalidOperationException(detail);
                }
                if (hasAssistant && string.Equals(status, "idle", StringComparison.Ordinal))
                {
                    await SaveSessionMessagesAsync(sessionId, messages).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(1_000).ConfigureAwait(false);
            }
            await SaveSessionMessagesAsync(sessionId, messages).ConfigureAwait(false);
            throw new TimeoutException($"Codex session {sessionId:D} did not finish in time.");
        }

        private async Task<IReadOnlyList<JsonElement>> ReadMessagesAfterAsync(
            Guid sessionId,
            long afterMessageId)
        {
            var messages = new List<JsonElement>();
            var cursor = afterMessageId;
            for (var page = 0; page < MaximumMessagePages; page++)
            {
                var response = await SendJsonAsync(
                    HttpMethod.Get,
                    $"api/v1/sessions/{sessionId:D}/messages?after={cursor}&limit={MessagePageSize}")
                    .ConfigureAwait(false);
                var items = response.EnumerateArray().Select(item => item.Clone()).ToArray();
                messages.AddRange(items);
                if (items.Length < MessagePageSize)
                {
                    return messages;
                }
                cursor = items[^1].GetProperty("id").GetInt64();
            }
            throw new InvalidDataException(
                $"Session {sessionId:D} exceeded the bounded message pagination budget.");
        }

        private async Task SaveSessionMessagesAsync(
            Guid sessionId,
            IReadOnlyList<JsonElement> messages)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_evidenceRoot, $"session-{sessionId:N}.json"),
                JsonSerializer.Serialize(messages, JsonOptions),
                new UTF8Encoding(false)).ConfigureAwait(false);
        }

        private async Task VerifyGrasshopperAsync(GeometryIds ids)
        {
            var snapshot = await SendJsonAsync(HttpMethod.Get, "api/v1/dev/snapshot")
                .ConfigureAwait(false);
            var canvas = snapshot.GetProperty("canvas");
            var objects = canvas.GetProperty("objects");
            var diameter = FindObject(objects, ids.DiameterSliderId);
            var height = FindObject(objects, ids.HeightSliderId);
            var python = FindObject(objects, ids.PythonObjectId);
            RequireSlider(diameter, "GPTinoE2E_Diameter", 10m, 0m, 100m, 0);
            RequireSlider(height, "GPTinoE2E_Height", 20m, 0m, 100m, 0);
            if (!string.Equals(python.GetProperty("nickName").GetString(), "GPTinoE2E_Cylinder", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Python component nickname is not exact.");
            }

            var diameterOutput = diameter.GetProperty("outputs")[0].GetProperty("parameterId").GetGuid();
            var heightOutput = height.GetProperty("outputs")[0].GetProperty("parameterId").GetGuid();
            var inputs = python.GetProperty("inputs");
            var diameterInput = FindParameter(inputs, "Diameter").GetProperty("parameterId").GetGuid();
            var heightInput = FindParameter(inputs, "Height").GetProperty("parameterId").GetGuid();
            var wires = canvas.GetProperty("wires");
            RequireWire(wires, ids.DiameterSliderId, diameterOutput, ids.PythonObjectId, diameterInput);
            RequireWire(wires, ids.HeightSliderId, heightOutput, ids.PythonObjectId, heightInput);

            var pythonInspection = await SendJsonAsync(
                HttpMethod.Get,
                $"api/v1/dev/grasshopper/{ids.PythonObjectId:D}/python").ConfigureAwait(false);
            var sourceInspection = RequireSingle(
                pythonInspection.GetProperty("inspections"),
                item => string.Equals(
                    item.GetProperty("operation").GetString(),
                    "python.inspect",
                    StringComparison.Ordinal),
                "python.inspect result");
            var source = sourceInspection.GetProperty("result").GetProperty("source").GetString() ?? string.Empty;
            if (!source.Contains("GPTINO_E2E_CYLINDER", StringComparison.Ordinal) ||
                !source.Contains("Diameter", StringComparison.Ordinal) ||
                !source.Contains("Height", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Executable Python source is not the requested cylinder source.");
            }
            RequireNoRuntimeErrors(sourceInspection.GetProperty("result").GetProperty("runtimeMessages"));
            var messagesInspection = RequireSingle(
                pythonInspection.GetProperty("inspections"),
                item => string.Equals(
                    item.GetProperty("operation").GetString(),
                    "python.runtimeMessages",
                    StringComparison.Ordinal),
                "python.runtimeMessages result");
            RequireNoRuntimeErrors(messagesInspection.GetProperty("result"));

            var output = await SendJsonAsync(
                HttpMethod.Get,
                $"api/v1/dev/grasshopper/{ids.PythonObjectId:D}/outputs").ConfigureAwait(false);
            var outputResult = output.GetProperty("result");
            var cylinder = RequireSingle(
                outputResult.GetProperty("outputs"),
                item => string.Equals(
                    item.GetProperty("nickName").GetString(),
                    "Cylinder",
                    StringComparison.Ordinal),
                "Cylinder output");
            if (cylinder.GetProperty("dataCount").GetInt32() < 1 ||
                cylinder.GetProperty("geometryBounds").ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Python Cylinder output contains no inspectable geometry.");
            }
            var size = cylinder.GetProperty("geometryBounds").GetProperty("size");
            RequireClose(size.GetProperty("x").GetDouble(), 10, 0.05, "Cylinder width X");
            RequireClose(size.GetProperty("y").GetDouble(), 10, 0.05, "Cylinder width Y");
            RequireClose(size.GetProperty("z").GetDouble(), 20, 0.05, "Cylinder height Z");
        }

        private async Task VerifyRhinoSphereAsync(Guid sphereObjectId)
        {
            var response = await SendJsonAsync(HttpMethod.Get, "api/v1/dev/rhino-objects")
                .ConfigureAwait(false);
            var objects = response.GetProperty("result").GetProperty("objects");
            var sphere = RequireSingle(
                objects,
                item => item.GetProperty("objectId").GetGuid() == sphereObjectId,
                "Rhino E2E sphere");
            if (!string.Equals(sphere.GetProperty("logicalEntityId").GetString(), "gptino-e2e-sphere", StringComparison.Ordinal) ||
                !string.Equals(sphere.GetProperty("name").GetString(), "GPTinoE2E_Sphere", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Rhino sphere identity or name is not exact.");
            }
            var bounds = sphere.GetProperty("bounds");
            var size = bounds.GetProperty("size");
            var center = bounds.GetProperty("center");
            RequireClose(size.GetProperty("x").GetDouble(), 6, 0.01, "Sphere size X");
            RequireClose(size.GetProperty("y").GetDouble(), 6, 0.01, "Sphere size Y");
            RequireClose(size.GetProperty("z").GetDouble(), 6, 0.01, "Sphere size Z");
            RequireClose(center.GetProperty("x").GetDouble(), 0, 0.01, "Sphere center X");
            RequireClose(center.GetProperty("y").GetDouble(), 0, 0.01, "Sphere center Y");
            RequireClose(center.GetProperty("z").GetDouble(), 0, 0.01, "Sphere center Z");
        }

        private async Task VerifyPriorityAndConflictAsync(Guid componentId)
        {
            var before = await ReadRuntimeAsync().ConfigureAwait(false);
            var baselineConflictIds = before.GetProperty("conflicts").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            await SendNoContentAsync(
                HttpMethod.Put,
                "api/v1/dev/writer/pause",
                new { paused = true }).ConfigureAwait(false);
            try
            {
                var lower = await CreateSessionAsync("E2E Move Lower Priority").ConfigureAwait(false);
                var preferred = await CreateSessionAsync("E2E Move Preferred").ConfigureAwait(false);
                await SetModelProfileAsync(lower, "fast").ConfigureAwait(false);
                await SetModelProfileAsync(preferred, "fast").ConfigureAwait(false);
                var lowerTask = SendPromptAndWaitAsync(
                    lower,
                    BuildMovePrompt(componentId, 640, 220),
                    "move-lower");
                var preferredTask = SendPromptAndWaitAsync(
                    preferred,
                    BuildMovePrompt(componentId, 820, 180),
                    "move-preferred");
                await Task.WhenAll(lowerTask, preferredTask).ConfigureAwait(false);

                var runtime = await ReadRuntimeAsync().ConfigureAwait(false);
                var queued = runtime.GetProperty("queue").EnumerateArray()
                    .Where(item =>
                        item.GetProperty("sessionId").GetGuid() == lower ||
                        item.GetProperty("sessionId").GetGuid() == preferred)
                    .ToArray();
                if (queued.Length != 2)
                {
                    throw new InvalidDataException(
                        $"Expected two paused writer jobs, found {queued.Length}.");
                }
                var lowerJobs = queued.Where(item => item.GetProperty("sessionId").GetGuid() == lower).ToArray();
                var preferredJobs = queued.Where(item => item.GetProperty("sessionId").GetGuid() == preferred).ToArray();
                if (lowerJobs.Length != 1 || preferredJobs.Length != 1)
                {
                    throw new InvalidDataException("Each priority session must own exactly one queued writer job.");
                }
                var lowerJobId = lowerJobs[0].GetProperty("id").GetGuid();
                var preferredJobId = preferredJobs[0].GetProperty("id").GetGuid();
                var expectedResource = $"GrasshopperComponentLayout:{componentId:D}:*";
                var activePairConflict = runtime.GetProperty("conflicts").EnumerateArray().Any(item =>
                {
                    var conflictId = item.GetProperty("id").GetString();
                    var sessions = item.GetProperty("sessionIds").EnumerateArray()
                        .Select(value => value.GetString())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return conflictId is not null &&
                        !baselineConflictIds.Contains(conflictId) &&
                        sessions.SetEquals([lower.ToString("D"), preferred.ToString("D")]) &&
                        string.Equals(
                            item.GetProperty("resource").GetString(),
                            expectedResource,
                            StringComparison.Ordinal);
                });
                if (!activePairConflict)
                {
                    throw new InvalidDataException(
                        "The two paused jobs produced no exact same-component layout conflict evidence.");
                }
                var currentSessions = runtime.GetProperty("sessions").EnumerateArray()
                    .Select(item => item.GetProperty("id").GetGuid())
                    .ToArray();
                var order = new[] { preferred, lower }
                    .Concat(currentSessions.Where(id => id != preferred && id != lower))
                    .ToArray();
                await SendNoContentAsync(
                    HttpMethod.Put,
                    "api/v1/sessions/order",
                    new
                    {
                        orderedSessionIds = order,
                        orderVersion = runtime.GetProperty("orderVersion").GetInt64()
                    }).ConfigureAwait(false);
                runtime = await ReadRuntimeAsync().ConfigureAwait(false);
                var reordered = runtime.GetProperty("queue").EnumerateArray()
                    .Where(item =>
                        item.GetProperty("sessionId").GetGuid() == lower ||
                        item.GetProperty("sessionId").GetGuid() == preferred)
                    .ToArray();
                if (reordered.Length != 2 ||
                    reordered[0].GetProperty("sessionId").GetGuid() != preferred)
                {
                    throw new InvalidDataException("Manual session order did not reprioritize the writer queue.");
                }
                await RequireEffectiveProfileAsync(lower, "FastSafe").ConfigureAwait(false);
                await RequireEffectiveProfileAsync(preferred, "FastSafe").ConfigureAwait(false);

                await SendNoContentAsync(
                    HttpMethod.Put,
                    "api/v1/dev/writer/pause",
                    new { paused = false }).ConfigureAwait(false);
                await WaitForWriterQueueToDrainAsync(lower, preferred).ConfigureAwait(false);
                var snapshot = await SendJsonAsync(HttpMethod.Get, "api/v1/dev/snapshot")
                    .ConfigureAwait(false);
                var component = FindObject(snapshot.GetProperty("canvas").GetProperty("objects"), componentId);
                var pivot = component.GetProperty("pivot");
                RequireClose(pivot.GetProperty("x").GetDouble(), 820, 0.01, "Priority final pivot X");
                RequireClose(pivot.GetProperty("y").GetDouble(), 180, 0.01, "Priority final pivot Y");
                var finalRuntime = await ReadRuntimeAsync().ConfigureAwait(false);
                var finalConflicts = finalRuntime.GetProperty("conflicts").EnumerateArray().ToArray();
                var lowerProblemId = $"problem-{lowerJobId:N}";
                var preferredProblemId = $"problem-{preferredJobId:N}";
                var lowerProblem = finalConflicts.Any(item =>
                    string.Equals(item.GetProperty("id").GetString(), lowerProblemId, StringComparison.Ordinal) &&
                    item.GetProperty("sessionIds").EnumerateArray().Any(value =>
                        string.Equals(value.GetString(), lower.ToString("D"), StringComparison.OrdinalIgnoreCase)));
                var preferredProblem = finalConflicts.Any(item =>
                    string.Equals(item.GetProperty("id").GetString(), preferredProblemId, StringComparison.Ordinal));
                if (!lowerProblem || preferredProblem)
                {
                    throw new InvalidDataException(
                        "The preferred job must commit while only the stale lower-priority job becomes a problem.");
                }
                _report.Steps.Add("Manual order committed the preferred conflicting write first.");
            }
            finally
            {
                await SendNoContentAsync(
                    HttpMethod.Put,
                    "api/v1/dev/writer/pause",
                    new { paused = false }).ConfigureAwait(false);
            }
        }

        private async Task WaitForWriterQueueToDrainAsync(Guid firstSession, Guid secondSession)
        {
            var deadline = DateTimeOffset.UtcNow + QueueTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var runtime = await ReadRuntimeAsync().ConfigureAwait(false);
                var active = runtime.GetProperty("queue").EnumerateArray().Any(item =>
                    item.GetProperty("sessionId").GetGuid() == firstSession ||
                    item.GetProperty("sessionId").GetGuid() == secondSession);
                if (!active && runtime.GetProperty("writer").ValueKind == JsonValueKind.Null)
                {
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            throw new TimeoutException("The prioritized writer queue did not drain in time.");
        }

        private async Task VerifyParallelReadsAsync(Guid pythonObjectId)
        {
            var paths = Enumerable.Range(0, 9).Select(index => (index % 3) switch
            {
                0 => "api/v1/dev/snapshot",
                1 => "api/v1/dev/rhino-objects",
                _ => $"api/v1/dev/grasshopper/{pythonObjectId:D}/outputs"
            }).ToArray();
            var stopwatch = Stopwatch.StartNew();
            var tasks = paths.Select(async path =>
            {
                var started = stopwatch.Elapsed;
                var result = await SendJsonAsync(HttpMethod.Get, path).ConfigureAwait(false);
                return new ReadEvidence(path, started, stopwatch.Elapsed, result.GetRawText().Length);
            }).ToArray();
            var reads = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (reads.Any(item => item.ResponseCharacters <= 2) ||
                reads.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != 3)
            {
                throw new InvalidDataException("Parallel document reads were incomplete or mixed.");
            }
            _report.ParallelReads = reads;
            _report.Steps.Add("Nine concurrent live reads completed with isolated response shapes.");
        }

        private async Task VerifyTerminalLaunchAsync(Guid sessionId)
        {
            await SendNoContentAsync(
                HttpMethod.Post,
                $"api/v1/sessions/{sessionId:D}/terminal",
                body: null).ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await SendJsonAsync(
                    HttpMethod.Get,
                    $"api/v1/dev/terminals/{sessionId:D}").ConfigureAwait(false);
                if (status.GetProperty("isOpen").GetBoolean())
                {
                    var processId = status.GetProperty("processId").GetInt32();
                    var processStartTimeUtc = status.GetProperty("processStartTimeUtc")
                        .GetDateTime()
                        .ToUniversalTime();
                    using var process = Process.GetProcessById(processId);
                    var identity = CaptureOwnedProcess(process);
                    if (identity.StartedAtUtc != processStartTimeUtc ||
                        _agentIdentity is null ||
                        GetParentProcessId(process) != _agentIdentity.ProcessId ||
                        !IsSameProcess(_agentIdentity) ||
                        !string.Equals(identity.Name, "GPTino.Terminal", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            Path.GetFileName(identity.ExecutablePath),
                            "GPTino.Terminal.exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Terminal status did not match the exact packaged terminal process identity.");
                    }
                    RequireStrictDescendant(
                        identity.ExecutablePath,
                        _installedPackageRoot,
                        "installed terminal executable");
                    RequireContentEqual(
                        ReadEvidence(_stagedTerminalExecutable),
                        ReadEvidence(identity.ExecutablePath),
                        "installed terminal executable");
                    _terminalIdentity = identity;
                    _report.TerminalProcessId = processId;
                    _report.OwnedProcesses.Add(identity);
                    _report.Steps.Add("Session terminal button launched one owned terminal client.");
                    await WriteReportAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            throw new TimeoutException("Session terminal process did not become observable.");
        }

        private async Task RequireEffectiveProfileAsync(Guid sessionId, string expected)
        {
            var session = FindSession(await ReadRuntimeAsync().ConfigureAwait(false), sessionId);
            var actual = session.TryGetProperty("effectiveProfile", out var element)
                ? element.GetString()
                : null;
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Session {sessionId:D} used effective profile '{actual}', expected '{expected}'.");
            }
        }

        private async Task<JsonElement> ReadRuntimeAsync() =>
            await SendJsonAsync(HttpMethod.Get, "api/v1/runtime").ConfigureAwait(false);

        private async Task<JsonElement> SendJsonAsync(
            HttpMethod method,
            string relativePath,
            object? body = null,
            bool allowEmpty = false)
        {
            using var request = new HttpRequestMessage(method, relativePath);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }
            using var response = await RequireApi().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var payload = await ReadBoundedApiContentAsync(response.Content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{method} {relativePath} returned {(int)response.StatusCode}: {Truncate(payload, 1000)}");
            }
            if (string.IsNullOrWhiteSpace(payload))
            {
                if (!allowEmpty)
                {
                    throw new InvalidDataException(
                        $"{method} {relativePath} returned an empty JSON response.");
                }
                using var empty = JsonDocument.Parse("{}");
                return empty.RootElement.Clone();
            }
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }

        private async Task SendNoContentAsync(HttpMethod method, string relativePath, object? body)
        {
            _ = await SendJsonAsync(method, relativePath, body, allowEmpty: true).ConfigureAwait(false);
        }

        private static async Task<string> ReadBoundedApiContentAsync(HttpContent content)
        {
            if (content.Headers.ContentLength is > MaximumApiResponseBytes)
            {
                throw new InvalidDataException("AgentHost API response exceeded the size limit.");
            }
            await using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaximumApiResponseBytes)
                {
                    throw new InvalidDataException("AgentHost API response exceeded the size limit.");
                }
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }

        private HttpClient RequireApi() =>
            _api ?? throw new InvalidOperationException("AgentHost API is not configured.");

        private async Task<ProcessEvidence> RunProcessAsync(
            string name,
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = _evidenceRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            RemoveGptinoEnvironment(startInfo);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {name}.");
            process.StandardInput.Close();
            var identity = CaptureOwnedProcess(process, executable);
            _report.OwnedProcesses.Add(identity);
            try
            {
                await WriteReportAsync().ConfigureAwait(false);
            }
            catch
            {
                if (MatchesOwnedProcess(process, identity))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                throw;
            }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (MatchesOwnedProcess(process, identity))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                throw new TimeoutException($"{name} exceeded {timeout}.");
            }
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var evidence = new ProcessEvidence(
                name,
                identity.ProcessId,
                identity.StartedAtUtc,
                process.ExitCode,
                output,
                error);
            _report.Processes.Add(evidence);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{name} exited with code {process.ExitCode}: {Truncate(error + output, 1500)}");
            }
            return evidence;
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

        private async Task StopOwnedProcessesAsync()
        {
            if (_rhinoProcess is not null &&
                _rhinoIdentity is not null &&
                MatchesOwnedProcess(_rhinoProcess, _rhinoIdentity))
            {
                _ = _rhinoProcess.CloseMainWindow();
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await _rhinoProcess.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (MatchesOwnedProcess(_rhinoProcess, _rhinoIdentity))
                    {
                        _rhinoProcess.Kill(entireProcessTree: true);
                        await _rhinoProcess.WaitForExitAsync().ConfigureAwait(false);
                        _report.ForcedRhinoCleanup = true;
                    }
                }
            }
            await StopExactProcessIfAliveAsync(_terminalIdentity).ConfigureAwait(false);
            await StopExactProcessIfAliveAsync(_agentIdentity).ConfigureAwait(false);
            _rhinoProcess?.Dispose();
            _rhinoProcess = null;
        }

        private static async Task StopExactProcessIfAliveAsync(OwnedProcess? identity)
        {
            if (identity is null)
            {
                return;
            }
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (!MatchesOwnedProcess(process, identity))
                {
                    return;
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
                // The captured process exited, or its identity no longer matches.
            }
        }

        private void VerifyOriginalSourcesUnchanged()
        {
            if (_rhinoBaseline is null || _grasshopperBaseline is null)
            {
                return;
            }
            RequireEvidenceEqual(_rhinoBaseline, ReadEvidence(_rhinoSource), "Desktop Rhino source");
            RequireEvidenceEqual(
                _grasshopperBaseline,
                ReadEvidence(_grasshopperSource),
                "Desktop Grasshopper source");
            _report.OriginalSourcesUnchanged = true;
        }

        private async Task WriteReportAsync()
        {
            if (!Directory.Exists(_evidenceRoot))
            {
                return;
            }
            var path = Path.Combine(_evidenceRoot, "report.json");
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(_report, JsonOptions),
                new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }

        private static string BuildGrasshopperPrompt(GeometryIds ids) => $$"""
            Work only in the explicitly bound Grasshopper document and use GPTino tools. Continue until the live state verifies.

            Create or repair exactly these task-owned objects, using these exact instance UUIDs and nicknames:
            - Number Slider {{ids.DiameterSliderId:D}}, nickname GPTinoE2E_Diameter, value 10, minimum 0, maximum 100, decimalPlaces 0.
            - Number Slider {{ids.HeightSliderId:D}}, nickname GPTinoE2E_Height, value 20, minimum 0, maximum 100, decimalPlaces 0.
            - Rhino 8 CPython 3 component {{ids.PythonObjectId:D}}, nickname GPTinoE2E_Cylinder.

            Inspect the component catalog to obtain actual installed type GUIDs; do not guess them. Preserve the Python component's existing first input socket UUID, rename it Diameter, append one item-access double input named Height with new UUID {{ids.AppendedHeightInputId:D}}, and preserve the existing output socket UUID while renaming it Cylinder. Do not remove sockets. Wire the two sliders to the matching inputs.

            Install and execute this exact behavior (the marker must remain in executable source):
            # GPTINO_E2E_CYLINDER
            import Rhino.Geometry as rg
            base_circle = rg.Circle(rg.Plane.WorldXY, Diameter / 2.0)
            Cylinder = rg.Cylinder(base_circle, Height).ToBrep(True, True)

            Require no runtime errors and a capped Brep with world bounding-box size 10 x 10 x 20. Do not modify any unrelated existing object. Use conflict fingerprints, typed payload artifacts, centrally serialized ChangeSets, and explicit runtime-error acceptance predicates.
            """;

        private static string BuildRhinoPrompt(Guid objectId) => $$"""
            Work only in the explicitly bound Rhino document and use GPTino tools. Create or repair one task-owned sphere with exact object UUID {{objectId:D}}, logicalEntityId gptino-e2e-sphere, object name GPTinoE2E_Sphere, center 0,0,0, and radius 3. Use createRhinoPrimitive with the Sphere kind rather than inventing RhinoCommon JSON. Verify independently that its bounding-box center is 0,0,0 and size is 6 x 6 x 6. Do not modify unrelated objects. Use a high-assurance, conflict-checked ChangeSet with an explicit object-exists acceptance predicate.
            """;

        private static string BuildMovePrompt(Guid componentId, double x, double y) =>
            $"Read the bound Grasshopper snapshot, then move only component {componentId:D} to exact pivot x={x}, y={y}. " +
            "This is a simple fast-safe task. Submit one conflict-checked MoveComponent ChangeSet with the actual current layout fingerprint and no unrelated writes. Reply after the writer job is accepted.";

        private static JsonElement FindSession(JsonElement runtime, Guid sessionId) =>
            RequireSingle(
                runtime.GetProperty("sessions"),
                item => item.GetProperty("id").GetGuid() == sessionId,
                $"session {sessionId:D}");

        private static JsonElement FindObject(JsonElement objects, Guid objectId) =>
            RequireSingle(
                objects,
                item => item.GetProperty("objectId").GetGuid() == objectId,
                $"canvas object {objectId:D}");

        private static JsonElement FindParameter(JsonElement parameters, string name) =>
            RequireSingle(
                parameters,
                item => string.Equals(
                    item.GetProperty("name").GetString(),
                    name,
                    StringComparison.Ordinal),
                $"parameter '{name}'");

        private static JsonElement RequireSingle(
            JsonElement array,
            Func<JsonElement, bool> predicate,
            string label)
        {
            JsonElement match = default;
            var count = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (!predicate(item))
                {
                    continue;
                }
                match = item;
                count++;
            }
            if (count != 1)
            {
                throw new InvalidDataException(
                    $"Expected exactly one {label}, found {count}.");
            }
            return match;
        }

        private static void RequireSlider(
            JsonElement state,
            string nickName,
            decimal value,
            decimal minimum,
            decimal maximum,
            int decimalPlaces)
        {
            if (!string.Equals(state.GetProperty("nickName").GetString(), nickName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Slider nickname '{nickName}' is not exact.");
            }
            using var valueDocument = JsonDocument.Parse(state.GetProperty("valueJson").GetString()!);
            var actual = valueDocument.RootElement;
            if (!string.Equals(actual.GetProperty("kind").GetString(), "numberSlider", StringComparison.Ordinal) ||
                actual.GetProperty("value").GetDecimal() != value ||
                actual.GetProperty("minimum").GetDecimal() != minimum ||
                actual.GetProperty("maximum").GetDecimal() != maximum ||
                actual.GetProperty("decimalPlaces").GetInt32() != decimalPlaces)
            {
                throw new InvalidDataException($"Slider '{nickName}' has the wrong value or range.");
            }
        }

        private static void RequireWire(
            JsonElement wires,
            Guid sourceObjectId,
            Guid sourceParameterId,
            Guid targetObjectId,
            Guid targetParameterId)
        {
            var found = wires.EnumerateArray().Any(item =>
                item.GetProperty("sourceObjectId").GetGuid() == sourceObjectId &&
                item.GetProperty("sourceParameterId").GetGuid() == sourceParameterId &&
                item.GetProperty("targetObjectId").GetGuid() == targetObjectId &&
                item.GetProperty("targetParameterId").GetGuid() == targetParameterId);
            if (!found)
            {
                throw new InvalidDataException("Required Grasshopper wire was not found.");
            }
        }

        private static void RequireNoRuntimeErrors(JsonElement messages)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("level", out var level) &&
                    ((level.ValueKind == JsonValueKind.String &&
                      string.Equals(level.GetString(), "error", StringComparison.OrdinalIgnoreCase)) ||
                     (level.ValueKind == JsonValueKind.Number && level.GetInt32() >= 2)))
                {
                    throw new InvalidDataException(
                        "Grasshopper Python reports a runtime error: " + message.GetRawText());
                }
            }
        }

        private static void RequireClose(double actual, double expected, double tolerance, string label)
        {
            if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidDataException(
                    $"{label} is {actual:R}; expected {expected:R} ± {tolerance:R}.");
            }
        }

        private static void RequirePathEqual(string? actual, string expected, string label)
        {
            if (string.IsNullOrWhiteSpace(actual) ||
                !string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{label} runtime path is not the owned copy.");
            }
        }

        private static FileEvidence ReadEvidence(string path)
        {
            var info = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            return new FileEvidence(
                Path.GetFullPath(path),
                info.Length,
                info.LastWriteTimeUtc,
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        private static DirectoryEvidence ReadDirectoryEvidence(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            var pending = new Stack<DirectoryInfo>();
            var files = new List<FileInfo>();
            pending.Push(new DirectoryInfo(fullRoot));
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                directory.Refresh();
                if (!directory.Exists)
                {
                    throw new DirectoryNotFoundException(
                        $"Package payload directory disappeared: {directory.FullName}");
                }
                if (directory.LinkTarget is not null ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Package payload contains a reparse-point directory.");
                }
                foreach (var child in directory.EnumerateDirectories())
                {
                    child.Refresh();
                    if (child.LinkTarget is not null ||
                        (child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Package payload contains a reparse-point directory.");
                    }
                    pending.Push(child);
                }
                foreach (var file in directory.EnumerateFiles())
                {
                    file.Refresh();
                    if (file.LinkTarget is not null ||
                        (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Package payload contains a reparse-point file.");
                    }
                    files.Add(file);
                }
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in files.OrderBy(
                         file => Path.GetRelativePath(fullRoot, file.FullName),
                         StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(fullRoot, file.FullName).Replace('\\', '/');
                var evidence = ReadEvidence(file.FullName);
                hash.AppendData(Encoding.UTF8.GetBytes(
                    relative + "\0" + evidence.Length + "\0" + evidence.Sha256 + "\0"));
            }
            return new DirectoryEvidence(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                files.Count);
        }

        private static void RequireDirectoryContentEqual(
            DirectoryEvidence expected,
            DirectoryEvidence actual,
            string label)
        {
            if (expected.FileCount != actual.FileCount ||
                !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{label} does not match the complete staged file manifest.");
            }
        }

        private static void RequireEvidenceEqual(FileEvidence expected, FileEvidence actual, string label)
        {
            if (expected.Length != actual.Length ||
                expected.LastWriteTimeUtc != actual.LastWriteTimeUtc ||
                !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{label} changed during live E2E.");
            }
        }

        private static void RequireContentEqual(FileEvidence expected, FileEvidence actual, string label)
        {
            if (expected.Length != actual.Length ||
                !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{label} does not match its hashed source bytes.");
            }
        }

        private static OwnedProcess CaptureOwnedProcess(
            Process process,
            string? trustedExecutablePath = null)
        {
            process.Refresh();
            var executablePath = trustedExecutablePath is null
                ? process.MainModule?.FileName
                : Path.GetFullPath(trustedExecutablePath);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException(
                    $"Could not resolve executable identity for process {process.Id}.");
            }
            return new OwnedProcess(
                process.Id,
                process.ProcessName,
                process.StartTime.ToUniversalTime(),
                Path.GetFullPath(executablePath));
        }

        private static bool IsSameProcess(OwnedProcess identity)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                return MatchesOwnedProcess(process, identity);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static bool MatchesOwnedProcess(Process process, OwnedProcess identity)
        {
            process.Refresh();
            if (process.Id != identity.ProcessId ||
                process.HasExited ||
                process.StartTime.ToUniversalTime() != identity.StartedAtUtc ||
                !string.Equals(process.ProcessName, identity.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var executablePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(executablePath) &&
                string.Equals(
                    Path.GetFullPath(executablePath),
                    identity.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void RequireExecutable(OwnedProcess identity, string expectedPath, string label)
        {
            if (!string.Equals(
                    identity.ExecutablePath,
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{label} executable identity is not the fixed path.");
            }
        }

        private static string Truncate(string value, int maximum) =>
            value.Length <= maximum ? value : value[..maximum] + "…";

        private sealed class ProcessCollection : IDisposable
        {
            public ProcessCollection(IReadOnlyList<Process> items)
            {
                Items = items;
            }

            public IReadOnlyList<Process> Items { get; }

            public void Dispose()
            {
                foreach (var process in Items)
                {
                    process.Dispose();
                }
            }
        }
    }

    private sealed class LiveRunReport
    {
        public required string RunRoot { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public required string Status { get; set; }
        public string? Error { get; set; }
        public string? PackageSha256 { get; set; }
        public string? StagedRuntimePayloadSha256 { get; set; }
        public string? InstalledRuntimePayloadSha256 { get; set; }
        public int? RuntimePayloadFileCount { get; set; }
        public int? RhinoProcessId { get; set; }
        public int? AgentHostProcessId { get; set; }
        public int? TerminalProcessId { get; set; }
        public bool ForcedRhinoCleanup { get; set; }
        public bool OriginalSourcesUnchanged { get; set; }
        public GeometryIds? GeometryIds { get; set; }
        public List<Guid> SessionIds { get; } = [];
        public List<string> Steps { get; } = [];
        public List<OwnedProcess> OwnedProcesses { get; } = [];
        public List<ProcessEvidence> Processes { get; } = [];
        public IReadOnlyList<ReadEvidence> ParallelReads { get; set; } = [];
    }

    private sealed record GeometryIds(
        Guid DiameterSliderId,
        Guid HeightSliderId,
        Guid PythonObjectId,
        Guid AppendedHeightInputId,
        Guid SphereObjectId);

    private sealed record FileEvidence(
        string Path,
        long Length,
        DateTime LastWriteTimeUtc,
        string Sha256);

    private sealed record DirectoryEvidence(string Sha256, int FileCount);

    private sealed record OwnedProcess(
        int ProcessId,
        string Name,
        DateTime StartedAtUtc,
        string ExecutablePath);

    private sealed record EndpointEvidence(
        Uri BaseUri,
        int ProcessId,
        DateTime ProcessStartTimeUtc,
        DateTimeOffset StartedAtUtc);

    private sealed record ProcessEvidence(
        string Name,
        int ProcessId,
        DateTime StartedAtUtc,
        int ExitCode,
        string Output,
        string Error);

    private sealed record ReadEvidence(
        string Path,
        TimeSpan Started,
        TimeSpan Completed,
        int ResponseCharacters);
}
