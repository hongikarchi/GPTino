using System.IO.Pipes;
using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class NamedPipeTransportTests
{
    [Fact]
    public async Task ClientAndServer_AuthenticateAndExchangeTargetedFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"test-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        var server = new DocumentPipeServer(PipeEndpoint.FromName(endpoint.Name), secret, "test-server");
        var client = new DocumentPipeClient(endpoint, secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask = server.AcceptAsync(cts.Token);
        await using var clientConnection = await client.ConnectAsync(
            "test-client",
            TimeSpan.FromSeconds(5),
            cts.Token);
        await using var serverConnection = await acceptTask;

        var expected = BridgeFrame.Create(
            BridgeMessageKind.Request,
            "test.request",
            new { operation = "read" },
            DocumentTargetTests.CreateTarget());
        var receiveTask = serverConnection.ReceiveAsync(cts.Token).AsTask();
        await clientConnection.SendAsync(expected, cts.Token);
        var actual = await receiveTask;

        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Target, actual.Target);
    }

    [Fact]
    public async Task Client_AcceptsValidFakeServerProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"fake-valid-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunFakeServerAsync(
            endpoint,
            secret,
            proofSecret: secret,
            wrongChallengeCorrelation: false,
            cts.Token);
        var client = new DocumentPipeClient(endpoint, secret);

        await using var connection = await client.ConnectAsync(
            "test-client",
            TimeSpan.FromSeconds(5),
            cts.Token);
        await serverTask;

        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task Client_RejectsFakeServerProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"fake-invalid-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunFakeServerAsync(
            endpoint,
            secret,
            proofSecret: BridgeSecret.Generate(),
            wrongChallengeCorrelation: false,
            cts.Token);
        var client = new DocumentPipeClient(endpoint, secret);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.ConnectAsync("test-client", TimeSpan.FromSeconds(5), cts.Token));
        await serverTask;
    }

    [Fact]
    public async Task Client_RejectsChallengeWithWrongCorrelation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"fake-correlation-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunFakeServerAsync(
            endpoint,
            secret,
            proofSecret: secret,
            wrongChallengeCorrelation: true,
            cts.Token);
        var client = new DocumentPipeClient(endpoint, secret);

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            () => client.ConnectAsync("test-client", TimeSpan.FromSeconds(5), cts.Token));
        await serverTask;

        Assert.Equal("handshake_correlation", exception.Code);
    }

    [Fact]
    public async Task Server_RejectsAuthenticateWithWrongCorrelation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"server-correlation-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        var codec = new BridgeFrameCodec();
        var server = new DocumentPipeServer(
            endpoint,
            secret,
            "test-server",
            handshakeTimeout: TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptAsync(cts.Token);
        await using var stream = CreateClientStream(endpoint);
        await stream.ConnectAsync(cts.Token);

        var clientNonce = BridgeAuthenticator.CreateNonce();
        var hello = BridgeFrame.Create(
            BridgeMessageKind.Hello,
            nameof(BridgeHello),
            new BridgeHello("test-client", clientNonce));
        await codec.WriteAsync(stream, hello, cts.Token);
        var challengeFrame = await codec.ReadAsync(stream, cts.Token);
        var challenge = challengeFrame.DeserializePayload<BridgeChallenge>();
        var clientProof = BridgeAuthenticator.CreateClientProof(
            secret,
            endpoint.Name,
            clientNonce,
            challenge.ServerNonce,
            "test-client",
            challenge.ServerId);
        var authenticate = BridgeFrame.Create(
            BridgeMessageKind.Authenticate,
            nameof(BridgeAuthenticate),
            new BridgeAuthenticate(clientProof),
            correlationId: Guid.NewGuid());
        await codec.WriteAsync(stream, authenticate, cts.Token);

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(() => acceptTask);
        Assert.Equal("handshake_correlation", exception.Code);
    }

    [Fact]
    public async Task Server_TimesOutStalledUnauthenticatedPeer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"server-timeout-{Guid.NewGuid():N}", Environment.ProcessId);
        var server = new DocumentPipeServer(
            endpoint,
            BridgeSecret.Generate(),
            "test-server",
            handshakeTimeout: TimeSpan.FromMilliseconds(150));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptAsync(cts.Token);
        await using var stream = CreateClientStream(endpoint);
        await stream.ConnectAsync(cts.Token);

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(() => acceptTask);
        Assert.Equal("handshake_timeout", exception.Code);
    }

    [Fact]
    public async Task Server_NormalizesMalformedHelloAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"server-malformed-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        var codec = new BridgeFrameCodec();
        var server = new DocumentPipeServer(endpoint, secret, "test-server");
        var client = new DocumentPipeClient(endpoint, secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = server.AcceptAsync(cts.Token);
        var malformedClient = CreateClientStream(endpoint);
        await malformedClient.ConnectAsync(cts.Token);
        await codec.WriteAsync(
            malformedClient,
            BridgeFrame.Create(
                BridgeMessageKind.Hello,
                nameof(BridgeHello),
                new BridgeHello("malformed-client", "not-a-valid-nonce")),
            cts.Token);

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(() => acceptTask);
        await malformedClient.DisposeAsync();
        Assert.Equal("invalid_handshake_payload", exception.Code);

        var retryAcceptTask = server.AcceptAsync(cts.Token);
        await using var clientConnection = await client.ConnectAsync(
            "test-client",
            TimeSpan.FromSeconds(5),
            cts.Token);
        await using var serverConnection = await retryAcceptTask;
        Assert.True(serverConnection.IsConnected);
    }

    [Fact]
    public async Task Server_ReconnectsAfterPriorConnectionIsDisposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"server-reconnect-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        var server = new DocumentPipeServer(endpoint, secret, "test-server");
        var client = new DocumentPipeClient(endpoint, secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var acceptTask = server.AcceptAsync(cts.Token);
            var clientConnection = await client.ConnectAsync(
                $"test-client-{attempt}",
                TimeSpan.FromSeconds(5),
                cts.Token);
            var serverConnection = await acceptTask;

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_RefusesPreexistingPipeAndCanRetryAfterItCloses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PipeEndpoint.ForProject($"server-squatter-{Guid.NewGuid():N}", Environment.ProcessId);
        var secret = BridgeSecret.Generate();
        var server = new DocumentPipeServer(endpoint, secret, "test-server");
        var client = new DocumentPipeClient(endpoint, secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preexistingPipe = CreateServerStream(endpoint);

        await Assert.ThrowsAsync<IOException>(() => server.AcceptAsync(cts.Token));
        await preexistingPipe.DisposeAsync();

        var acceptTask = server.AcceptAsync(cts.Token);
        await using var clientConnection = await client.ConnectAsync(
            "test-client",
            TimeSpan.FromSeconds(5),
            cts.Token);
        await using var serverConnection = await acceptTask;

        Assert.True(clientConnection.IsConnected);
        Assert.True(serverConnection.IsConnected);
    }

    [Theory]
    [InlineData("other-123")]
    [InlineData("gptino-has/slash")]
    [InlineData("gptino-has space")]
    public void Endpoint_RejectsUntrustedRawNames(string name)
    {
        Assert.Throws<ArgumentException>(() => PipeEndpoint.FromName(name));
    }

    private static async Task RunFakeServerAsync(
        PipeEndpoint endpoint,
        BridgeSecret authenticationSecret,
        BridgeSecret proofSecret,
        bool wrongChallengeCorrelation,
        CancellationToken cancellationToken)
    {
        var codec = new BridgeFrameCodec();
        await using var stream = CreateServerStream(endpoint);
        await stream.WaitForConnectionAsync(cancellationToken);
        var helloFrame = await codec.ReadAsync(stream, cancellationToken);
        var hello = helloFrame.DeserializePayload<BridgeHello>();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        const string serverId = "fake-server";
        var serverProof = BridgeAuthenticator.CreateServerProof(
            proofSecret,
            endpoint.Name,
            hello.ClientNonce,
            serverNonce,
            serverId);
        var challengeFrame = BridgeFrame.Create(
            BridgeMessageKind.Challenge,
            nameof(BridgeChallenge),
            new BridgeChallenge(serverNonce, serverId, serverProof),
            correlationId: wrongChallengeCorrelation ? Guid.NewGuid() : helloFrame.MessageId);
        await codec.WriteAsync(stream, challengeFrame, cancellationToken);

        if (!ReferenceEquals(authenticationSecret, proofSecret) || wrongChallengeCorrelation)
        {
            return;
        }

        var authFrame = await codec.ReadAsync(stream, cancellationToken);
        var auth = authFrame.DeserializePayload<BridgeAuthenticate>();
        Assert.Equal(challengeFrame.MessageId, authFrame.CorrelationId);
        Assert.True(
            BridgeAuthenticator.VerifyClientProof(
                authenticationSecret,
                endpoint.Name,
                hello.ClientNonce,
                serverNonce,
                hello.ClientId,
                serverId,
                auth.Proof));
        await codec.WriteAsync(
            stream,
            BridgeFrame.Create(
                BridgeMessageKind.Authenticated,
                nameof(BridgeAuthenticated),
                new BridgeAuthenticated(serverId),
                correlationId: authFrame.MessageId),
            cancellationToken);
    }

    private static NamedPipeServerStream CreateServerStream(PipeEndpoint endpoint)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            endpoint.Name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            options);
    }

    private static NamedPipeClientStream CreateClientStream(PipeEndpoint endpoint) =>
        new(
            ".",
            endpoint.Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            System.Security.Principal.TokenImpersonationLevel.Identification);
}
