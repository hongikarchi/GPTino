using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GPTino.AgentHost.Api;

namespace GPTino.AgentHost.Hosting;

public sealed class TerminalLauncher
{
    private const int SwRestore = 9;
    private const string ApiTokenEnvironmentVariable = "GPTINO_API_TOKEN";

    private readonly EndpointRegistry _endpoint;
    private readonly AgentHostOptions _options;
    private readonly object _processGate = new();
    private readonly Dictionary<Guid, Process> _processes = [];

    public TerminalLauncher(EndpointRegistry endpoint, AgentHostOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    public async Task LaunchAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var baseUri = _endpoint.BaseUri ?? await _endpoint.WhenReady.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_processGate)
        {
            if (TryGetOpenProcessLocked(session.Id, out var existing))
            {
                TryFocus(existing);
                return;
            }

            var baseDirectory = AppContext.BaseDirectory;
            var executable = Path.Combine(baseDirectory, "GPTino.Terminal.exe");
            var assembly = Path.Combine(baseDirectory, "GPTino.Terminal.dll");
            ProcessStartInfo startInfo;
            if (File.Exists(executable))
            {
                startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            }
            else if (File.Exists(assembly))
            {
                startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                startInfo.ArgumentList.Add(assembly);
            }
            else
            {
                throw new FileNotFoundException("GPTino terminal client is not installed beside AgentHost.");
            }
            startInfo.ArgumentList.Add("attach");
            startInfo.ArgumentList.Add("--endpoint");
            startInfo.ArgumentList.Add(baseUri.ToString().TrimEnd('/'));
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(session.Id.ToString("D"));
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(session.Name);
            startInfo.Environment[ApiTokenEnvironmentVariable] = _options.ApiToken;

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Terminal process did not start.");
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException("Windows could not open the GPTino terminal client.", exception);
            }

            _processes[session.Id] = process;
            process.Exited += (_, _) => RemoveProcess(session.Id, process);
            process.EnableRaisingEvents = true;
            if (HasExited(process))
            {
                RemoveProcessLocked(session.Id, process);
            }
        }
    }

    public bool IsOpen(Guid sessionId)
    {
        lock (_processGate)
        {
            return TryGetOpenProcessLocked(sessionId, out _);
        }
    }

    private bool TryGetOpenProcessLocked(Guid sessionId, out Process process)
    {
        if (_processes.TryGetValue(sessionId, out process!) && !HasExited(process))
        {
            return true;
        }

        if (process is not null)
        {
            RemoveProcessLocked(sessionId, process);
        }
        process = null!;
        return false;
    }

    private void RemoveProcess(Guid sessionId, Process process)
    {
        lock (_processGate)
        {
            RemoveProcessLocked(sessionId, process);
        }
    }

    private void RemoveProcessLocked(Guid sessionId, Process process)
    {
        if (_processes.TryGetValue(sessionId, out var current) && ReferenceEquals(current, process))
        {
            _processes.Remove(sessionId);
            process.Dispose();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryFocus(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            process.Refresh();
            var window = process.MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                _ = ShowWindowAsync(window, SwRestore);
                _ = SetForegroundWindow(window);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the liveness check and focus request.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}
