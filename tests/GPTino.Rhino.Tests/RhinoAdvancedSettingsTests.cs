using System.Reflection;
using Xunit;

namespace GPTino.Rhino.Tests;

/// <summary>
/// Canary for the reflection-based advanced-settings accessor: the float-prompt feature depends on
/// RhinoCommon internals (the public Rhino.Runtime.AdvancedSetting enum value and the internal
/// UnsafeNativeMethods.CRhAdvancedSettings_GetBool/SetBool accessors). The RhinoCommon package
/// ships reference assemblies that cannot be loaded for execution, so the member shapes are pinned
/// via MetadataLoadContext against the copied package assembly — the build fails the moment a
/// Rhino update renames them, instead of the prompt silently never appearing. The names come from
/// RhinoAdvancedSettings itself so the test cannot drift from production.
/// </summary>
public sealed class RhinoAdvancedSettingsTests
{
    [Fact]
    public void RhinoCommonStillExposesTheHideFloatingWindowsAccessors()
    {
        var rhinoCommonPath = Path.Combine(AppContext.BaseDirectory, "RhinoCommon.dll");
        Assert.True(File.Exists(rhinoCommonPath), $"RhinoCommon.dll not found at {rhinoCommonPath}");

        var runtimeAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(rhinoCommonPath);
        using var context = new MetadataLoadContext(new PathAssemblyResolver(runtimeAssemblies));
        var rhinoCommon = context.LoadFromAssemblyPath(rhinoCommonPath);

        var enumType = rhinoCommon.GetType(RhinoAdvancedSettings.EnumTypeName);
        Assert.NotNull(enumType);
        Assert.True(enumType!.IsEnum, "AdvancedSetting is no longer an enum");
        Assert.NotNull(enumType.GetField(RhinoAdvancedSettings.EnumValueName));

        var accessor = rhinoCommon.GetType(RhinoAdvancedSettings.AccessorTypeName);
        Assert.NotNull(accessor);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var get = accessor!.GetMethods(flags).SingleOrDefault(method =>
            method.Name == RhinoAdvancedSettings.GetMethodName &&
            method.GetParameters() is [{ ParameterType: var only }] &&
            only == enumType);
        var set = accessor.GetMethods(flags).SingleOrDefault(method =>
            method.Name == RhinoAdvancedSettings.SetMethodName &&
            method.GetParameters() is [{ ParameterType: var first }, { ParameterType.FullName: "System.Boolean" }] &&
            first == enumType);
        Assert.NotNull(get);
        Assert.Equal("System.Boolean", get!.ReturnType.FullName);
        Assert.NotNull(set);
    }
}
