namespace GPTino.Terminal.Tests;

public sealed class TerminalCommandTests
{
    [Theory]
    [InlineData("/pause", "Pause")]
    [InlineData(" /RESUME ", "Resume")]
    [InlineData("/status", "Status")]
    [InlineData("/help", "Help")]
    [InlineData("/quit", "Quit")]
    [InlineData("/exit", "Quit")]
    [InlineData("/bogus", "Unknown")]
    public void ParseRecognizesCommands(string value, string expected)
    {
        Assert.Equal(expected, TerminalCommand.Parse(value).Kind.ToString());
    }

    [Fact]
    public void ParsePreservesTrimmedMessage()
    {
        var command = TerminalCommand.Parse("  Move the south wall  ");

        Assert.Equal(TerminalCommandKind.Message, command.Kind);
        Assert.Equal("Move the south wall", command.Content);
    }
}
