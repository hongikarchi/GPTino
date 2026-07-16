using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GPTino.Rhino.Tests;

public sealed class PluginMetadataTests
{
    private const string ExpectedPluginId = "b903e20d-1cb3-4d8e-b37d-9be263a678d4";

    [Fact]
    public void AssemblyExposesTheRhinoPluginId()
    {
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "GPTino.Rhino.rhp");
        using var stream = File.OpenRead(pluginPath);
        using var portableExecutable = new PEReader(stream);
        var metadata = portableExecutable.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();

        var assemblyGuid = assembly.GetCustomAttributes()
            .Select(handle => ReadGuidAttribute(metadata, handle))
            .Single(value => value is not null);

        Assert.Equal(ExpectedPluginId, assemblyGuid, ignoreCase: true);
    }

    private static string? ReadGuidAttribute(MetadataReader metadata, CustomAttributeHandle handle)
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference)
        {
            return null;
        }

        var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (metadata.GetString(type.Namespace) != "System.Runtime.InteropServices" ||
            metadata.GetString(type.Name) != "GuidAttribute")
        {
            return null;
        }

        var value = metadata.GetBlobReader(attribute.Value);
        Assert.Equal(1, value.ReadUInt16());
        return value.ReadSerializedString();
    }
}
