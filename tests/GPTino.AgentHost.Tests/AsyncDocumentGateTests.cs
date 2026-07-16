using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

public sealed class AsyncDocumentGateTests
{
    [Fact]
    public async Task IndependentReadsOverlap()
    {
        var gate = new AsyncDocumentGate();
        using var first = await gate.EnterReadAsync();

        var secondTask = gate.EnterReadAsync().AsTask();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(secondTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitingWriterBlocksLaterReadersUntilWriteEpochEnds()
    {
        var gate = new AsyncDocumentGate();
        var firstRead = await gate.EnterReadAsync();
        var writerTask = gate.EnterWriteAsync().AsTask();
        await Task.Delay(25);
        var laterReadTask = gate.EnterReadAsync().AsTask();

        Assert.False(writerTask.IsCompleted);
        Assert.False(laterReadTask.IsCompleted);

        firstRead.Dispose();
        var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(laterReadTask.IsCompleted);

        writer.Dispose();
        using var laterRead = await laterReadTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
