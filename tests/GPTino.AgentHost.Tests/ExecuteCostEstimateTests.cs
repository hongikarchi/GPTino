using GPTino.AgentHost.Runtime;
using GPTino.CanvasSceneAdapter;

namespace GPTino.AgentHost.Tests;

public sealed class ExecuteCostEstimateTests
{
    private static readonly Guid ScriptId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ScriptTypeId = "719467e6-7cf5-4848-99b0-c5dd57e5442c";

    private static string SliderValueJson(double value, int decimalPlaces = 0) =>
        $$"""{"kind":"numberSlider","value":{{value}},"minimum":0,"maximum":100000,"decimalPlaces":{{decimalPlaces}}}""";

    private static CanvasObjectState Slider(Guid id, string name, double value, int decimalPlaces = 0) =>
        new(id, Guid.NewGuid(), name, new CanvasPoint(0, 0), new CanvasSize(10, 10), "fp")
        {
            ValueJson = SliderValueJson(value, decimalPlaces),
        };

    private static CanvasParameterState Input(string name, params Guid[] sources) =>
        new(
            ScriptId,
            Guid.NewGuid(),
            name,
            name,
            CanvasParameterDirection.Input,
            "System.Object",
            "object",
            CanvasParameterAccess.Item,
            Optional: false,
            sources.Select(id => new CanvasParameterEndpoint(id, Guid.NewGuid())).ToArray());

    private static CanvasSnapshot Snapshot(IReadOnlyList<CanvasParameterState> inputs, params CanvasObjectState[] sliders)
    {
        var script = new CanvasObjectState(
            ScriptId,
            Guid.Parse(ScriptTypeId),
            "Script",
            new CanvasPoint(100, 100),
            new CanvasSize(90, 40),
            "fp")
        {
            Inputs = inputs,
        };
        return new CanvasSnapshot(
            Guid.NewGuid(),
            "doc-fp",
            [script, .. sliders],
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    [Fact]
    public void MultipliesWholeNumberResolutionSlidersWiredIntoCountNamedSockets()
    {
        var u = Guid.NewGuid();
        var v = Guid.NewGuid();
        var snapshot = Snapshot(
            [Input("USpans", u), Input("VSpans", v)],
            Slider(u, "U", 2000),
            Slider(v, "V", 2000));

        var (estimate, knobs) = LiveDocumentBackend.EstimateExecuteElementCost(snapshot, ScriptId);

        Assert.Equal(4_000_000, estimate);
        Assert.Equal(2, knobs.Count);
    }

    [Fact]
    public void IgnoresFractionalSlidersAndValuesBelowTwo()
    {
        var sag = Guid.NewGuid();
        var one = Guid.NewGuid();
        var snapshot = Snapshot(
            // 'sag' is a dimension (fractional) and not a count keyword; 'count' slider is 1 (no cost).
            [Input("sagDivision", sag), Input("count", one)],
            Slider(sag, "sag", 1.5, decimalPlaces: 2),
            Slider(one, "n", 1));

        var (estimate, knobs) = LiveDocumentBackend.EstimateExecuteElementCost(snapshot, ScriptId);

        Assert.Equal(0, estimate);
        Assert.Empty(knobs);
    }

    [Fact]
    public void ReturnsZeroWhenNoCountNamedSocketDrivesTheComponent()
    {
        var radius = Guid.NewGuid();
        var snapshot = Snapshot(
            [Input("radius", radius), Input("height", Guid.NewGuid())],
            Slider(radius, "radius", 5000));

        var (estimate, _) = LiveDocumentBackend.EstimateExecuteElementCost(snapshot, ScriptId);

        Assert.Equal(0, estimate);
    }

    [Fact]
    public void OrdinaryResolutionsStayWellBelowTheBlockThreshold()
    {
        var u = Guid.NewGuid();
        var v = Guid.NewGuid();
        var snapshot = Snapshot(
            [Input("uCount", u), Input("vCount", v)],
            Slider(u, "u", 80),
            Slider(v, "v", 80));

        var (estimate, _) = LiveDocumentBackend.EstimateExecuteElementCost(snapshot, ScriptId);

        Assert.Equal(6_400, estimate);
    }

    // ---- Layer 1: low-resolution-first gate (ShouldBlockExecuteCost) ----

    [Theory]
    // Established component (has a committed solve): only the 2,000,000 hard ceiling applies.
    [InlineData(6_400, true, false)]
    [InlineData(1_999_999, true, false)]
    [InlineData(2_000_000, true, false)]
    [InlineData(2_000_001, true, true)]
    // First solve (never committed): the 10,000-element low-resolution ceiling applies instead.
    [InlineData(6_400, false, false)]
    [InlineData(10_000, false, false)]
    [InlineData(10_001, false, true)]
    [InlineData(40_000, false, true)]
    public void FirstSolveGetsTheLowResolutionCeiling_EstablishedGetsTheHardCeiling(
        long estimate,
        bool established,
        bool expectedBlocked)
    {
        Assert.Equal(expectedBlocked, LiveDocumentBackend.ShouldBlockExecuteCost(estimate, established, out _));
    }

    [Fact]
    public void FirstSolveCeilingIsFarStricterThanTheEstablishedCeiling()
    {
        LiveDocumentBackend.ShouldBlockExecuteCost(0, established: true, out var establishedCeiling);
        LiveDocumentBackend.ShouldBlockExecuteCost(0, established: false, out var firstSolveCeiling);

        Assert.Equal(2_000_000, establishedCeiling);
        Assert.Equal(10_000, firstSolveCeiling);
        // A 100x100 first pass squeaks under; a 200x200 first pass must go lower first.
        Assert.False(LiveDocumentBackend.ShouldBlockExecuteCost(100 * 100, established: false, out _));
        Assert.True(LiveDocumentBackend.ShouldBlockExecuteCost(200 * 200, established: false, out _));
    }
}
