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

    /// <summary>
    /// The recovery halt teaches "call recovery_resume" in rejection messages and house rules; the
    /// tool must actually exist in the model-facing schema (with its jobId argument) or the model
    /// is told to call something it cannot emit — the exact drift class this file guards.
    /// </summary>
    [Fact]
    public void RecoveryResumeToolIsExposedWithAJobIdArgument()
    {
        var json = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create());
        var tools = json[0].GetProperty("tools").EnumerateArray().ToArray();
        var resume = Assert.Single(tools, tool =>
            tool.GetProperty("name").GetString() == "recovery_resume");
        var schema = resume.GetProperty("inputSchema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("jobId", out _));
        Assert.Contains(
            "jobId",
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>
    /// W3: the change_submit schema uses additionalProperties=false, so without an explicit
    /// "intent" property the model could never declare a cleanup tier the backend enforces —
    /// the exact drift class this file guards. The enum must cover all three CleanupIntents.
    /// </summary>
    [Fact]
    public void ChangeSetSchemaExposesTheCleanupIntentEnum()
    {
        var intents = CollectEnumContaining(CleanupIntents.Relayout);
        Assert.Equal(
            new[] { CleanupIntents.Relayout, CleanupIntents.Regroup, CleanupIntents.Destructive },
            intents);

        var json = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create());
        var submit = Assert.Single(
            json[0].GetProperty("tools").EnumerateArray().ToArray(),
            tool => tool.GetProperty("name").GetString() == "change_submit");
        var changeSet = submit.GetProperty("inputSchema").GetProperty("properties").GetProperty("changeSet");
        Assert.True(changeSet.GetProperty("properties").TryGetProperty("intent", out _));
        // Optional: authoring ChangeSets omit it, so it must not be required.
        Assert.DoesNotContain(
            "intent",
            changeSet.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>
    /// W3 SHARED CONTRACT: approval_request targets gained optional label/role/impact — the panel
    /// renders them per target, so the model-facing schema must actually admit them
    /// (additionalProperties=false would otherwise reject every card that fills them).
    /// </summary>
    [Fact]
    public void ApprovalRequestTargetsExposeLabelRoleAndImpact()
    {
        var json = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create());
        var approval = Assert.Single(
            json[0].GetProperty("tools").EnumerateArray().ToArray(),
            tool => tool.GetProperty("name").GetString() == "approval_request");
        var targetProperties = approval
            .GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("items").GetProperty("items")
            .GetProperty("properties").GetProperty("targets")
            .GetProperty("items").GetProperty("properties");
        Assert.True(targetProperties.TryGetProperty("label", out _));
        Assert.True(targetProperties.TryGetProperty("role", out _));
        Assert.True(targetProperties.TryGetProperty("impact", out _));
        // The grant binding itself is unchanged: objectId+fingerprint stay the only required pair.
        var required = approval
            .GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("items").GetProperty("items")
            .GetProperty("properties").GetProperty("targets")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "objectId", "fingerprint" }, required);
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
