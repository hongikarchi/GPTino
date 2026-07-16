using System.Diagnostics;

namespace GPTino.AgentHost.Hosting;

public sealed class ParentProcessMonitor : BackgroundService
{
    private readonly AgentHostOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ParentProcessMonitor> _logger;

    public ParentProcessMonitor(
        AgentHostOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<ParentProcessMonitor> logger)
    {
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ParentProcessId is not { } parentId)
        {
            return;
        }
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Rhino parent process {ProcessId} was not running; stopping AgentHost.", parentId);
            _lifetime.StopApplication();
            return;
        }

        using (parent)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (parent.HasExited)
                {
                    _logger.LogInformation("Rhino parent process exited; stopping AgentHost.");
                    _lifetime.StopApplication();
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
