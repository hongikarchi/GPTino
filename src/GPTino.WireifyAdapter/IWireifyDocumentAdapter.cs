using GPTino.BridgeContract;

namespace GPTino.WireifyAdapter;

/// <summary>
/// Owns Python source, parameter schemas and typing, execution, and runtime errors.
/// Canvas topology and layout are deliberately absent; those belong to Cordyceps.
/// </summary>
public interface IWireifyDocumentAdapter
{
    Task<PythonComponentState> ReadPythonComponentAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default);

    Task<WireifyMutationResult> SetSourceAsync(
        DocumentTarget target,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken = default);

    Task<WireifyMutationResult> SetParameterSchemaAsync(
        DocumentTarget target,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken = default);

    Task<WireifyMutationResult> SetInputTypingAsync(
        DocumentTarget target,
        SetInputTypingRequest request,
        CancellationToken cancellationToken = default);

    Task<PythonExecutionResult> ExecuteAsync(
        DocumentTarget target,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComponentRuntimeMessage>> ReadRuntimeMessagesAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default);
}

public sealed record PythonComponentState(
    Guid ComponentId,
    string Source,
    string SourceSha256,
    PythonRuntime Runtime,
    IReadOnlyList<PythonParameter> Inputs,
    IReadOnlyList<PythonParameter> Outputs,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

// Historic name: this enum predates C# support and is baked into the bridge payload contract
// ("runtime" on python.setSource). It now covers every Rhino 8 script-component language.
public enum PythonRuntime
{
    Cpython3,
    IronPython2,
    Csharp,
}

public enum ParameterAccess
{
    Item,
    List,
    Tree,
}

public sealed record PythonParameter(
    Guid ParameterId,
    string Name,
    string NickName,
    string TypeHint,
    ParameterAccess Access,
    bool Optional);

public sealed record SetPythonSourceRequest(
    string OperationId,
    Guid ComponentId,
    string ExpectedSourceSha256,
    string Source,
    PythonRuntime Runtime,
    bool ExpireSolution);

public sealed record SetParameterSchemaRequest(
    string OperationId,
    Guid ComponentId,
    IReadOnlyList<PythonParameter> Inputs,
    IReadOnlyList<PythonParameter> Outputs,
    bool PreserveIncidentWires);

public sealed record SetInputTypingRequest(
    string OperationId,
    Guid ComponentId,
    Guid InputParameterId,
    string TypeHint,
    ParameterAccess Access);

public sealed record ExecutePythonComponentRequest(
    string OperationId,
    Guid ComponentId,
    bool ExpireUpstream,
    bool RecomputeDocument);

public sealed record WireifyMutationResult(
    string OperationId,
    bool Changed,
    string BeforeFingerprint,
    string AfterFingerprint,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

public sealed record PythonExecutionResult(
    string OperationId,
    Guid ComponentId,
    bool Solved,
    string OutputFingerprint,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

public sealed record ComponentRuntimeMessage(
    RuntimeMessageLevel Level,
    string Message);

public enum RuntimeMessageLevel
{
    Remark,
    Warning,
    Error,
}
