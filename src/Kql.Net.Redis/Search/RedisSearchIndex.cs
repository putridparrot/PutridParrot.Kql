using StackExchange.Redis;

namespace Kql.Net.Redis.Search;

/// <summary>The type of a RediSearch index attribute.</summary>
public enum RedisSearchFieldType
{
    Text,
    Tag,
    Numeric,
    Geo,
    Other
}

/// <summary>An attribute (field) of a RediSearch index, as reported by FT.INFO.</summary>
/// <param name="Name">The attribute name used in queries (the alias when one is set).</param>
/// <param name="Identifier">The hash field name or JSON path the attribute indexes.</param>
/// <param name="Type">The attribute type.</param>
/// <param name="CaseSensitive">For TAG attributes: whether matching is case-sensitive (CASESENSITIVE).</param>
/// <param name="Separator">For TAG attributes: the character separating several tags in one value.</param>
public sealed record RedisSearchField(string Name, string Identifier, RedisSearchFieldType Type, bool CaseSensitive, char Separator)
{
    /// <summary>For JSON indexes: true when the path can select several values (e.g. <c>$.tags[*]</c>).</summary>
    public bool MultiValued => Identifier.Contains("[*]") || Identifier.Contains("..") || Identifier.Contains("?(");
}

/// <summary>A RediSearch index definition: what it indexes (hashes or JSON documents) and its attributes.</summary>
public sealed class RedisSearchIndex
{
    /// <summary>The default RediSearch stop words (terms that are not indexed in TEXT fields).</summary>
    public static readonly IReadOnlySet<string> DefaultStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "is", "the", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "it", "no", "not",
        "of", "on", "or", "such", "that", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with"
    };

    public RedisSearchIndex(string name, bool isJson, IReadOnlyList<RedisSearchField> fields, IReadOnlySet<string>? stopWords = null)
    {
        Name = name;
        IsJson = isJson;
        Fields = fields;
        StopWords = stopWords ?? DefaultStopWords;
    }

    public string Name { get; }

    /// <summary>True for an index ON JSON, false for ON HASH.</summary>
    public bool IsJson { get; }

    public IReadOnlyList<RedisSearchField> Fields { get; }

    public IReadOnlySet<string> StopWords { get; }

    /// <summary>Reads an index definition with FT.INFO.</summary>
    public static RedisSearchIndex Read(IDatabase db, string index)
    {
        var info = Pairs(db.Execute("FT.INFO", index));
        var definition = info.TryGetValue("index_definition", out var d) ? Pairs(d) : [];
        var isJson = definition.TryGetValue("key_type", out var kt) && string.Equals((string?)kt, "JSON", StringComparison.OrdinalIgnoreCase);

        var fields = new List<RedisSearchField>();
        if (info.TryGetValue("attributes", out var attributes))
        {
            foreach (var attribute in (RedisResult[])attributes!)
            {
                var tokens = Flatten(attribute).ToArray();
                string? Value(string key)
                {
                    var i = Array.FindIndex(tokens, t => string.Equals(t, key, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < tokens.Length ? tokens[i + 1] : null;
                }
                var identifier = Value("identifier") ?? "";
                var name = Value("attribute") ?? identifier;
                var type = (Value("type") ?? "").ToUpperInvariant() switch
                {
                    "TEXT" => RedisSearchFieldType.Text,
                    "TAG" => RedisSearchFieldType.Tag,
                    "NUMERIC" => RedisSearchFieldType.Numeric,
                    "GEO" => RedisSearchFieldType.Geo,
                    _ => RedisSearchFieldType.Other
                };
                var separator = Value("SEPARATOR") is { Length: > 0 } s ? s[0] : ',';
                var caseSensitive = tokens.Any(t => string.Equals(t, "CASESENSITIVE", StringComparison.OrdinalIgnoreCase));
                fields.Add(new RedisSearchField(name, identifier, type, caseSensitive, separator));
            }
        }

        IReadOnlySet<string>? stopWords = null;
        if (info.TryGetValue("stopwords_list", out var sw) && !sw.IsNull)
            stopWords = Flatten(sw).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RedisSearchIndex(index, isJson, fields, stopWords);
    }

    /// <summary>The scalar strings of a reply, with nested arrays (e.g. flag lists) flattened in order.</summary>
    private static IEnumerable<string> Flatten(RedisResult result)
    {
        if (result.IsNull) yield break;
        if (result.Resp2Type == ResultType.Array)
        {
            foreach (var item in (RedisResult[])result!)
                foreach (var s in Flatten(item))
                    yield return s;
        }
        else yield return (string?)result ?? "";
    }

    /// <summary>A flat [name, value, name, value...] reply as a dictionary (RESP2), or a map reply (RESP3).</summary>
    private static Dictionary<string, RedisResult> Pairs(RedisResult result)
    {
        var map = new Dictionary<string, RedisResult>(StringComparer.OrdinalIgnoreCase);
        if (result.IsNull) return map;
        var items = (RedisResult[])result!;
        for (var i = 0; i + 1 < items.Length; i += 2) map[(string?)items[i] ?? ""] = items[i + 1];
        return map;
    }
}
