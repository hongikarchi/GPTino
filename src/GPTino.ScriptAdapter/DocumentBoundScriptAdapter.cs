using GPTino.BridgeContract;

namespace GPTino.ScriptAdapter;

/// <summary>
/// Base class that forces concrete adapters to resolve the exact requested document before work.
/// It intentionally provides no active-document resolver.
/// </summary>
public abstract class DocumentBoundScriptAdapter<TDocument> : IScriptDocumentAdapter
    where TDocument : class
{
    private readonly IScriptDocumentResolver<TDocument> _resolver;

    protected DocumentBoundScriptAdapter(IScriptDocumentResolver<TDocument> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public Task<PythonComponentState> ReadPythonComponentAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default) =>
        ReadPythonComponentCoreAsync(Resolve(target), componentId, cancellationToken);

    public Task<ScriptMutationResult> SetSourceAsync(
        DocumentTarget target,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken = default) =>
        SetSourceCoreAsync(Resolve(target), request, cancellationToken);

    public Task<ScriptMutationResult> SetParameterSchemaAsync(
        DocumentTarget target,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken = default) =>
        SetParameterSchemaCoreAsync(Resolve(target), request, cancellationToken);

    public Task<ScriptMutationResult> SetInputTypingAsync(
        DocumentTarget target,
        SetInputTypingRequest request,
        CancellationToken cancellationToken = default) =>
        SetInputTypingCoreAsync(Resolve(target), request, cancellationToken);

    public Task<PythonExecutionResult> ExecuteAsync(
        DocumentTarget target,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = Resolve(target);
        // Checkpoint the document BEFORE the solve: a script execute can freeze or crash Rhino on the
        // single UI thread, and a force-kill afterwards would otherwise lose all unsaved work.
        OnBeforeExecute(document, target, cancellationToken);
        return ExecuteCoreAsync(document, request, cancellationToken);
    }

    public Task<IReadOnlyList<ComponentRuntimeMessage>> ReadRuntimeMessagesAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default) =>
        ReadRuntimeMessagesCoreAsync(Resolve(target), componentId, cancellationToken);

    protected abstract Task<PythonComponentState> ReadPythonComponentCoreAsync(
        TDocument document,
        Guid componentId,
        CancellationToken cancellationToken);

    protected abstract Task<ScriptMutationResult> SetSourceCoreAsync(
        TDocument document,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<ScriptMutationResult> SetParameterSchemaCoreAsync(
        TDocument document,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<ScriptMutationResult> SetInputTypingCoreAsync(
        TDocument document,
        SetInputTypingRequest request,
        CancellationToken cancellationToken);

    protected abstract Task<PythonExecutionResult> ExecuteCoreAsync(
        TDocument document,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fired on the UI thread immediately before a script solve, with the resolved document and its target
    /// (which carries the paired Rhino document serial). Concrete adapters override it to back the document
    /// up before the risky solve. Default is a no-op. It MUST NOT throw — it runs inline with the user's
    /// execute, and a backup failure must never block authoring.
    /// </summary>
    protected virtual void OnBeforeExecute(
        TDocument document,
        DocumentTarget target,
        CancellationToken cancellationToken)
    {
    }

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

public interface IScriptDocumentResolver<out TDocument>
    where TDocument : class
{
    TDocument Resolve(DocumentTarget target);
}
