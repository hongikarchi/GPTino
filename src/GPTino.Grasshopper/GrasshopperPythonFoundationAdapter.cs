// SPDX-License-Identifier: Apache-2.0
// Wireify-derived behavior; provenance is recorded in docs/upstream-map.json.

using System.Collections;
using System.Reflection;
using System.Text;
using GPTino.WireifyAdapter;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Document-bound Rhino 8 Python adapter. RhinoCode's unversioned implementation assembly is
/// accessed by reflection, but only after the component has been identified by its Python proxy
/// GUID. This deliberately excludes C# Script components, which implement the same public script
/// interfaces.
/// </summary>
public sealed class GrasshopperPythonFoundationAdapter : DocumentBoundWireifyAdapter<GH_Document>
{
    private static readonly Guid Cpython3ComponentGuid = new("719467e6-7cf5-4848-99b0-c5dd57e5442c");
    private static readonly Guid IronPython2ComponentGuid = new("410755b1-224a-4c1e-a407-bf32fb45ea7e");
    private static readonly TimeSpan RebuildTimeout = TimeSpan.FromSeconds(30);

    private const string Python3Directive = "#! python 3";
    private const string ScriptComponentInterface = "RhinoCodePlatform.GH.IScriptComponent";
    private const string ScriptParameterInterface = "RhinoCodePlatform.GH.IScriptParameter";

    public GrasshopperPythonFoundationAdapter(ExplicitGrasshopperDocumentResolver resolver)
        : base(resolver)
    {
    }

    protected override Task<PythonComponentState> ReadPythonComponentCoreAsync(
        GH_Document document,
        Guid componentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadState(ResolveComponent(document, componentId)));
    }

    protected override Task<WireifyMutationResult> SetSourceCoreAsync(
        GH_Document document,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperation(request.OperationId, request.ComponentId);
        if (string.IsNullOrWhiteSpace(request.ExpectedSourceSha256))
        {
            throw new InvalidOperationException("ExpectedSourceSha256 is required for a source edit.");
        }

        var component = ResolveComponent(document, request.ComponentId);
        var before = ReadState(component);
        if (!string.Equals(before.SourceSha256, request.ExpectedSourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Python source changed after the request snapshot.");
        }
        if (before.Runtime != request.Runtime)
        {
            throw new NotSupportedException(
                "Changing the Python runtime is not safe in-place; create the intended script component first.");
        }

        var source = before.Runtime == PythonRuntime.Cpython3
            ? EnsurePython3Directive(request.Source)
            : request.Source ?? throw new InvalidOperationException("Python source is required.");
        if (string.Equals(before.Source, source, StringComparison.Ordinal))
        {
            return Task.FromResult(new WireifyMutationResult(
                request.OperationId,
                Changed: false,
                PythonComponentFingerprint.Compute(before),
                PythonComponentFingerprint.Compute(before),
                before.RuntimeMessages));
        }

        document.UndoUtil.RecordGenericObjectEvent($"GPTino: {request.OperationId}", component);
        try
        {
            SetExecutableSource(component, before.Runtime, source);
            Rebuild(component, before.Runtime);
            var installed = ReadExecutableSource(component, before.Runtime);
            if (!string.Equals(installed, source, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("RhinoCode did not retain the requested executable source.");
            }
        }
        catch (Exception mutationFailure)
        {
            try
            {
                SetExecutableSource(component, before.Runtime, before.Source);
                Rebuild(component, before.Runtime);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Python source update failed and its in-place rollback also failed; use Grasshopper Undo.",
                    mutationFailure,
                    rollbackFailure);
            }

            throw;
        }

        if (request.ExpireSolution)
        {
            component.ExpireSolution(recompute: false);
            document.NewSolution(expireAllObjects: false);
        }

        var after = ReadState(component);
        return Task.FromResult(new WireifyMutationResult(
            request.OperationId,
            Changed: true,
            PythonComponentFingerprint.Compute(before),
            PythonComponentFingerprint.Compute(after),
            after.RuntimeMessages));
    }

    protected override Task<WireifyMutationResult> SetParameterSchemaCoreAsync(
        GH_Document document,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperation(request.OperationId, request.ComponentId);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        ArgumentNullException.ThrowIfNull(request.Outputs);

        var component = ResolveComponent(document, request.ComponentId);
        var before = ReadState(component);
        var inputs = ReadParameterObjects(component, "Inputs");
        var outputs = ReadParameterObjects(component, "Outputs");
        if (inputs.Count != request.Inputs.Count || outputs.Count != request.Outputs.Count)
        {
            throw new NotSupportedException(
                "Foundation schema updates preserve socket count. Add/remove sockets through an undo-safe staged component workflow.");
        }

        ValidateSchema(request.Inputs, request.Outputs);
        var plans = PrepareSchema(inputs, request.Inputs)
            .Concat(PrepareSchema(outputs, request.Outputs))
            .ToArray();
        if (plans.All(plan => plan.IsNoOp))
        {
            return Task.FromResult(new WireifyMutationResult(
                request.OperationId,
                Changed: false,
                PythonComponentFingerprint.Compute(before),
                PythonComponentFingerprint.Compute(before),
                before.RuntimeMessages));
        }

        var wireState = CaptureWireState(inputs.Concat(outputs));
        document.UndoUtil.RecordGenericObjectEvent($"GPTino: {request.OperationId}", component);
        try
        {
            foreach (var plan in plans)
            {
                ApplyPlan(plan);
            }
            SynchronizeParameters(component);
            VerifySocketIdentity(inputs, request.Inputs);
            VerifySocketIdentity(outputs, request.Outputs);
            VerifyWireState(wireState);
        }
        catch (Exception mutationFailure)
        {
            RestorePlans(plans, wireState, component, mutationFailure);
            throw;
        }

        component.ExpireSolution(recompute: false);
        document.NewSolution(expireAllObjects: false);
        var after = ReadState(component);
        return Task.FromResult(new WireifyMutationResult(
            request.OperationId,
            !string.Equals(
                PythonComponentFingerprint.Compute(before),
                PythonComponentFingerprint.Compute(after),
                StringComparison.Ordinal),
            PythonComponentFingerprint.Compute(before),
            PythonComponentFingerprint.Compute(after),
            after.RuntimeMessages));
    }

    protected override Task<WireifyMutationResult> SetInputTypingCoreAsync(
        GH_Document document,
        SetInputTypingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperation(request.OperationId, request.ComponentId);
        if (request.InputParameterId == Guid.Empty)
        {
            throw new InvalidOperationException("InputParameterId is required.");
        }

        var component = ResolveComponent(document, request.ComponentId);
        var before = ReadState(component);
        var input = ReadParameterObjects(component, "Inputs")
            .SingleOrDefault(item => item.ParameterId == request.InputParameterId)
            ?? throw new KeyNotFoundException(
                $"Python input {request.InputParameterId:D} was not found.");
        var current = ToParameter(input);
        var desired = current with { TypeHint = request.TypeHint, Access = request.Access };
        var plan = PrepareSchema(new[] { input }, new[] { desired }).Single();
        if (plan.IsNoOp)
        {
            return Task.FromResult(new WireifyMutationResult(
                request.OperationId,
                Changed: false,
                PythonComponentFingerprint.Compute(before),
                PythonComponentFingerprint.Compute(before),
                before.RuntimeMessages));
        }

        var wireState = CaptureWireState(new[] { input });
        document.UndoUtil.RecordGenericObjectEvent($"GPTino: {request.OperationId}", component);
        try
        {
            ApplyPlan(plan);
            SynchronizeParameters(component);
            VerifySocketIdentity(new[] { input }, new[] { desired });
            VerifyWireState(wireState);
        }
        catch (Exception mutationFailure)
        {
            RestorePlans(new[] { plan }, wireState, component, mutationFailure);
            throw;
        }

        component.ExpireSolution(recompute: false);
        document.NewSolution(expireAllObjects: false);
        var after = ReadState(component);
        return Task.FromResult(new WireifyMutationResult(
            request.OperationId,
            Changed: true,
            PythonComponentFingerprint.Compute(before),
            PythonComponentFingerprint.Compute(after),
            after.RuntimeMessages));
    }

    protected override Task<PythonExecutionResult> ExecuteCoreAsync(
        GH_Document document,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperation(request.OperationId, request.ComponentId);
        var component = ResolveComponent(document, request.ComponentId);
        if (request.ExpireUpstream && component is IGH_Component ghComponent)
        {
            foreach (var source in ghComponent.Params.Input.SelectMany(input => input.Sources))
            {
                source.ExpireSolution(recompute: false);
            }
        }

        component.ExpireSolution(recompute: false);
        document.NewSolution(expireAllObjects: request.RecomputeDocument);
        var state = ReadState(component);
        var solved = state.RuntimeMessages.All(message => message.Level != RuntimeMessageLevel.Error);
        return Task.FromResult(new PythonExecutionResult(
            request.OperationId,
            request.ComponentId,
            solved,
            PythonComponentFingerprint.Compute(state),
            state.RuntimeMessages));
    }

    protected override Task<IReadOnlyList<ComponentRuntimeMessage>> ReadRuntimeMessagesCoreAsync(
        GH_Document document,
        Guid componentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ComponentRuntimeMessage>>(
            ReadMessages(ResolveComponent(document, componentId)));
    }

    private static IGH_DocumentObject ResolveComponent(GH_Document document, Guid componentId)
    {
        if (componentId == Guid.Empty)
        {
            throw new InvalidOperationException("ComponentId is required.");
        }
        var component = document.FindObject(componentId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {componentId:D} was not found.");
        var runtime = RuntimeOf(component);
        if (runtime == PythonRuntime.Cpython3 && FindInterface(component, ScriptComponentInterface) is null)
        {
            throw new NotSupportedException(
                $"Grasshopper object {componentId:D} is not a supported Rhino 8 CPython component.");
        }
        return component;
    }

    private static PythonRuntime RuntimeOf(IGH_DocumentObject component)
    {
        if (component.ComponentGuid == Cpython3ComponentGuid)
        {
            return PythonRuntime.Cpython3;
        }
        if (component.ComponentGuid == IronPython2ComponentGuid)
        {
            return PythonRuntime.IronPython2;
        }
        throw new NotSupportedException(
            $"Grasshopper object {component.InstanceGuid:D} is not a Python script component.");
    }

    private static PythonComponentState ReadState(IGH_DocumentObject component)
    {
        var runtime = RuntimeOf(component);
        var source = ReadExecutableSource(component, runtime);
        return new PythonComponentState(
            component.InstanceGuid,
            source,
            Hash(source),
            runtime,
            ReadParameterObjects(component, "Inputs").Select(ToParameter).ToArray(),
            ReadParameterObjects(component, "Outputs").Select(ToParameter).ToArray(),
            ReadMessages(component));
    }

    private static string ReadExecutableSource(IGH_DocumentObject component, PythonRuntime runtime)
    {
        if (runtime == PythonRuntime.IronPython2)
        {
            return component.GetType().GetProperty("Code")?.GetValue(component) as string
                ?? throw new InvalidOperationException("IronPython Code is unavailable.");
        }

        foreach (var method in component.GetType().GetMethods())
        {
            if (!string.Equals(method.Name, "TryGetSource", StringComparison.Ordinal))
            {
                continue;
            }
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].IsOut ||
                parameters[0].ParameterType.GetElementType() != typeof(string))
            {
                continue;
            }
            var arguments = new object?[] { null };
            if (method.Invoke(component, arguments) is true && arguments[0] is string source)
            {
                return source;
            }
            break;
        }

        throw new InvalidOperationException(
            "RhinoCode TryGetSource(out string) is unavailable; refusing to use display-only Text.");
    }

    private static void SetExecutableSource(
        IGH_DocumentObject component,
        PythonRuntime runtime,
        string source)
    {
        try
        {
            if (runtime == PythonRuntime.IronPython2)
            {
                var property = component.GetType().GetProperty("Code")
                    ?? throw new MissingMemberException(component.GetType().FullName, "Code");
                if (!property.CanWrite)
                {
                    throw new InvalidOperationException("IronPython Code is read-only.");
                }
                property.SetValue(component, source);
                return;
            }

            var method = component.GetType().GetMethod("SetSource", new[] { typeof(string) })
                ?? throw new MissingMethodException(component.GetType().FullName, "SetSource(string)");
            method.Invoke(component, new object[] { source });
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }
    }

    private static void Rebuild(IGH_DocumentObject component, PythonRuntime runtime)
    {
        MethodInfo? rebuild = null;
        foreach (var contract in component.GetType().GetInterfaces())
        {
            if (string.Equals(contract.Name, "IScriptObject", StringComparison.Ordinal))
            {
                rebuild = contract.GetMethod("ReBuild", Type.EmptyTypes);
                if (rebuild is not null)
                {
                    break;
                }
            }
        }

        if (rebuild is null)
        {
            if (runtime == PythonRuntime.Cpython3)
            {
                throw new MissingMethodException(component.GetType().FullName, "IScriptObject.ReBuild()");
            }
            return;
        }

        try
        {
            if (rebuild.Invoke(component, null) is Task task && !task.Wait(RebuildTimeout))
            {
                throw new TimeoutException(
                    $"RhinoCode rebuild exceeded {RebuildTimeout.TotalSeconds:0} seconds.");
            }
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
        {
            throw new InvalidOperationException(
                exception.InnerException?.Message ?? exception.Message,
                exception.InnerException ?? exception);
        }
    }

    private static string EnsurePython3Directive(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var trimmed = source.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("#!", StringComparison.Ordinal) &&
            !trimmed.StartsWith(Python3Directive, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CPython source has an incompatible language directive; expected '{Python3Directive}'.");
        }
        return trimmed.StartsWith(Python3Directive, StringComparison.Ordinal)
            ? trimmed
            : Python3Directive + "\n" + source;
    }

    private static IReadOnlyList<ScriptParameterObject> ReadParameterObjects(
        object component,
        string property)
    {
        var enumerable = GetInterfaceProperty(component, ScriptComponentInterface, property) as IEnumerable
            ?? throw new InvalidOperationException($"Script component {property} are unavailable.");
        var result = new List<ScriptParameterObject>();
        foreach (var scriptParameter in enumerable)
        {
            if (scriptParameter is null)
            {
                continue;
            }
            var ghParameter = scriptParameter as IGH_Param;
            result.Add(new ScriptParameterObject(
                ghParameter?.InstanceGuid ?? Guid.Empty,
                scriptParameter,
                ghParameter));
        }
        return result;
    }

    private static PythonParameter ToParameter(ScriptParameterObject item)
    {
        var variableName = GetInterfaceProperty(
            item.ScriptParameter,
            ScriptParameterInterface,
            "VariableName") as string ?? item.GrasshopperParameter?.Name ?? string.Empty;
        var prettyName = GetInterfaceProperty(
            item.ScriptParameter,
            ScriptParameterInterface,
            "PrettyName") as string ?? item.GrasshopperParameter?.NickName ?? variableName;
        var converter = GetInterfaceProperty(
            item.ScriptParameter,
            ScriptParameterInterface,
            "Converter");
        var typeHint = ConverterTypeName(converter);
        var accessName = GetInterfaceProperty(
            item.ScriptParameter,
            ScriptParameterInterface,
            "Access")?.ToString();
        var access = accessName?.ToLowerInvariant() switch
        {
            "list" => ParameterAccess.List,
            "tree" => ParameterAccess.Tree,
            _ => ParameterAccess.Item
        };
        return new PythonParameter(
            item.ParameterId,
            variableName,
            prettyName,
            typeHint,
            access,
            item.GrasshopperParameter?.Optional ?? false);
    }

    private static void ValidateSchema(
        IReadOnlyList<PythonParameter> inputs,
        IReadOnlyList<PythonParameter> outputs)
    {
        var all = inputs.Concat(outputs).ToArray();
        if (all.Any(parameter => parameter.ParameterId == Guid.Empty))
        {
            throw new InvalidOperationException("Every existing socket must retain its non-empty ParameterId.");
        }
        if (all.GroupBy(parameter => parameter.ParameterId).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Socket ParameterIds must be unique.");
        }
        foreach (var parameter in all)
        {
            if (!IsPythonIdentifier(parameter.Name))
            {
                throw new InvalidOperationException(
                    $"'{parameter.Name}' is not a safe Python variable name.");
            }
            if (string.IsNullOrWhiteSpace(parameter.NickName))
            {
                throw new InvalidOperationException("Socket NickName is required.");
            }
            _ = ResolveSafeType(parameter.TypeHint, allowObject: true);
        }
        if (all.GroupBy(parameter => parameter.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Python socket variable names must be unique.");
        }
    }

    private static IReadOnlyList<ParameterMutationPlan> PrepareSchema(
        IReadOnlyList<ScriptParameterObject> actual,
        IReadOnlyList<PythonParameter> requested)
    {
        var result = new List<ParameterMutationPlan>(actual.Count);
        for (var index = 0; index < actual.Count; index++)
        {
            var current = actual[index];
            var desired = requested[index];
            if (current.ParameterId != desired.ParameterId)
            {
                throw new InvalidOperationException(
                    $"Socket identity changed at schema position {index}.");
            }
            var variable = RequireWritableProperty(current.ScriptParameter, "VariableName");
            var pretty = RequireWritableProperty(current.ScriptParameter, "PrettyName");
            var access = RequireWritableProperty(current.ScriptParameter, "Access");
            var converter = RequireWritableProperty(current.ScriptParameter, "Converter");
            var accessValue = Enum.Parse(access.PropertyType, desired.Access.ToString(), ignoreCase: true);
            var converterValue = PrepareConverter(converter, current.ScriptParameter, desired.TypeHint);
            var snapshot = new ParameterSnapshot(
                variable.GetValue(current.ScriptParameter),
                pretty.GetValue(current.ScriptParameter),
                access.GetValue(current.ScriptParameter),
                converter.GetValue(current.ScriptParameter),
                current.GrasshopperParameter?.Name,
                current.GrasshopperParameter?.NickName,
                current.GrasshopperParameter?.Optional ?? false);
            var isNoOp = string.Equals(snapshot.VariableName as string, desired.Name, StringComparison.Ordinal) &&
                string.Equals(snapshot.PrettyName as string, desired.NickName, StringComparison.Ordinal) &&
                Equals(snapshot.Access, accessValue) &&
                ConverterEquivalent(snapshot.Converter, converterValue) &&
                (current.GrasshopperParameter is null ||
                    (string.Equals(snapshot.GhName, desired.Name, StringComparison.Ordinal) &&
                     string.Equals(snapshot.GhNickName, desired.NickName, StringComparison.Ordinal) &&
                     snapshot.GhOptional == desired.Optional));
            result.Add(new ParameterMutationPlan(
                current,
                desired,
                variable,
                pretty,
                access,
                converter,
                accessValue,
                converterValue,
                snapshot,
                isNoOp));
        }
        return result;
    }

    private static void ApplyPlan(ParameterMutationPlan plan)
    {
        if (plan.IsNoOp)
        {
            return;
        }
        SetProperty(plan.VariableProperty, plan.Target.ScriptParameter, plan.Desired.Name);
        SetProperty(plan.PrettyProperty, plan.Target.ScriptParameter, plan.Desired.NickName);
        SetProperty(plan.AccessProperty, plan.Target.ScriptParameter, plan.AccessValue);
        SetProperty(plan.ConverterProperty, plan.Target.ScriptParameter, plan.ConverterValue);
        if (plan.Target.GrasshopperParameter is { } parameter)
        {
            parameter.Name = plan.Desired.Name;
            parameter.NickName = plan.Desired.NickName;
            parameter.Optional = plan.Desired.Optional;
        }
    }

    private static void RestorePlans(
        IReadOnlyList<ParameterMutationPlan> plans,
        IReadOnlyList<WireSnapshot> wireState,
        IGH_DocumentObject component,
        Exception mutationFailure)
    {
        try
        {
            foreach (var plan in plans.Reverse())
            {
                SetProperty(plan.VariableProperty, plan.Target.ScriptParameter, plan.Snapshot.VariableName);
                SetProperty(plan.PrettyProperty, plan.Target.ScriptParameter, plan.Snapshot.PrettyName);
                SetProperty(plan.AccessProperty, plan.Target.ScriptParameter, plan.Snapshot.Access);
                SetProperty(plan.ConverterProperty, plan.Target.ScriptParameter, plan.Snapshot.Converter);
                if (plan.Target.GrasshopperParameter is { } parameter)
                {
                    parameter.Name = plan.Snapshot.GhName ?? string.Empty;
                    parameter.NickName = plan.Snapshot.GhNickName ?? string.Empty;
                    parameter.Optional = plan.Snapshot.GhOptional;
                }
            }
            SynchronizeParameters(component);
            RestoreWireState(wireState);
        }
        catch (Exception rollbackFailure)
        {
            throw new AggregateException(
                "Python schema update failed and its in-place rollback also failed; use Grasshopper Undo.",
                mutationFailure,
                rollbackFailure);
        }
    }

    private static PropertyInfo RequireWritableProperty(object target, string name)
    {
        var property = FindInterface(target, ScriptParameterInterface)?.GetProperty(name)
            ?? throw new InvalidOperationException($"Script parameter {name} is unavailable.");
        if (!property.CanWrite)
        {
            throw new InvalidOperationException($"Script parameter {name} is read-only.");
        }
        return property;
    }

    private static void SetProperty(PropertyInfo property, object target, object? value)
    {
        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }
    }

    private static object? PrepareConverter(PropertyInfo property, object parameter, string typeHint)
    {
        var targetType = ResolveSafeType(typeHint, allowObject: true);
        var existing = property.GetValue(parameter);
        if (targetType == typeof(object))
        {
            return null;
        }
        if (ConverterTargets(existing, targetType))
        {
            return existing;
        }

        var codeAssembly = property.PropertyType.Assembly;
        var identityType = codeAssembly.GetType(
            "Rhino.Runtime.Code.Execution.ParamConverterIdentity",
            throwOnError: true)!;
        var paramType = codeAssembly.GetType("Rhino.Runtime.Code.ParamType", throwOnError: true)!;
        var converterType = codeAssembly.GetType(
            "Rhino.Runtime.Code.Execution.ParamValueCastConverter",
            throwOnError: true)!;
        var identity = Activator.CreateInstance(
            identityType,
            StableGuid(typeHint),
            $"GPTino {targetType.Name}",
            $"gptino/{targetType.Name.ToLowerInvariant()}")
            ?? throw new InvalidOperationException("Could not create a RhinoCode converter identity.");
        var target = Activator.CreateInstance(paramType, targetType)
            ?? throw new InvalidOperationException("Could not create a RhinoCode parameter type.");
        return Activator.CreateInstance(converterType, identity, target)
            ?? throw new InvalidOperationException("Could not create a RhinoCode value converter.");
    }

    private static Type ResolveSafeType(string typeHint, bool allowObject)
    {
        var resolved = typeHint?.Trim().ToLowerInvariant() switch
        {
            "object" or "system.object" when allowObject => typeof(object),
            "bool" or "boolean" or "system.boolean" => typeof(bool),
            "int" or "int32" or "integer" or "system.int32" => typeof(int),
            "float" or "single" or "system.single" => typeof(float),
            "double" or "number" or "system.double" => typeof(double),
            "string" or "text" or "system.string" => typeof(string),
            _ => null
        };
        return resolved ?? throw new NotSupportedException(
            $"Type hint '{typeHint}' is not in GPTino's safe primitive converter set.");
    }

    private static bool ConverterTargets(object? converter, Type targetType)
    {
        if (converter is null)
        {
            return targetType == typeof(object);
        }
        var typeName = converter.GetType().GetProperty("TypeName")?.GetValue(converter)?.ToString();
        if (string.Equals(typeName, targetType.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeName, targetType.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var target = converter.GetType().GetProperty("TargetType")?.GetValue(converter);
        return target is Type type && type == targetType;
    }

    private static bool ConverterEquivalent(object? left, object? right) =>
        ReferenceEquals(left, right) ||
        string.Equals(ConverterTypeName(left), ConverterTypeName(right), StringComparison.OrdinalIgnoreCase);

    private static string ConverterTypeName(object? converter)
    {
        if (converter is null)
        {
            return "object";
        }
        var typeName = converter.GetType().GetProperty("TypeName")?.GetValue(converter)?.ToString();
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return typeName;
        }
        var target = converter.GetType().GetProperty("TargetType")?.GetValue(converter);
        return target is Type type ? type.FullName ?? type.Name : converter.ToString() ?? "object";
    }

    private static void SynchronizeParameters(IGH_DocumentObject component)
    {
        if (component is IGH_VariableParameterComponent variable)
        {
            variable.VariableParameterMaintenance();
        }
        else
        {
            component.GetType().GetMethod("VariableParameterMaintenance", Type.EmptyTypes)
                ?.Invoke(component, null);
        }
        if (component is IGH_Component ghComponent)
        {
            ghComponent.Params.OnParametersChanged();
        }
        component.Attributes?.ExpireLayout();
    }

    private static IReadOnlyList<WireSnapshot> CaptureWireState(
        IEnumerable<ScriptParameterObject> parameters)
    {
        var direct = parameters
            .Where(item => item.GrasshopperParameter is not null)
            .Select(item => item.GrasshopperParameter!)
            .ToArray();
        return direct
            .Concat(direct.SelectMany(parameter => parameter.Recipients))
            .Distinct(ParameterReferenceComparer.Instance)
            .Select(parameter => new WireSnapshot(parameter, parameter.Sources.ToArray()))
            .ToArray();
    }

    private static void VerifyWireState(IReadOnlyList<WireSnapshot> expected)
    {
        foreach (var snapshot in expected)
        {
            var sources = snapshot.Parameter.Sources.Select(source => source.InstanceGuid).ToArray();
            if (!sources.SequenceEqual(snapshot.Sources.Select(source => source.InstanceGuid)))
            {
                throw new InvalidOperationException(
                    $"Parameter {snapshot.Parameter.InstanceGuid:D} wiring changed during a schema-only update.");
            }
        }
    }

    private static void RestoreWireState(IReadOnlyList<WireSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            snapshot.Parameter.RemoveAllSources();
            foreach (var source in snapshot.Sources)
            {
                snapshot.Parameter.AddSource(source);
            }
        }
    }

    private static void VerifySocketIdentity(
        IReadOnlyList<ScriptParameterObject> actual,
        IReadOnlyList<PythonParameter> expected)
    {
        var current = actual.Select(item => item.GrasshopperParameter?.InstanceGuid ?? Guid.Empty).ToArray();
        var requested = expected.Select(item => item.ParameterId).ToArray();
        if (!current.SequenceEqual(requested))
        {
            throw new InvalidOperationException("Python socket identity changed during schema synchronization.");
        }
    }

    private static bool IsPythonIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }
        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static IReadOnlyList<ComponentRuntimeMessage> ReadMessages(object component)
    {
        if (component is not IGH_ActiveObject active)
        {
            return Array.Empty<ComponentRuntimeMessage>();
        }
        return new[]
            {
                (GH_RuntimeMessageLevel.Remark, RuntimeMessageLevel.Remark),
                (GH_RuntimeMessageLevel.Warning, RuntimeMessageLevel.Warning),
                (GH_RuntimeMessageLevel.Error, RuntimeMessageLevel.Error)
            }
            .SelectMany(pair => active.RuntimeMessages(pair.Item1)
                .Select(message => new ComponentRuntimeMessage(pair.Item2, message)))
            .ToArray();
    }

    private static object? GetInterfaceProperty(object target, string interfaceName, string property) =>
        FindInterface(target, interfaceName)?.GetProperty(property)?.GetValue(target);

    private static Type? FindInterface(object target, string fullName) =>
        target.GetType().GetInterfaces().FirstOrDefault(item =>
            string.Equals(item.FullName, fullName, StringComparison.Ordinal));

    private static void RequireOperation(string operationId, Guid componentId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException("OperationId is required.");
        }
        if (componentId == Guid.Empty)
        {
            throw new InvalidOperationException("ComponentId is required.");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static Guid StableGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"gptino-converter:{value.ToLowerInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record ScriptParameterObject(
        Guid ParameterId,
        object ScriptParameter,
        IGH_Param? GrasshopperParameter);

    private sealed record ParameterSnapshot(
        object? VariableName,
        object? PrettyName,
        object? Access,
        object? Converter,
        string? GhName,
        string? GhNickName,
        bool GhOptional);

    private sealed record ParameterMutationPlan(
        ScriptParameterObject Target,
        PythonParameter Desired,
        PropertyInfo VariableProperty,
        PropertyInfo PrettyProperty,
        PropertyInfo AccessProperty,
        PropertyInfo ConverterProperty,
        object AccessValue,
        object? ConverterValue,
        ParameterSnapshot Snapshot,
        bool IsNoOp);

    private sealed record WireSnapshot(
        IGH_Param Parameter,
        IReadOnlyList<IGH_Param> Sources);

    private sealed class ParameterReferenceComparer : IEqualityComparer<IGH_Param>
    {
        public static ParameterReferenceComparer Instance { get; } = new();

        public bool Equals(IGH_Param? x, IGH_Param? y) => ReferenceEquals(x, y);

        public int GetHashCode(IGH_Param obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
