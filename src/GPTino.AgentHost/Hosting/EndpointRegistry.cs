namespace GPTino.AgentHost.Hosting;

public sealed class EndpointRegistry
{
    private readonly TaskCompletionSource<Uri> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri? BaseUri { get; private set; }

    public Task<Uri> WhenReady => _ready.Task;

    public void Set(Uri baseUri)
    {
        if (!baseUri.IsLoopback)
        {
            throw new InvalidOperationException("GPTino AgentHost must publish a loopback endpoint.");
        }
        BaseUri = baseUri;
        _ready.TrySetResult(baseUri);
    }
}
