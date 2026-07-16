using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class AgentHostOptionsTests
{
    [Fact]
    public void DefaultDataRootIsStableAcrossWindowsPathCaseAndRelativeSegments()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var grasshopper = directory.GetPath("Definition.gh");
        var canonical = new AgentHostOptions
        {
            RhinoPath = rhino,
            GrasshopperPath = grasshopper
        };
        var aliases = new AgentHostOptions
        {
            RhinoPath = Path.Combine(directory.Path.ToUpperInvariant(), ".", "MODEL.3DM"),
            GrasshopperPath = Path.Combine(directory.Path.ToLowerInvariant(), ".", "definition.GH")
        };

        Assert.Equal(canonical.ResolveDataDirectory(), aliases.ResolveDataDirectory());
    }
}
