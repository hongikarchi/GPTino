using GPTino.BridgeContract;

namespace GPTino.WireifyAdapter;

/// <summary>
/// Base class that forces concrete adapters to resolve the exact requested document before work.
/// It intentionally provides no active-document resolver.
/// </summary>
public abstract class DocumentBoundWireifyAdapter<TDocument> : IWireifyDocumentAdapter
    where TDocument : class
{
    private readonly IWireifyDocumentResolver<TDocument> _resolver;

    protected DocumentBoundWireifyAdapter(IWireifyDocumentResolver<TDocument> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public Task<PythonComponentState> ReadPythonComponentAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default) =>
        ReadPythonComponentCoreAsync(Resolve(target), componentId, cancellationToken);

    public Task<WireifyMutationResult> SetSourceAsync(
        DocumentTarget target,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken = default) =>
        SetSourceCoreAsync(Resolve(target), request, cancellationToken);

    public Task<WireifyMutationResult> SetParameterSchemaAsync(
        DocumentTarget target,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken = default) =>
        SetParameterSchemaCoreAsync(Resolve(target), request, cancellationToken);

    public Task<WireifyMutationResult> SetInputTypingAsync(
        DocumentTarget target,
        SetInputTypingRequest request,
        CancellationToken cancellationToken = default) =>
        SetInputTypingCoreAsync(Resolve(target), request, cancellationToken);

    public Task<PythonExecutionResult> ExecuteAsync(
        DocumentTarget target,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(Resolve(target), request, cancellationToken);

    public Task<IReadOnlyList<ComponentRuntimeMessage>> ReadRuntimeMessagesAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default) =>
        ReadRuntimeMessagesCoreAsync(Resolve(target), componentId, cancellationToken);

    protected abstract Task<PythonComponentState> ReadPythonComponentCoreAsync(
        TDocument document,
        Guid componentId,
        CancellationToken cancellationToken);

    protected abstract Task<WireifyMutationResult> SetSourceCoreAsync(
        TDocument document,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<WireifyMutationResult> SetParameterSchemaCoreAsync(
        TDocument document,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<WireifyMutationResult> SetInputTypingCoreAsync(
        TDocument document,
        SetInputTypingRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<PythonExecutionResult> ExecuteCoreAsync(
        TDocument document,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<IReadOnlyList<ComponentRuntimeMessage>> ReadRuntimeMessagesCoreAsync(
        TDocument document,
        Guid componentId,
        CancellationToken cancellationToken);

    private TDocument Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return _resolver.Resolve(target);
    }
}

public interface IWireifyDocumentResolver<out TDocument>
    where TDocument : class
{
    TDocument Resolve(DocumentTarget target);
}
