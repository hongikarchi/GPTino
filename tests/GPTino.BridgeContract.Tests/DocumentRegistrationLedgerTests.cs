using GPTino.BridgeContract;

namespace GPTino.BridgeContract.Tests;

public sealed class DocumentRegistrationLedgerTests
{
    [Fact]
    public void UnconfirmedTargetsAreOutstanding()
    {
        var ledger = new DocumentRegistrationLedger();
        var pair = DocumentTargetTests.CreateTarget();
        var rhinoOnly = DocumentTargetTests.CreateRhinoOnlyTarget();

        Assert.Equal(
            new[] { pair, rhinoOnly },
            ledger.Outstanding(new[] { pair, rhinoOnly }));
    }

    [Fact]
    public void ConfirmedTargetsDropOutOfTheOutstandingSet()
    {
        var ledger = new DocumentRegistrationLedger();
        var pair = DocumentTargetTests.CreateTarget();
        var rhinoOnly = DocumentTargetTests.CreateRhinoOnlyTarget();
        ledger.Confirm(rhinoOnly.StableTargetKey(), rhinoOnly.Generation);

        // The exact failure this ledger exists for: one target confirmed, a sibling that arrived
        // later still needing a frame. Before it, the sibling was simply lost.
        Assert.Equal(new[] { pair }, ledger.Outstanding(new[] { pair, rhinoOnly }));
        Assert.True(ledger.IsConfirmed(rhinoOnly));
        Assert.False(ledger.IsConfirmed(pair));
    }

    [Fact]
    public void ANewerGenerationNeedsRegisteringAgain()
    {
        // A Save As re-registers the SAME live document at a higher generation. Treating the old
        // acknowledgement as covering it would leave the AgentHost on stale paths.
        var ledger = new DocumentRegistrationLedger();
        var target = DocumentTargetTests.CreateTarget();
        ledger.Confirm(target.StableTargetKey(), target.Generation);
        var renamed = target.NextGeneration();

        Assert.False(ledger.IsConfirmed(renamed));
        Assert.Equal(new[] { renamed }, ledger.Outstanding(new[] { renamed }));

        ledger.Confirm(renamed.StableTargetKey(), renamed.Generation);
        Assert.True(ledger.IsConfirmed(renamed));
        // The older generation stays covered — acknowledgements only ever move forward.
        Assert.True(ledger.IsConfirmed(target));
    }

    [Fact]
    public void ALateAcknowledgementCannotRewindAConfirmation()
    {
        var ledger = new DocumentRegistrationLedger();
        var target = DocumentTargetTests.CreateTarget();
        var renamed = target.NextGeneration();
        ledger.Confirm(renamed.StableTargetKey(), renamed.Generation);
        // An acknowledgement for the previous generation arriving out of order.
        ledger.Confirm(target.StableTargetKey(), target.Generation);

        Assert.True(ledger.IsConfirmed(renamed));
    }

    [Fact]
    public void DisconnectingForgetsEverything()
    {
        // The next AgentHost is a different process with no memory of these targets, so anything
        // still marked confirmed would never be sent again.
        var ledger = new DocumentRegistrationLedger();
        var pair = DocumentTargetTests.CreateTarget();
        var rhinoOnly = DocumentTargetTests.CreateRhinoOnlyTarget();
        ledger.Confirm(pair.StableTargetKey(), pair.Generation);
        ledger.Confirm(rhinoOnly.StableTargetKey(), rhinoOnly.Generation);

        ledger.Clear();

        Assert.Equal(new[] { pair, rhinoOnly }, ledger.Outstanding(new[] { pair, rhinoOnly }));
    }

    [Fact]
    public void ClosingOneTargetLeavesItsSiblingsConfirmed()
    {
        var ledger = new DocumentRegistrationLedger();
        var pair = DocumentTargetTests.CreateTarget();
        var rhinoOnly = DocumentTargetTests.CreateRhinoOnlyTarget();
        ledger.Confirm(pair.StableTargetKey(), pair.Generation);
        ledger.Confirm(rhinoOnly.StableTargetKey(), rhinoOnly.Generation);

        ledger.Forget(pair.StableTargetKey());

        Assert.Equal(new[] { pair }, ledger.Outstanding(new[] { pair, rhinoOnly }));
    }

    [Fact]
    public void ABlankTargetKeyConfirmsNothing()
    {
        var ledger = new DocumentRegistrationLedger();
        ledger.Confirm(string.Empty, 1);
        ledger.Confirm("   ", 1);

        Assert.Equal(
            new[] { DocumentTargetTests.CreateTarget() },
            ledger.Outstanding(new[] { DocumentTargetTests.CreateTarget() }));
    }
}
