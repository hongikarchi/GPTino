using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;

const string BridgeSecretEnvironment = "GPTINO_BRIDGE_SECRET";
const string FingerprintEnvironment = "GPTINO_SMOKE_FINGERPRINT";

try
{
    var options = SmokeBridgeOptions.Parse(args);
    var encodedSecret = Environment.GetEnvironmentVariable(BridgeSecretEnvironment);
    Environment.SetEnvironmentVariable(BridgeSecretEnvironment, null);
    if (string.IsNullOrWhiteSpace(encodedSecret))
    {
        throw new InvalidOperationException($"{BridgeSecretEnvironment} is required.");
    }
    var documentFingerprint = Environment.GetEnvironmentVariable(FingerprintEnvironment);
    Environment.SetEnvironmentVariable(FingerprintEnvironment, null);
    if (string.IsNullOrWhiteSpace(documentFingerprint))
    {
        throw new InvalidOperationException($"{FingerprintEnvironment} is required.");
    }

    var secret = BridgeSecret.FromBase64(encodedSecret);
    var target = DocumentRuntimeTarget.Create(
        options.ProjectId,
        Environment.ProcessId,
        Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        options.RhinoDocumentSerial,
        options.GrasshopperDocumentId,
        options.RhinoPath,
        options.GrasshopperPath);
    var endpoint = PipeEndpoint.FromName(options.PipeName);
    await using var connection = await new DocumentPipeClient(endpoint, secret).ConnectAsync(
        "gptino-smoke-bridge",
        TimeSpan.FromSeconds(15));

    var registration = BridgeFrame.Create(
        BridgeMessageKind.Event,
        BridgeMessageTypes.RegisterDocument,
        new RegisterDocumentRequest(
            "gptino-smoke-bridge",
            "0.1.0",
            [BridgeAdapterOwner.CordycepsCanvas]),
        target);
    await connection.SendAsync(registration);
    var registered = await connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    if (registered.Kind != BridgeMessageKind.Response ||
        !string.Equals(
            registered.PayloadType,
            BridgeMessageTypes.DocumentRegistered,
            StringComparison.Ordinal) ||
        registered.CorrelationId != registration.MessageId)
    {
        throw new BridgeProtocolException(
            "smoke_registration",
            $"Smoke registration was rejected as {registered.Kind}/{registered.PayloadType}.");
    }
    DocumentTargetGuard.RequireCurrent(target, registered.Target!);
    var registrationResult = registered.DeserializePayload<DocumentRegisteredResponse>();
    if (!string.Equals(registrationResult.InstanceId, "gptino-smoke-bridge", StringComparison.Ordinal) ||
        !string.Equals(registrationResult.TargetKey, target.StableTargetKey(), StringComparison.Ordinal) ||
        registrationResult.Generation != target.Generation ||
        !registrationResult.AvailableAdapters.SequenceEqual([BridgeAdapterOwner.CordycepsCanvas]))
    {
        throw new BridgeProtocolException(
            "smoke_registration_payload",
            "Smoke registration response did not preserve the exact target and adapter contract.");
    }

    Console.Out.WriteLine("GPTINO_SMOKE_BRIDGE_READY");
    Console.Out.Flush();
    var evidence = ComputeEvidencePrefix(documentFingerprint);
    var snapshotServed = false;

    while (connection.IsConnected)
    {
        BridgeFrame frame;
        try
        {
            frame = await connection.ReceiveAsync();
        }
        catch (EndOfStreamException)
        {
            return snapshotServed ? 0 : 3;
        }
        catch (IOException exception) when (exception is not BridgeProtocolException)
        {
            return snapshotServed ? 0 : 3;
        }

        if (frame.Kind != BridgeMessageKind.Request ||
            !string.Equals(frame.PayloadType, BridgeMessageTypes.OperationRequest, StringComparison.Ordinal))
        {
            throw new SmokeProtocolViolationException("unexpected-frame");
        }

        DocumentTargetGuard.RequireCurrent(target, frame.Target!);
        var request = frame.DeserializePayload<BridgeOperationRequest>();
        request.Validate();
        if (snapshotServed)
        {
            throw new SmokeProtocolViolationException("multiple-requests");
        }
        if (frame.CorrelationId is not null ||
            request.Owner != BridgeAdapterOwner.CordycepsCanvas ||
            request.Access != BridgeOperationAccess.Read ||
            !string.Equals(request.Operation, "canvas.snapshot", StringComparison.Ordinal) ||
            request.BaseSnapshotRevision != 0 ||
            request.ExpectedFingerprint is not null ||
            request.WriterLeaseToken is not null ||
            request.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object ||
            request.Arguments.EnumerateObject().Any())
        {
            throw new SmokeProtocolViolationException("unexpected-operation");
        }

        snapshotServed = true;
        var snapshot = new CanvasSnapshot(
            options.GrasshopperDocumentId,
            documentFingerprint,
            Array.Empty<CanvasObjectState>(),
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
        var response = BridgeFrame.Create(
            BridgeMessageKind.Response,
            BridgeMessageTypes.OperationResponse,
            BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                snapshot,
                afterFingerprint: documentFingerprint),
            target,
            frame.MessageId);
        await connection.SendAsync(response);
        Console.Out.WriteLine($"GPTINO_SMOKE_SNAPSHOT sha256={evidence}");
        Console.Out.Flush();
    }

    return 0;
}
catch (SmokeProtocolViolationException exception)
{
    Console.Out.WriteLine($"GPTINO_SMOKE_VIOLATION code={exception.Code}");
    Console.Out.Flush();
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"GPTino smoke bridge failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static string ComputeEvidencePrefix(string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    try
    {
        var hash = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(bytes);
    }
}

internal sealed class SmokeProtocolViolationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed record SmokeBridgeOptions(
    string PipeName,
    Guid ProjectId,
    string RhinoPath,
    string GrasshopperPath,
    uint RhinoDocumentSerial,
    Guid GrasshopperDocumentId)
{
    private static readonly HashSet<string> AllowedArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pipe-name",
        "project-id",
        "rhino",
        "grasshopper",
        "rhino-document-serial",
        "grasshopper-document-id",
    };

    public static SmokeBridgeOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length ||
                !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Smoke bridge arguments must be --name value pairs.");
            }
            var name = arguments[index][2..];
            if (!AllowedArgumentNames.Contains(name))
            {
                throw new ArgumentException($"Unknown smoke bridge argument '{arguments[index]}'.");
            }
            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException($"Duplicate smoke bridge argument '{arguments[index]}'.");
            }
        }

        var pipeName = Required("pipe-name");
        var rhinoPath = Path.GetFullPath(Required("rhino"));
        var grasshopperPath = Path.GetFullPath(Required("grasshopper"));
        if (!Guid.TryParse(Required("project-id"), out var projectId) || projectId == Guid.Empty)
        {
            throw new ArgumentException("--project-id must be a non-empty UUID.");
        }
        if (!uint.TryParse(Required("rhino-document-serial"), out var documentSerial) ||
            documentSerial == 0)
        {
            throw new ArgumentException("--rhino-document-serial must be a positive UInt32.");
        }
        if (!Guid.TryParse(Required("grasshopper-document-id"), out var grasshopperDocumentId) ||
            grasshopperDocumentId == Guid.Empty)
        {
            throw new ArgumentException("--grasshopper-document-id must be a non-empty UUID.");
        }

        return new SmokeBridgeOptions(
            pipeName,
            projectId,
            rhinoPath,
            grasshopperPath,
            documentSerial,
            grasshopperDocumentId);

        string Required(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"--{name} is required.");
    }
}
