using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoGame;

internal static class WireJson
{
    internal static AutoGameValue FromJson(JsonNode? node)
    {
        if (node is null) return AutoGameValue.Null;
        if (node is JsonObject obj)
        {
            var values = new Dictionary<string, AutoGameValue>(StringComparer.Ordinal);
            foreach (var pair in obj) values.Add(pair.Key, FromJson(pair.Value));
            return AutoGameValue.FromObject(values);
        }
        if (node is JsonArray array)
            return AutoGameValue.FromArray(array.Select(FromJson).ToList());
        if (node is not JsonValue scalar) throw new InvalidDataException("Unsupported JSON node.");
        if (scalar.TryGetValue<bool>(out var boolean)) return AutoGameValue.FromBoolean(boolean);
        if (scalar.TryGetValue<long>(out var signed)) return AutoGameValue.FromInt64(signed);
        if (scalar.TryGetValue<ulong>(out var unsigned)) return AutoGameValue.FromUInt64(unsigned);
        if (scalar.TryGetValue<double>(out var number)) return AutoGameValue.FromDouble(number);
        if (scalar.TryGetValue<string>(out var text)) return AutoGameValue.FromString(text);
        using var document = JsonDocument.Parse(scalar.ToJsonString());
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Number when document.RootElement.TryGetInt64(out signed) => AutoGameValue.FromInt64(signed),
            JsonValueKind.Number when document.RootElement.TryGetUInt64(out unsigned) => AutoGameValue.FromUInt64(unsigned),
            JsonValueKind.Number => AutoGameValue.FromDouble(document.RootElement.GetDouble()),
            _ => throw new InvalidDataException("Unsupported JSON scalar.")
        };
    }

    internal static JsonNode? ToJson(AutoGameValue value)
    {
        return value.Kind switch
        {
            AutoGameValueKind.Null => null,
            AutoGameValueKind.Boolean => JsonValue.Create(value.AsBoolean()),
            AutoGameValueKind.Int64 => JsonValue.Create(value.AsInt64()),
            AutoGameValueKind.UInt64 => JsonValue.Create(value.AsUInt64()),
            AutoGameValueKind.Double => JsonValue.Create(value.AsDouble()),
            AutoGameValueKind.String => JsonValue.Create(value.AsString()),
            AutoGameValueKind.Array => new JsonArray(value.AsArray().Select(ToJson).ToArray()),
            AutoGameValueKind.Object => ToObject(value.AsObject()),
            _ => throw new InvalidDataException("Unsupported wire value.")
        };
    }

    internal static JsonObject ToObject(IDictionary<string, AutoGameValue> values)
    {
        var result = new JsonObject();
        foreach (var pair in values) result.Add(pair.Key, ToJson(pair.Value));
        return result;
    }
}
