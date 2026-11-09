using System.Collections;
using System.Text.Json;

namespace Kql.Net.Runtime;

/// <summary>
/// Helpers for KQL <c>dynamic</c> values. Dynamic values produced by Kql.Net are plain CLR graphs:
/// <see cref="Dictionary{TKey,TValue}"/> (string keys) for property bags, <see cref="List{T}"/> for arrays,
/// and long/double/string/bool/null for scalars. Values supplied by callers (POCOs, JsonNode, JsonElement…)
/// are also accepted wherever a dynamic is read.
/// </summary>
public static class Dynamic
{
    /// <summary>Parses JSON text into a CLR graph; invalid JSON yields the original string (KQL semantics).</summary>
    public static object? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static object? FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Aggregate(new Dictionary<string, object?>(), (d, p) => { d[p.Name] = FromElement(p.Value); return d; }),
        JsonValueKind.Array => e.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    /// <summary>Normalises any supported dynamic representation into the CLR graph form.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonElement je => FromElement(je),
        System.Text.Json.Nodes.JsonNode jn => Parse(jn.ToJsonString()),
        string or bool or long or double or decimal or DateTime or TimeSpan or Guid => value,
        int i => (long)i,
        float f => (double)f,
        IDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        IDictionary d => d.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => Normalize(d[k])),
        IEnumerable e => e.Cast<object?>().Select(Normalize).ToList(),
        _ when Schema.KqlTypes.IsScalarClrType(value.GetType()) => value,
        _ => Parse(JsonSerializer.Serialize(value))
    };

    /// <summary>Enumerates the elements of a dynamic array (or nothing if not an array).</summary>
    public static IEnumerable<object?> Elements(object? value) => Normalize(value) switch
    {
        List<object?> list => list,
        _ => []
    };
}
