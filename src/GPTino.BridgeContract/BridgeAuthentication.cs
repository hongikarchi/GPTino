using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GPTino.BridgeContract;

public sealed class BridgeSecret
{
    public const int ByteLength = 32;

    private readonly byte[] _bytes;

    private BridgeSecret(byte[] bytes)
    {
        _bytes = bytes;
    }

    public static BridgeSecret Generate() => new(RandomNumberGenerator.GetBytes(ByteLength));

    public static BridgeSecret FromBase64(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != ByteLength)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException($"Bridge secrets must be {ByteLength} bytes.", nameof(value));
        }

        return new BridgeSecret(bytes);
    }

    public string ExportBase64() => Convert.ToBase64String(_bytes);

    internal ReadOnlySpan<byte> Bytes => _bytes;
}

public static class BridgeAuthenticator
{
    private const string ClientProofDomain = "gptino-bridge/client-proof";
    private const string ServerProofDomain = "gptino-bridge/server-proof";

    public const int NonceByteLength = 32;

    public static string CreateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceByteLength));

    public static string CreateClientProof(
        BridgeSecret secret,
        string endpointName,
        string clientNonce,
        string serverNonce,
        string clientId,
        string serverId)
    {
        ValidateTranscript(endpointName, clientNonce, serverNonce, serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return CreateProof(
            secret,
            ClientProofDomain,
            endpointName,
            clientNonce,
            serverNonce,
            clientId,
            serverId);
    }

    public static bool VerifyClientProof(
        BridgeSecret secret,
        string endpointName,
        string clientNonce,
        string serverNonce,
        string clientId,
        string serverId,
        string proof)
    {
        var expected = CreateClientProof(
            secret,
            endpointName,
            clientNonce,
            serverNonce,
            clientId,
            serverId);
        return VerifyProof(expected, proof);
    }

    public static string CreateServerProof(
        BridgeSecret secret,
        string endpointName,
        string clientNonce,
        string serverNonce,
        string serverId)
    {
        ValidateTranscript(endpointName, clientNonce, serverNonce, serverId);
        return CreateProof(
            secret,
            ServerProofDomain,
            endpointName,
            clientNonce,
            serverNonce,
            serverId);
    }

    public static bool VerifyServerProof(
        BridgeSecret secret,
        string endpointName,
        string clientNonce,
        string serverNonce,
        string serverId,
        string proof)
    {
        var expected = CreateServerProof(
            secret,
            endpointName,
            clientNonce,
            serverNonce,
            serverId);
        return VerifyProof(expected, proof);
    }

    private static string CreateProof(
        BridgeSecret secret,
        string domain,
        params string[] transcriptFields)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var message = EncodeTranscript(domain, transcriptFields);
        var key = secret.Bytes.ToArray();
        try
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static bool VerifyProof(string expectedProof, string suppliedProof)
    {
        if (string.IsNullOrWhiteSpace(suppliedProof))
        {
            return false;
        }

        byte[] supplied;
        byte[] expected;

        try
        {
            supplied = Convert.FromBase64String(suppliedProof);
            expected = Convert.FromBase64String(expectedProof);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return supplied.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static byte[] EncodeTranscript(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        AppendField(stream, domain);
        AppendField(stream, BridgeProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var field in fields)
        {
            AppendField(stream, field);
        }

        return stream.ToArray();
    }

    private static void AppendField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static void ValidateTranscript(
        string endpointName,
        string clientNonce,
        string serverNonce,
        string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ValidateNonce(clientNonce, nameof(clientNonce));
        ValidateNonce(serverNonce, nameof(serverNonce));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
    }

    private static void ValidateNonce(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Nonce must be Base64.", parameterName, exception);
        }

        try
        {
            if (bytes.Length != NonceByteLength)
            {
                throw new ArgumentException(
                    $"Nonce must be {NonceByteLength} bytes.",
                    parameterName);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed record BridgeHello(string ClientId, string ClientNonce);

public sealed record BridgeChallenge(string ServerNonce, string ServerId, string ServerProof);

public sealed record BridgeAuthenticate(string Proof);

public sealed record BridgeAuthenticated(string ServerId);
