using System.Security.Cryptography;
using System.Text.Json;
using GPTino.BridgeContract;

namespace GPTino.WireifyAdapter;

/// <summary>
/// Produces the bridge concurrency fingerprint for the complete observable state of a
/// Python component. The record-shaped payload has stable property ordering and contains no
/// dictionaries, so the bridge JSON representation is deterministic for a given state.
/// </summary>
public static class PythonComponentFingerprint
{
    public static string Compute(PythonComponentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var canonicalJson = JsonSerializer.SerializeToUtf8Bytes(state, BridgeProtocol.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }
}
