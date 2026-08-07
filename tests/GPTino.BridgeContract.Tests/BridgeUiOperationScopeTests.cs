using GPTino.BridgeContract;
using Xunit;

namespace GPTino.BridgeContract.Tests;

/// <summary>
/// The re-entrancy gate for the document-open crash: bookkeeping checks
/// IsActiveOnCurrentThread to defer while a bridge operation executes, and drains on Exited.
/// These tests pin the contract that makes that safe — same-thread visibility only, outermost
/// exit fires exactly once, and a throwing subscriber never breaks the operation path.
/// </summary>
public sealed class BridgeUiOperationScopeTests
{
    [Fact]
    public void ActiveInsideTheScopeOnTheSameThreadOnly()
    {
        Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);
        using (BridgeUiOperationScope.Enter())
        {
            Assert.True(BridgeUiOperationScope.IsActiveOnCurrentThread);

            var seenOnOtherThread = true;
            var observer = new Thread(() => seenOnOtherThread = BridgeUiOperationScope.IsActiveOnCurrentThread);
            observer.Start();
            observer.Join();
            Assert.False(seenOnOtherThread);
        }
        Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);
    }

    [Fact]
    public void ExitedFiresOnceOnTheOutermostExit()
    {
        var exits = 0;
        Action handler = () => exits++;
        BridgeUiOperationScope.Exited += handler;
        try
        {
            var outer = BridgeUiOperationScope.Enter();
            var inner = BridgeUiOperationScope.Enter();
            inner.Dispose();
            Assert.Equal(0, exits);
            Assert.True(BridgeUiOperationScope.IsActiveOnCurrentThread);
            outer.Dispose();
            Assert.Equal(1, exits);
            Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);

            // Double-dispose must not fire again or unbalance the depth.
            outer.Dispose();
            inner.Dispose();
            Assert.Equal(1, exits);
            Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);
        }
        finally
        {
            BridgeUiOperationScope.Exited -= handler;
        }
    }

    [Fact]
    public void ThrowingExitedSubscriberIsSwallowedAndStateStaysClean()
    {
        Action handler = () => throw new InvalidOperationException("subscriber failure");
        BridgeUiOperationScope.Exited += handler;
        try
        {
            var scope = BridgeUiOperationScope.Enter();
            scope.Dispose(); // must not throw
            Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);

            // The gate still works for the next operation.
            using (BridgeUiOperationScope.Enter())
            {
                Assert.True(BridgeUiOperationScope.IsActiveOnCurrentThread);
            }
            Assert.False(BridgeUiOperationScope.IsActiveOnCurrentThread);
        }
        finally
        {
            BridgeUiOperationScope.Exited -= handler;
        }
    }
}
