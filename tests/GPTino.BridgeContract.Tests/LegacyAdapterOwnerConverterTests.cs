using System.Text.Json;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.BridgeContract.Tests;

// Pins the durable-job upgrade path: ChangeSets persisted before the owner rename carry
// "wireify"/"cordyceps" and must keep deserializing through the real BridgeProtocol.JsonOptions
// (which also pins LegacyAdapterOwnerConverter's registration ahead of the general enum converter).
public sealed class LegacyAdapterOwnerConverterTests
{
    [Theory]
    [InlineData("\"wireify\"", AdapterOwner.Script)]
    [InlineData("\"Wireify\"", AdapterOwner.Script)]
    [InlineData("\"cordyceps\"", AdapterOwner.Canvas)]
    [InlineData("\"Cordyceps\"", AdapterOwner.Canvas)]
    [InlineData("\"script\"", AdapterOwner.Script)]
    [InlineData("\"canvas\"", AdapterOwner.Canvas)]
    [InlineData("\"rhinoBridge\"", AdapterOwner.RhinoBridge)]
    public void Read_AcceptsCurrentAndLegacySpellings(string json, AdapterOwner expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<AdapterOwner>(json, BridgeProtocol.JsonOptions));
    }

    [Fact]
    public void Read_RejectsUnknownOwner()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AdapterOwner>("\"karamba\"", BridgeProtocol.JsonOptions));
    }

    [Theory]
    [InlineData(AdapterOwner.Script, "\"script\"")]
    [InlineData(AdapterOwner.Canvas, "\"canvas\"")]
    [InlineData(AdapterOwner.RhinoBridge, "\"rhinoBridge\"")]
    public void Write_EmitsCurrentCamelCaseNames(AdapterOwner owner, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(owner, BridgeProtocol.JsonOptions));
    }

    [Fact]
    public void TypedOperation_WithLegacyOwner_Deserializes()
    {
        const string json =
            """{"operationId":"op-legacy","kind":"createComponent","owner":"wireify","reads":[],"writes":[],"reversible":false}""";

        var operation = JsonSerializer.Deserialize<TypedOperation>(json, BridgeProtocol.JsonOptions);

        Assert.NotNull(operation);
        Assert.Equal(AdapterOwner.Script, operation.Owner);
    }
}
