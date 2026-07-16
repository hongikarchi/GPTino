using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GPTino.BridgeContract;

public sealed record PipeEndpoint
{
    private const int MaximumNameLength = 128;

    private PipeEndpoint(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public static PipeEndpoint ForProject(string projectId, int rhinoProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (rhinoProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rhinoProcessId));
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(projectId.Trim())))[..20].ToLowerInvariant();
        return new PipeEndpoint($"gptino-{rhinoProcessId}-{hash}");
    }

    public static PipeEndpoint FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumNameLength ||
            !name.StartsWith("gptino-", StringComparison.Ordinal) ||
            name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "Pipe name must start with 'gptino-', contain only ASCII letters, digits, or hyphens, " +
                $"and be at most {MaximumNameLength} characters.",
                nameof(name));
        }

        return new PipeEndpoint(name);
    }
}

public sealed class DocumentPipeConnection : IAsyncDisposable
{
    private readonly PipeStream _stream;
    private readonly BridgeFrameCodec _codec;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Action? _onDispose;
    private int _disposed;

    internal DocumentPipeConnection(
        PipeStream stream,
        BridgeFrameCodec codec,
        Action? onDispose = null)
    {
        _stream = stream;
        _codec = codec;
        _onDispose = onDispose;
    }

    public bool IsConnected => _stream.IsConnected;

    public async ValueTask SendAsync(BridgeFrame frame, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _codec.WriteAsync(_stream, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<BridgeFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _codec.ReadAsync(_stream, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Dispose();
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}

public sealed class DocumentPipeClient
{
    private readonly PipeEndpoint _endpoint;
    private readonly BridgeSecret _secret;
    private readonly BridgeFrameCodec _codec;

    public DocumentPipeClient(
        PipeEndpoint endpoint,
        BridgeSecret secret,
        BridgeFrameCodec? codec = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        _codec = codec ?? new BridgeFrameCodec();
    }

    public async Task<DocumentPipeConnection> ConnectAsync(
        string clientId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stream = new NamedPipeClientStream(
            ".",
            _endpoint.Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            System.Security.Principal.TokenImpersonationLevel.Identification);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await stream.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            var clientNonce = BridgeAuthenticator.CreateNonce();
            var helloFrame = BridgeFrame.Create(
                BridgeMessageKind.Hello,
                nameof(BridgeHello),
                new BridgeHello(clientId, clientNonce));
            await _codec.WriteAsync(
                stream,
                helloFrame,
                timeoutCts.Token).ConfigureAwait(false);

            var challengeFrame = await _codec.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            RequireHandshakeFrame(
                challengeFrame,
                BridgeMessageKind.Challenge,
                nameof(BridgeChallenge),
                helloFrame.MessageId);
            var challenge = challengeFrame.DeserializePayload<BridgeChallenge>();
            if (!BridgeAuthenticator.VerifyServerProof(
                    _secret,
                    _endpoint.Name,
                    clientNonce,
                    challenge.ServerNonce,
                    challenge.ServerId,
                    challenge.ServerProof))
            {
                throw new UnauthorizedAccessException(
                    "GPTino bridge server authentication failed.");
            }

            var proof = BridgeAuthenticator.CreateClientProof(
                _secret,
                _endpoint.Name,
                clientNonce,
                challenge.ServerNonce,
                clientId,
                challenge.ServerId);

            var authFrame = BridgeFrame.Create(
                BridgeMessageKind.Authenticate,
                nameof(BridgeAuthenticate),
                new BridgeAuthenticate(proof),
                correlationId: challengeFrame.MessageId);
            await _codec.WriteAsync(
                stream,
                authFrame,
                timeoutCts.Token).ConfigureAwait(false);

            var result = await _codec.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            RequireHandshakeCorrelation(result, authFrame.MessageId);
            if (result.Kind == BridgeMessageKind.Error)
            {
                throw new UnauthorizedAccessException("GPTino bridge authentication was rejected.");
            }

            RequireHandshakeFrame(
                result,
                BridgeMessageKind.Authenticated,
                nameof(BridgeAuthenticated),
                authFrame.MessageId);
            var authenticated = result.DeserializePayload<BridgeAuthenticated>();
            if (!string.Equals(
                    authenticated.ServerId,
                    challenge.ServerId,
                    StringComparison.Ordinal))
            {
                throw new BridgeProtocolException(
                    "server_identity",
                    "Authenticated server identity does not match the proven challenge identity.");
            }

            return new DocumentPipeConnection(stream, _codec);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new BridgeProtocolException(
                "invalid_handshake_payload",
                "The bridge server returned an invalid authentication payload.",
                exception);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void RequireHandshakeFrame(
        BridgeFrame frame,
        BridgeMessageKind expectedKind,
        string expectedPayloadType,
        Guid expectedCorrelationId)
    {
        if (frame.Kind != expectedKind ||
            !string.Equals(frame.PayloadType, expectedPayloadType, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "handshake_sequence",
                $"Expected {expectedKind}/{expectedPayloadType}, got {frame.Kind}/{frame.PayloadType}.");
        }

        RequireHandshakeCorrelation(frame, expectedCorrelationId);
    }

    private static void RequireHandshakeCorrelation(BridgeFrame frame, Guid expectedCorrelationId)
    {
        if (frame.CorrelationId != expectedCorrelationId)
        {
            throw new BridgeProtocolException(
                "handshake_correlation",
                $"Expected handshake correlation {expectedCorrelationId}, got {frame.CorrelationId?.ToString() ?? "none"}.");
        }
    }
}

public sealed class DocumentPipeServer
{
    public static TimeSpan DefaultHandshakeTimeout { get; } = TimeSpan.FromSeconds(3);

    private readonly PipeEndpoint _endpoint;
    private readonly BridgeSecret _secret;
    private readonly BridgeFrameCodec _codec;
    private readonly string _serverId;
    private readonly TimeSpan _handshakeTimeout;
    private readonly SemaphoreSlim _acceptGate = new(1, 1);

    public DocumentPipeServer(
        PipeEndpoint endpoint,
        BridgeSecret secret,
        string serverId,
        BridgeFrameCodec? codec = null,
        TimeSpan? handshakeTimeout = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        _serverId = serverId;
        _codec = codec ?? new BridgeFrameCodec();
        _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        if (_handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }
    }

    public async Task<DocumentPipeConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        await _acceptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var releaseAcceptGate = true;
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance;
        }

        NamedPipeServerStream? stream = null;

        try
        {
            stream = new NamedPipeServerStream(
                _endpoint.Name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                options);
            await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(_handshakeTimeout);
            try
            {
                var helloFrame = await _codec.ReadAsync(stream, handshakeCts.Token).ConfigureAwait(false);
                RequireHandshakeFrame(
                    helloFrame,
                    BridgeMessageKind.Hello,
                    nameof(BridgeHello),
                    expectedCorrelationId: null);
                var hello = helloFrame.DeserializePayload<BridgeHello>();
                ArgumentException.ThrowIfNullOrWhiteSpace(hello.ClientId);

                var serverNonce = BridgeAuthenticator.CreateNonce();
                var serverProof = BridgeAuthenticator.CreateServerProof(
                    _secret,
                    _endpoint.Name,
                    hello.ClientNonce,
                    serverNonce,
                    _serverId);
                var challengeFrame = BridgeFrame.Create(
                    BridgeMessageKind.Challenge,
                    nameof(BridgeChallenge),
                    new BridgeChallenge(serverNonce, _serverId, serverProof),
                    correlationId: helloFrame.MessageId);
                await _codec.WriteAsync(stream, challengeFrame, handshakeCts.Token).ConfigureAwait(false);

                var authFrame = await _codec.ReadAsync(stream, handshakeCts.Token).ConfigureAwait(false);
                RequireHandshakeFrame(
                    authFrame,
                    BridgeMessageKind.Authenticate,
                    nameof(BridgeAuthenticate),
                    challengeFrame.MessageId);
                var auth = authFrame.DeserializePayload<BridgeAuthenticate>();
                if (!BridgeAuthenticator.VerifyClientProof(
                        _secret,
                        _endpoint.Name,
                        hello.ClientNonce,
                        serverNonce,
                        hello.ClientId,
                        _serverId,
                        auth.Proof))
                {
                    await _codec.WriteAsync(
                        stream,
                        BridgeFrame.Create(
                            BridgeMessageKind.Error,
                            "authentication_failed",
                            new { message = "Authentication failed." },
                            correlationId: authFrame.MessageId) with
                        {
                            ErrorCode = "authentication_failed",
                        },
                        handshakeCts.Token).ConfigureAwait(false);
                    throw new UnauthorizedAccessException("GPTino bridge authentication failed.");
                }

                await _codec.WriteAsync(
                    stream,
                    BridgeFrame.Create(
                        BridgeMessageKind.Authenticated,
                        nameof(BridgeAuthenticated),
                        new BridgeAuthenticated(_serverId),
                        correlationId: authFrame.MessageId),
                    handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested && handshakeCts.IsCancellationRequested)
            {
                throw new BridgeProtocolException(
                    "handshake_timeout",
                    $"Bridge authentication did not complete within {_handshakeTimeout.TotalSeconds:0.###} seconds.",
                    exception);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                throw new BridgeProtocolException(
                    "invalid_handshake_payload",
                    "The bridge client sent an invalid authentication payload.",
                    exception);
            }

            releaseAcceptGate = false;
            return new DocumentPipeConnection(stream, _codec, () => _acceptGate.Release());
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (releaseAcceptGate)
            {
                _acceptGate.Release();
            }
        }
    }

    private static void RequireHandshakeFrame(
        BridgeFrame frame,
        BridgeMessageKind expectedKind,
        string expectedPayloadType,
        Guid? expectedCorrelationId)
    {
        if (frame.Kind != expectedKind ||
            !string.Equals(frame.PayloadType, expectedPayloadType, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "handshake_sequence",
                $"Expected {expectedKind}/{expectedPayloadType}, got {frame.Kind}/{frame.PayloadType}.");
        }

        if (frame.CorrelationId != expectedCorrelationId)
        {
            throw new BridgeProtocolException(
                "handshake_correlation",
                $"Expected handshake correlation {expectedCorrelationId?.ToString() ?? "none"}, got {frame.CorrelationId?.ToString() ?? "none"}.");
        }
    }
}
