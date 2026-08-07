using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPTino.Contracts;

/// <summary>
/// Reads <see cref="AdapterOwner"/> accepting the pre-rename spellings ("wireify" → Script,
/// "cordyceps" → Canvas) so ChangeSets persisted in live-jobs.db, and change payloads from a
/// session started before an upgrade, still deserialize. Writes the current camelCase names.
/// </summary>
public sealed class LegacyAdapterOwnerConverter : JsonConverter<AdapterOwner>
{
    public override AdapterOwner Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (Enum.TryParse<AdapterOwner>(value, ignoreCase: true, out var owner))
        {
            return owner;
        }

        return value?.ToLowerInvariant() switch
        {
            "wireify" => AdapterOwner.Script,
            "cordyceps" => AdapterOwner.Canvas,
            _ => throw new JsonException($"Unknown AdapterOwner '{value}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, AdapterOwner value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
