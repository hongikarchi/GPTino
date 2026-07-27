using System.Text.Json;
using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

public sealed class OutputCountPredicateTests
{
    private static readonly Guid ComponentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static IReadOnlyList<LiveDocumentBackend.JobComponentOutputs> Outputs(Guid componentId, string inspectionJson)
    {
        using var document = JsonDocument.Parse(inspectionJson);
        return [new LiveDocumentBackend.JobComponentOutputs(componentId, document.RootElement.Clone())];
    }

    [Theory]
    [InlineData("alt_1:1:100", true, 1, 100)]
    [InlineData("x:0:*", true, 0, int.MaxValue)]
    [InlineData("panels:5:5", true, 5, 5)]
    [InlineData("x:5:2", false, 0, 0)]   // max < min
    [InlineData("x:-1:5", false, 0, 0)]  // negative min
    [InlineData("x:a:b", false, 0, 0)]   // non-numeric
    [InlineData("noparts", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void ParsesRangeSpecifications(string spec, bool ok, int min, int max)
    {
        var parsed = LiveDocumentBackend.TryParseOutputCountRange(spec, out var range);
        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Equal(min, range.Min);
            Assert.Equal(max, range.Max);
        }
    }

    [Fact]
    public void PassesWhenNamedOutputCountIsWithinRange()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"alt_1","dataCount":5},{"name":"out","dataCount":0}]}""");
        Assert.True(LiveDocumentBackend.EvaluateOutputCountInRange(outputs, ComponentId, new("alt_1", 1, 10)));
    }

    [Fact]
    public void FailsWhenCountIsOutsideRange()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"alt_1","dataCount":5}]}""");
        Assert.False(LiveDocumentBackend.EvaluateOutputCountInRange(outputs, ComponentId, new("alt_1", 10, 20)));
    }

    [Fact]
    public void FailsClosedWhenOutputNameOrComponentIsMissing()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"alt_1","dataCount":5}]}""");
        // Named output not present -> fail closed.
        Assert.False(LiveDocumentBackend.EvaluateOutputCountInRange(outputs, ComponentId, new("missing", 0, 100)));
        // Component not inspected -> fail closed.
        Assert.False(LiveDocumentBackend.EvaluateOutputCountInRange(outputs, Guid.NewGuid(), new("alt_1", 0, 100)));
        // No outputs at all -> fail closed.
        Assert.False(LiveDocumentBackend.EvaluateOutputCountInRange(null, ComponentId, new("alt_1", 0, 100)));
    }

    [Theory]
    [InlineData("srf:1.5:10", true, 1.5, 10)]
    [InlineData("srf:0:*", true, 0, double.PositiveInfinity)]
    [InlineData("srf:5:2", false, 0, 0)]
    public void ParsesNumericRangeSpecifications(string spec, bool ok, double min, double max)
    {
        var parsed = LiveDocumentBackend.TryParseNumericOutputRange(spec, out _, out var lo, out var hi);
        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Equal(min, lo);
            Assert.Equal(max, hi);
        }
    }

    [Fact]
    public void EvaluatesAreaAndBranchCountNumericFields()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"srf","area":12.5,"branchCount":3,"closed":true}]}""");
        Assert.True(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "srf", "area", 10, 20));
        Assert.False(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "srf", "area", 0, 5));
        Assert.True(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "srf", "branchCount", 1, 5));
        // Missing numeric field -> fail closed.
        Assert.False(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "srf", "volume", 0, 100));
    }

    [Fact]
    public void EvaluatesGeometryClosed()
    {
        var closed = Outputs(ComponentId, """{"outputs":[{"name":"panel","closed":true}]}""");
        var open = Outputs(ComponentId, """{"outputs":[{"name":"panel","closed":false}]}""");
        var absent = Outputs(ComponentId, """{"outputs":[{"name":"panel","dataCount":1}]}""");
        Assert.True(LiveDocumentBackend.EvaluateGeometryClosed(closed, ComponentId, "panel"));
        Assert.False(LiveDocumentBackend.EvaluateGeometryClosed(open, ComponentId, "panel"));
        Assert.False(LiveDocumentBackend.EvaluateGeometryClosed(absent, ComponentId, "panel")); // no closed field -> fail closed
    }

    [Fact]
    public void EvaluatesVolumeViaNumericField()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"solid","volume":8.0}]}""");
        Assert.True(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "solid", "volume", 1, 10));
        Assert.False(LiveDocumentBackend.EvaluateNumericOutputInRange(outputs, ComponentId, "solid", "volume", 10, 20));
    }

    [Theory]
    [InlineData("srf:diagonal:0:100", true, "diagonal", 0, 100)]
    [InlineData("srf:x:1:5", true, "x", 1, 5)]
    [InlineData("srf:w:0:1", false, "", 0, 0)]   // bad axis
    [InlineData("srf:x:5:2", false, "", 0, 0)]   // max<min
    [InlineData("srf:x:0", false, "", 0, 0)]     // too few parts
    public void ParsesBoundingBoxRange(string spec, bool ok, string axis, double min, double max)
    {
        var parsed = LiveDocumentBackend.TryParseBoundingBoxRange(spec, out _, out var a, out var lo, out var hi);
        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Equal(axis, a);
            Assert.Equal(min, lo);
            Assert.Equal(max, hi);
        }
    }

    [Fact]
    public void EvaluatesBoundingBoxAxisAndDiagonal()
    {
        var outputs = Outputs(ComponentId, """{"outputs":[{"name":"srf","geometryBounds":{"minimum":{"x":0,"y":0,"z":0},"maximum":{"x":3,"y":4,"z":0},"size":{"x":3,"y":4,"z":0}}}]}""");
        Assert.True(LiveDocumentBackend.EvaluateBoundingBoxInRange(outputs, ComponentId, "srf", "x", 1, 5));
        Assert.False(LiveDocumentBackend.EvaluateBoundingBoxInRange(outputs, ComponentId, "srf", "x", 4, 10));
        // diagonal of (3,4,0) = 5
        Assert.True(LiveDocumentBackend.EvaluateBoundingBoxInRange(outputs, ComponentId, "srf", "diagonal", 4, 6));
        Assert.False(LiveDocumentBackend.EvaluateBoundingBoxInRange(outputs, ComponentId, "srf", "diagonal", 6, 10));
        // no bounds -> fail closed
        var noBounds = Outputs(ComponentId, """{"outputs":[{"name":"srf","dataCount":1}]}""");
        Assert.False(LiveDocumentBackend.EvaluateBoundingBoxInRange(noBounds, ComponentId, "srf", "x", 0, 100));
    }
}
