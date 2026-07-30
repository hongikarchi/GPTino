using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// End-to-end tests for the server-resolved arrange_layout write path: it computes a tidy layout from the
/// live snapshot and submits an ordinary canvas.move through the full pipeline (no execute-flow surgery).
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class ArrangeLayoutTests
{
    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);

    [Fact]
    public async Task TidiesAWiredClusterAndCommitsALeftToRightMove()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true; // adds the second component
        harness.WireFirstTwoObjects = true;      // wires first -> second, so a real dataflow cluster exists
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy"));

        var result = ToElement(await harness.Backend.ArrangeLayoutAsync(
            session,
            Args(new
            {
                seedComponentIds = new[]
                {
                    harness.CanvasObjectId.ToString("D"),
                    harness.SecondCanvasObjectId.ToString("D"),
                },
                wait = false,
            }),
            CancellationToken.None));

        // A real move was submitted (not the already-tidy no-op).
        Assert.True(result.TryGetProperty("jobId", out var jobIdElement),
            "arrange_layout should submit a move job; got: " + result);
        var state = await harness.WaitForJobStateAsync(jobIdElement.GetGuid());
        var jobView = await harness.ReadJobViewAsync(jobIdElement.GetGuid());
        Assert.True(state == "committed", jobView.GetProperty("message").GetString());

        // The bridge received exactly one canvas.move; the downstream (second) component was repositioned to
        // the right of the upstream one (left->right dataflow).
        var move = Assert.Single(responder.Requests, r => string.Equals(r.Operation, "canvas.move", StringComparison.Ordinal));
        var pivots = move.Arguments.GetProperty("pivots");
        Assert.True(pivots.TryGetProperty(harness.SecondCanvasObjectId.ToString("D"), out var secondPivot),
            "the downstream component should have moved");
        // Upstream stayed at its column-0 x (10); downstream lands to its right.
        Assert.True(secondPivot.GetProperty("x").GetDouble() > 10.0);
    }

    [Fact]
    public async Task IsANoOpWhenThereIsNothingToTidy()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy"));

        // A single isolated seed forms a one-node cluster already at its own position — no move.
        var result = ToElement(await harness.Backend.ArrangeLayoutAsync(
            session,
            Args(new { seedComponentIds = new[] { harness.CanvasObjectId.ToString("D") } }),
            CancellationToken.None));

        Assert.Equal("already-tidy", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("moved").GetInt32());
        Assert.DoesNotContain(responder.Requests, r => string.Equals(r.Operation, "canvas.move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAnEmptySeedSet()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.ArrangeLayoutAsync(
                session,
                Args(new { seedComponentIds = Array.Empty<string>() }),
                CancellationToken.None));
    }
}
