using GPTino.AgentHost.Runtime;
using GPTino.CordycepsAdapter;

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
}
