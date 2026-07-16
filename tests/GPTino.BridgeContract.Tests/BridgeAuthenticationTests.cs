using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class BridgeAuthenticationTests
{
    [Fact]
    public void ClientProof_VerifiesOnlyForMatchingSecretAndTranscript()
    {
        var secret = BridgeSecret.Generate();
        var otherSecret = BridgeSecret.Generate();
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var proof = BridgeAuthenticator.CreateClientProof(
            secret,
            "gptino-test",
            clientNonce,
            serverNonce,
            "client-1",
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                otherSecret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "gptino-other",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-2",
                proof));
    }

    [Fact]
    public void ServerProof_IsDomainSeparatedAndBoundToServerIdentity()
    {
        var secret = BridgeSecret.Generate();
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var serverProof = BridgeAuthenticator.CreateServerProof(
            secret,
            "gptino-test",
            clientNonce,
            serverNonce,
            "server-1");
        var clientProof = BridgeAuthenticator.CreateClientProof(
            secret,
            "gptino-test",
            clientNonce,
            serverNonce,
            "client-1",
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "server-1",
                serverProof));
        Assert.False(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "server-2",
                serverProof));
        Assert.False(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "gptino-test",
                clientNonce,
                serverNonce,
                "server-1",
                clientProof));
    }

    [Fact]
    public void Secret_RoundTripsThroughBase64()
    {
        var secret = BridgeSecret.Generate();
        var restored = BridgeSecret.FromBase64(secret.ExportBase64());
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var proof = BridgeAuthenticator.CreateServerProof(
            secret,
            "gptino-test",
            clientNonce,
            serverNonce,
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyServerProof(
                restored,
                "gptino-test",
                clientNonce,
                serverNonce,
                "server-1",
                proof));
    }
}
