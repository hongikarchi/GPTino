using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class BridgeOperationMessageTests
{
    [Fact]
    public void WriteOperation_RequiresBrokerLease()
    {
        var request = BridgeOperationRequest.Create(
            "op-1",
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.move",
            BridgeOperationAccess.Write,
            4,
            new { objectId = Guid.NewGuid(), x = 20, y = 30 });

        var exception = Assert.Throws<BridgeProtocolException>(() => request.Validate());
        Assert.Equal("writer_lease_required", exception.Code);
    }

    [Fact]
    public void ReadOperation_DoesNotRequireBrokerLease()
    {
        var request = BridgeOperationRequest.Create(
            "op-2",
            BridgeAdapterOwner.Wireify,
            "python.inspect",
            BridgeOperationAccess.Read,
            4,
            new { componentId = Guid.NewGuid() });

        request.Validate();
    }

    [Fact]
    public void OperationArguments_RoundTripAsTypedPayload()
    {
        var componentId = Guid.NewGuid();
        var request = BridgeOperationRequest.Create(
            "op-3",
            BridgeAdapterOwner.Wireify,
            "python.setSource",
            BridgeOperationAccess.Write,
            8,
            new TestArguments(componentId, "print('ok')"),
            expectedFingerprint: "before",
            writerLeaseToken: "lease-from-broker");

        request.Validate();
        var arguments = request.DeserializeArguments<TestArguments>();

        Assert.Equal(componentId, arguments.ComponentId);
        Assert.Equal("print('ok')", arguments.Source);
    }

    private sealed record TestArguments(Guid ComponentId, string Source);
}
