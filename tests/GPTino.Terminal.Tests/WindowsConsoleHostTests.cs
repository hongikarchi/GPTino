namespace GPTino.Terminal.Tests;

public sealed class WindowsConsoleHostTests
{
    [Fact]
    public void ReleasesInheritedConsoleBeforeAllocatingDedicatedConsole()
    {
        var releaseCalls = 0;
        var allocationCalls = 0;

        var allocated = WindowsConsoleHost.CreateDedicated(
            () =>
            {
                releaseCalls++;
                return true;
            },
            () =>
            {
                allocationCalls++;
                return true;
            });

        Assert.True(allocated);
        Assert.Equal(1, releaseCalls);
        Assert.Equal(1, allocationCalls);
    }

    [Fact]
    public void AllocatesConsoleEvenWhenNoInheritedConsoleExists()
    {
        var releaseCalls = 0;
        var allocationCalls = 0;

        var allocated = WindowsConsoleHost.CreateDedicated(
            () =>
            {
                releaseCalls++;
                return false;
            },
            () =>
            {
                allocationCalls++;
                return true;
            });

        Assert.True(allocated);
        Assert.Equal(1, releaseCalls);
        Assert.Equal(1, allocationCalls);
    }

    [Fact]
    public void ReportsAllocationFailureAfterReleasingInheritedConsole()
    {
        var released = false;

        var allocated = WindowsConsoleHost.CreateDedicated(
            () => released = true,
            () => false);

        Assert.True(released);
        Assert.False(allocated);
    }
}
