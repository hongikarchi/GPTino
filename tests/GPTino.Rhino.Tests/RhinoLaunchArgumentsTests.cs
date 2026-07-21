using System.Diagnostics;
using GPTino.LiveE2E;
using Xunit;

namespace GPTino.Rhino.Tests;

public sealed class RhinoLaunchArgumentsTests
{
    [Fact]
    public void Configure_UsesExactRawRunScriptShapeForPathsWithSpaces()
    {
        var startInfo = new ProcessStartInfo();

        RhinoLaunchArguments.Configure(
            startInfo,
            @"C:\Owned Run\models\GPTino-E2E.gh",
            @"C:\Owned Run\models\GPTino-E2E.3dm");

        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(
            "/netcore-8 /nosplash /runscript=\"_GPTinoOpenPanel " +
            "_-Grasshopper _Banner _Disable _Enter " +
            "_-Grasshopper _Document _Open \"\"C:\\Owned Run\\models\\GPTino-E2E.gh\"\" _Enter " +
            "_GPTinoOpenPanel\" \"C:\\Owned Run\\models\\GPTino-E2E.3dm\"",
            startInfo.Arguments);
    }

    [Theory]
    [InlineData("C:\\Owned\\bad\"name.gh")]
    [InlineData("C:\\Owned\\bad\rname.gh")]
    [InlineData("C:\\Owned\\bad\nname.gh")]
    public void Configure_RejectsUnsafeDynamicPaths(string grasshopperPath)
    {
        var startInfo = new ProcessStartInfo();

        Assert.Throws<ArgumentException>(() => RhinoLaunchArguments.Configure(
            startInfo,
            grasshopperPath,
            @"C:\Owned\model.3dm"));
    }
}
