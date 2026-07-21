namespace GPTino.Terminal.Tests;

public sealed class CliArgumentsTests
{
    [Fact]
    public void ParseAcceptsAttachArgumentsAndNormalizesEndpoint()
    {
        var sessionId = Guid.NewGuid();

        var result = CliArguments.Parse(
        [
            "attach",
            "--new-console",
            "--endpoint", "http://127.0.0.1:51872",
            "--session", sessionId.ToString("D"),
            "--token", "local-secret",
            "--title", "Facade study",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Uri("http://127.0.0.1:51872/"), result.Arguments!.Endpoint);
        Assert.Equal(sessionId, result.Arguments.SessionId);
        Assert.Equal("local-secret", result.Arguments.Token);
        Assert.Equal("Facade study", result.Arguments.Title);
        Assert.True(result.Arguments.NewConsole);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/temp")]
    [InlineData("http://user:password@127.0.0.1:5000")]
    [InlineData("http://127.0.0.1:5000/?token=secret")]
    public void ParseRejectsUnsafeEndpoint(string endpoint)
    {
        var result = CliArguments.Parse(
        [
            "attach",
            "--endpoint", endpoint,
            "--session", Guid.NewGuid().ToString("D"),
            "--token", "local-secret",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("loopback URL", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsMissingTokenWithoutEchoingArguments()
    {
        var result = CliArguments.Parse(
        [
            "attach",
            "--endpoint", "http://localhost:5000",
            "--session", Guid.NewGuid().ToString("D"),
            "--token", " ",
        ], environmentToken: null);

        Assert.False(result.IsSuccess);
        Assert.Equal("API token is required via --token or GPTINO_API_TOKEN.", result.Error);
    }

    [Fact]
    public void ParseUsesEnvironmentTokenWhenCommandLineTokenIsOmitted()
    {
        var sessionId = Guid.NewGuid();

        var result = CliArguments.Parse(
        [
            "attach",
            "--endpoint", "http://localhost:5000",
            "--session", sessionId.ToString("D"),
        ], environmentToken: "child-only-secret");

        Assert.True(result.IsSuccess);
        Assert.Equal("child-only-secret", result.Arguments!.Token);
        Assert.False(result.Arguments.NewConsole);
    }

    [Fact]
    public void ParsePrefersExplicitTokenOverEnvironmentFallback()
    {
        var result = CliArguments.Parse(
        [
            "attach",
            "--endpoint", "http://localhost:5000",
            "--session", Guid.NewGuid().ToString("D"),
            "--token", "manual-secret",
        ], environmentToken: "environment-secret");

        Assert.True(result.IsSuccess);
        Assert.Equal("manual-secret", result.Arguments!.Token);
    }
}
