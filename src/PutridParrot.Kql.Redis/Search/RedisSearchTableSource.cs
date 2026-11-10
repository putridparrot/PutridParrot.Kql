using System.Globalization;
using System.Reflection;
using PutridParrot.Kql.Runtime;
using StackExchange.Redis;

namespace PutridParrot.Kql.Redis.Search;

/// <summary>Options for a RediSearch table.</summary>
public sealed record RedisSearchOptions
{
    /// <summary>Database number (default: the connection's default database).</summary>
    public int Database { get; init; } = -1;

    /// <summary>Name of the column that holds each document's Redis key (default <c>key</c>).</summary>
    public string KeyColumn { get; init; } = "key";

    /// <summary>Documents fetched per FT.SEARCH page (default 1000).</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>
    /// Push <c>has</c>, <c>has_any</c>, <c>has_all</c>, <c>==</c> and <c>=~</c> on TEXT attributes into the search
    /// (default false). RediSearch tokenizes text differently from KQL (for example <c>_</c> doesn't separate terms, and
    /// stemming applies), so a document KQL would match can be missed when its text joins words with such characters.
    /// Enable it when that can't happen in your data. NUMERIC and TAG conditions are always pushed down.
    /// </summary>
    public bool PushDownText { get; init; }

    /// <summary>Called with each FT.SEARCH command sent (for logging and diagnostics).</summary>
    public Action<string>? OnCommand { get; init; }
}

/// <summary>
/// A table over a RediSearch index (FT.CREATE ... ON HASH or ON JSON): one row per indexed document, with a key column
/// and one column per index attribute (NUMERIC as <c>real</c>, TAG/TEXT/GEO as <c>string</c>; multi-value JSON paths
/// as <c>dynamic</c>). Filters, limits, numeric top-N and counts are pushed into FT.SEARCH where KQL semantics allow;
/// everything else runs in memory over the documents found. See <see cref="RedisSearchOptions"/>.
/// </summary>
public sealed class RedisSearchTableSource : IKqlTableSource
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RowSetTableSource _rows;
    private readonly IReadOnlyList<RedisSearchField> _fields;
    private readonly RedisSearchProvider _provider;

    public RedisSearchTableSource(IConnectionMultiplexer redis, string index, RedisSearchOptions? options = null)
    {
        _redis = redis;
        Options = options ?? new RedisSearchOptions();
        Index = RedisSearchIndex.Read(Database, index);
        _fields = Index.Fields.Where(f => f.Type != RedisSearchFieldType.Other).ToList();

        var columns = new List<(string, Type)> { (Options.KeyColumn, typeof(string)) };
        columns.AddRange(_fields.Select(f => (f.Name, ColumnType(f))));
        _rows = new RowSetTableSource(columns, () => throw new InvalidOperationException("Rows are fetched by the RediSearch provider."));

        var properties = ElementType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.MetadataToken).ToArray();
        FieldsByMember = _fields.Select((f, i) => (properties[i + 1].Name, f)).ToDictionary(x => x.Name, x => x.f, StringComparer.Ordinal);
        _provider = new RedisSearchProvider(this);
    }

    public RedisSearchIndex Index { get; }

    public RedisSearchOptions Options { get; }

    public Type ElementType => _rows.ElementType;

    /// <summary>Row-type property name -> index attribute.</summary>
    internal IReadOnlyDictionary<string, RedisSearchField> FieldsByMember { get; }

    private IDatabase Database => _redis.GetDatabase(Options.Database);

    public IQueryable GetQuery() =>
        (IQueryable)Activator.CreateInstance(typeof(RedisSearchQueryable<>).MakeGenericType(ElementType), _provider)!;

    private Type ColumnType(RedisSearchField f) => f switch
    {
        _ when Index.IsJson && f.MultiValued => typeof(object),
        { Type: RedisSearchFieldType.Numeric } => typeof(double),
        _ => typeof(string)
    };

    private int Dialect => Index.IsJson ? 3 : 2; // dialect 3 returns JSON values as JSON arrays

    // ------------------------------------------------------------------ FT.SEARCH

    /// <summary>The number of documents matching a filter (FT.SEARCH ... LIMIT 0 0).</summary>
    internal long Count(SearchFilter filter) => (long)Search(filter.Query, null, 0, 0).Total;

    /// <summary>Fetches the documents a plan needs, as rows of <see cref="ElementType"/>.</summary>
    internal Array Fetch(SearchPlan plan)
    {
        var rows = new List<object>();
        if (plan is { Sort: { } sort, Limit: { } top })
        {
            // KQL puts nulls first ascending and last descending; RediSearch's SORTBY has no such rule, so documents with
            // and without a value are fetched separately
            var present = $"@{SearchFilterTranslator.Name(sort.Field)}:[-inf +inf]";
            var withValue = Combine(plan.Filter, present);
            var withoutValue = Combine(plan.Filter, "-" + present);
            if (sort.Descending)
            {
                rows.AddRange(Search(withValue, sort, 0, top).Rows);
                if (rows.Count < top) rows.AddRange(Search(withoutValue, null, 0, top - rows.Count).Rows);
            }
            else
            {
                rows.AddRange(Search(withoutValue, null, 0, top).Rows);
                if (rows.Count < top) rows.AddRange(Search(withValue, sort, 0, top - rows.Count).Rows);
            }
        }
        else if (plan.Limit is { } limit)
        {
            rows.AddRange(Search(plan.Filter.Query, null, 0, limit).Rows);
        }
        else
        {
            for (var offset = 0; ; offset += Options.PageSize)
            {
                var page = Search(plan.Filter.Query, null, offset, Options.PageSize);
                rows.AddRange(page.Rows);
                if (page.Rows.Count < Options.PageSize || offset + page.Rows.Count >= page.Total) break;
            }
        }

        var typed = Array.CreateInstance(ElementType, rows.Count);
        for (var i = 0; i < rows.Count; i++) typed.SetValue(rows[i], i);
        return typed;
    }

    private static string Combine(SearchFilter filter, string extra) => filter.IsAll ? extra : $"({filter.Query}) {extra}";

    private (double Total, List<object> Rows) Search(string query, (RedisSearchField Field, bool Descending)? sort, int offset, int count)
    {
        var args = new List<object> { Index.Name, query };
        if (Index.IsJson && count > 0)
        {
            args.Add("RETURN");
            args.Add(_fields.Count);
            args.AddRange(_fields.Select(f => (object)f.Name));
        }
        if (sort is { } s)
        {
            args.Add("SORTBY");
            args.Add(s.Field.Name);
            args.Add(s.Descending ? "DESC" : "ASC");
        }
        args.AddRange(["LIMIT", offset, count, "DIALECT", Dialect]);
        Options.OnCommand?.Invoke("FT.SEARCH " + string.Join(" ", args.Select(a => a is string str && str.Contains(' ') ? $"\"{str}\"" : a)));

        var reply = (RedisResult[])Database.Execute("FT.SEARCH", args.ToArray())!;
        var rows = new List<object>();
        if (reply.Length > 0 && reply[0].Resp2Type != ResultType.Integer)
        {
            // RESP3: {total_results: n, results: [{id: key, extra_attributes: {field: value, ...}}, ...], ...}
            var map = Pairs(reply);
            foreach (var result in map.TryGetValue("results", out var results) ? (RedisResult[])results! : [])
            {
                var entry = Pairs((RedisResult[])result!);
                var attributes = entry.TryGetValue("extra_attributes", out var a) && !a.IsNull ? (RedisResult[])a! : [];
                rows.Add(ToRow(entry.TryGetValue("id", out var id) ? (string?)id : null, attributes));
            }
            return (map.TryGetValue("total_results", out var t) ? (double)t : rows.Count, rows);
        }

        // RESP2: [total, key, [field, value, ...], key, [...], ...]
        for (var i = 1; i + 1 < reply.Length; i += 2) rows.Add(ToRow((string?)reply[i], (RedisResult[])reply[i + 1]!));
        return ((double)reply[0], rows);
    }

    private static Dictionary<string, RedisResult> Pairs(RedisResult[] items)
    {
        var map = new Dictionary<string, RedisResult>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < items.Length; i += 2) map[(string?)items[i] ?? ""] = items[i + 1];
        return map;
    }

    private object ToRow(string? key, RedisResult[] fields)
    {
        var values = new object?[_fields.Count + 1];
        values[0] = key;
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var name = (string?)fields[i];
            var index = Index.IsJson
                ? IndexOf(f => f.Name == name)
                : IndexOf(f => f.Identifier == name);
            if (index < 0) continue;
            values[index + 1] = Value(_fields[index], (string?)fields[i + 1]);
        }
        return _rows.CreateRow(values);
    }

    private int IndexOf(Func<RedisSearchField, bool> match)
    {
        for (var i = 0; i < _fields.Count; i++) if (match(_fields[i])) return i;
        return -1;
    }

    private object? Value(RedisSearchField field, string? raw)
    {
        if (raw is null) return null;
        if (Index.IsJson)
        {
            // DIALECT 3: each value is a JSON array of the matches
            var parsed = Dynamic.Parse(raw);
            if (field.MultiValued) return parsed;
            return parsed is List<object?> list ? (list.Count > 0 ? list[0] : null) : parsed;
        }
        return field.Type == RedisSearchFieldType.Numeric
            ? double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null
            : raw;
    }
}
