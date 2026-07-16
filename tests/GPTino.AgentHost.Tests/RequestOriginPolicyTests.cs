using GPTino.AgentHost.Security;

namespace GPTino.AgentHost.Tests;

public sealed class RequestOriginPolicyTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("not a uri")]
    [InlineData("http://127.0.0.1:43112/path")]
    [InlineData("https://127.0.0.1:43112")]
    [InlineData("http://127.0.0.1:43113")]
    public void OpaqueMalformedOrCrossOriginValuesAreRejected(string origin)
    {
        Assert.False(RequestOriginPolicy.AllowsPresentedOrigin(
            [origin],
            "http",
            "127.0.0.1:43112"));
    }

    [Fact]
    public void MultipleOriginValuesAreRejected()
    {
        Assert.False(RequestOriginPolicy.AllowsPresentedOrigin(
            ["http://127.0.0.1:43112", "http://127.0.0.1:43112"],
            "http",
            "127.0.0.1:43112"));
    }

    [Fact]
    public void ExactLoopbackOriginIsAccepted()
    {
        Assert.True(RequestOriginPolicy.AllowsPresentedOrigin(
            ["http://127.0.0.1:43112"],
            "http",
            "127.0.0.1:43112"));
    }
}
