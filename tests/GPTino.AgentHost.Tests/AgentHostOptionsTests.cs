using System.Security.Cryptography;
using System.Text;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class AgentHostOptionsTests
{
    [Fact]
    public void DefaultDataRootIsStableAcrossWindowsPathCaseAndRelativeSegments()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var canonical = new AgentHostOptions
        {
            RhinoPath = rhino,
            GrasshopperPath = directory.GetPath("Definition.gh")
        };
        var aliases = new AgentHostOptions
        {
            RhinoPath = Path.Combine(directory.Path.ToUpperInvariant(), ".", "MODEL.3DM"),
            GrasshopperPath = directory.GetPath("definition.GH")
        };

        Assert.Equal(canonical.ResolveDataDirectory(), aliases.ResolveDataDirectory());
    }

    [Fact]
    public void DefaultDataRootIsFingerprintedOverRhinoPathOnly()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var firstDefinition = new AgentHostOptions
        {
            RhinoPath = rhino,
            GrasshopperPath = directory.GetPath("First.gh")
        };
        var secondDefinition = new AgentHostOptions
        {
            RhinoPath = rhino,
            GrasshopperPath = directory.GetPath("Second.gh")
        };
        var noDefinition = new AgentHostOptions { RhinoPath = rhino };

        // One Rhino document = one data root: swapping (or dropping) the Grasshopper
        // definition must never fork the project's persistent state.
        Assert.Equal(firstDefinition.ResolveDataDirectory(), secondDefinition.ResolveDataDirectory());
        Assert.Equal(firstDefinition.ResolveDataDirectory(), noDefinition.ResolveDataDirectory());

        var expectedFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rhino).ToUpperInvariant())))[..16];
        Assert.Equal(expectedFingerprint, Path.GetFileName(firstDefinition.ResolveDataDirectory()));
    }
}
