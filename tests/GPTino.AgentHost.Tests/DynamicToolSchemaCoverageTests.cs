using System.Text.Json;
using GPTino.AgentHost.Codex;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Guards the drift that let <c>referenceRhinoObjects</c> and the six semantic predicate kinds
/// reach the backend, contracts, and prompt while the change_submit tool schema still restricted
/// the model to the old enum — so the model could never actually emit them. Every OperationKind /
/// PredicateKind is either exposed in the schema enum or listed here as deliberately model-hidden.
/// </summary>
public sealed class DynamicToolSchemaCoverageTests
{
    // OperationKinds the model must never submit through change_submit — server-internal or
    // synthesized only. Anything NOT here MUST appear in the schema's operation-kind enum.
    private static readonly HashSet<string> NonModelOperationKinds = new(StringComparer.Ordinal)
    {
        "rename", "setSolverState", "updateRhinoLayer", "documentGlobal",
    };

    // Predicate kinds not exposed to the model: legacy exact-match variants + the freeform sentinel.
    private static readonly HashSet<string> NonModelPredicateKinds = new(StringComparer.Ordinal)
    {
        "outputEquals", "boundingBoxEquals", "custom",
    };

    [Fact]
    public void OperationKindEnumCoversEveryModelFacingKind()
    {
        var schema = CollectEnumContaining("moveComponent");
        var expected = EnumCamelNames<OperationKind>()
            .Where(name => !NonModelOperationKinds.Contains(name));
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), schema.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void PredicateKindEnumCoversEveryModelFacingKind()
    {
        var schema = CollectEnumContaining("fingerprintEquals");
        var expected = EnumCamelNames<PredicateKind>()
            .Where(name => !NonModelPredicateKinds.Contains(name));
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), schema.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static IEnumerable<string> EnumCamelNames<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Select(JsonNamingPolicy.CamelCase.ConvertName);

    // Serialize the whole tool-spec tree and pull out the single `enum` string array that contains
    // the marker value — the marker uniquely identifies the operation-kind vs predicate-kind enum.
    private static IReadOnlyList<string> CollectEnumContaining(string marker)
    {
        var json = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create());
        var matches = new List<IReadOnlyList<string>>();
        Walk(json, marker, matches);
        Assert.Single(matches);
        return matches[0];
    }

    private static void Walk(JsonElement element, string marker, List<IReadOnlyList<string>> matches)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("enum") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var values = property.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .ToArray();
                        if (values.Contains(marker))
                        {
                            matches.Add(values);
                        }
                    }

                    Walk(property.Value, marker, matches);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, marker, matches);
                }

                break;
        }
    }
}
