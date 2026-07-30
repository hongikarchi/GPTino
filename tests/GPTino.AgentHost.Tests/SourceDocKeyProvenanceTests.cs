using System.Text.Json;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The bake-provenance contract: models can never author GPTino.SourceDocKey (attribution would
/// be spoofable), and the executor stamps every dispatched rhino.upsert with the job's target
/// docKey without touching the frozen payload.
/// </summary>
public sealed class SourceDocKeyProvenanceTests
{
    private static UpsertRhinoObjectRequest ValidUpsert(string? sourceDocKey = null) => new(
        "op-1",
        Guid.NewGuid(),
        "entity-1",
        "Curve",
        "{}",
        "{}",
        ExpectedFingerprint: null,
        SourceDocKey: sourceDocKey);

    [Fact]
    public void ValidateUpsertRejectsModelAuthoredSourceDocKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateUpsertArguments(ValidUpsert("abcdef0123456789"), "op-1"));
        Assert.Contains("sourceDocKey", exception.Message, StringComparison.Ordinal);

        // The same payload without the field passes this gate (provenance is server business).
        LiveDocumentBackend.ValidateUpsertArguments(ValidUpsert(), "op-1");
    }

    [Fact]
    public void InjectRewritesOnlyRhinoUpsertArgumentsAndKeepsFrozenPayload()
    {
        var upsertArguments = JsonSerializer.SerializeToElement(
            new { operationId = "op-1", objectId = Guid.NewGuid() },
            BridgeProtocol.JsonOptions);
        var wireArguments = JsonSerializer.SerializeToElement(
            new { operationId = "op-2" },
            BridgeProtocol.JsonOptions);
        var frozen = new byte[] { 1, 2, 3 };
        var operations = new[]
        {
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.CordycepsRhino, "rhino.upsert", upsertArguments, frozen, "sha-upsert"),
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.CordycepsCanvas, "canvas.setWire", wireArguments, frozen, "sha-wire"),
        };

        var injected = LiveDocumentBackend.InjectRhinoUpsertSourceDocKey(operations, "abcdef0123456789");

        var stamped = injected[0];
        Assert.Equal(
            "abcdef0123456789",
            stamped.Arguments.GetProperty("sourceDocKey").GetString());
        // Existing fields survive the rewrite and the frozen idempotency payload is untouched.
        Assert.Equal("op-1", stamped.Arguments.GetProperty("operationId").GetString());
        Assert.Same(frozen, stamped.FrozenPayload);
        Assert.Equal("sha-upsert", stamped.PayloadSha256);
        // Non-upsert operations pass through by reference, arguments unmodified.
        Assert.Same(operations[1], injected[1]);
        Assert.False(injected[1].Arguments.TryGetProperty("sourceDocKey", out _));

        // The stamped shape still deserializes under the strict bridge options (Disallow unmapped),
        // which is exactly what rhino.validateUpsert and rhino.upsert do on the GH side.
        var roundTripped = stamped.Arguments.Deserialize<UpsertRhinoObjectRequest>(BridgeProtocol.JsonOptions);
        Assert.Equal("abcdef0123456789", roundTripped!.SourceDocKey);
    }
}
