using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class BridgeFrameCodecTests
{
    [Fact]
    public async Task Frame_RoundTripsWithExplicitTarget()
    {
        var codec = new BridgeFrameCodec();
        var expected = BridgeFrame.Create(
            BridgeMessageKind.Request,
            "test.request",
            new { operation = "inspect" },
            DocumentTargetTests.CreateTarget());
        await using var stream = new MemoryStream();

        await codec.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await codec.ReadAsync(stream);

        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal("inspect", actual.Payload.GetProperty("operation").GetString());
    }

    [Fact]
    public void ApplicationFrame_WithoutTargetIsRejected()
    {
        var frame = BridgeFrame.Create(
            BridgeMessageKind.Request,
            "test.request",
            new { operation = "inspect" });

        var exception = Assert.Throws<BridgeProtocolException>(() => frame.Validate());
        Assert.Equal("target_required", exception.Code);
    }

    [Fact]
    public async Task OversizedFrame_IsRejectedBeforeWrite()
    {
        var codec = new BridgeFrameCodec(32);
        var frame = BridgeFrame.Create(
            BridgeMessageKind.Request,
            "test.request",
            new { content = new string('x', 128) },
            DocumentTargetTests.CreateTarget());
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            async () => await codec.WriteAsync(stream, frame));
        Assert.Equal("frame_too_large", exception.Code);
    }
}
