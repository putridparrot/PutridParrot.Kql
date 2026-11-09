using System.Text.RegularExpressions;

namespace Kql.Net.Runtime;

/// <summary>In-memory implementations of row-multiplying / row-extending operators (mv-expand, parse).</summary>
public static class RowOperators
{
    /// <summary>
    /// mv-expand: each row is repeated once per element of the expanded value(s). Several values expand in
    /// parallel (shorter ones are padded with null). A null value yields one row with null; an empty array or
    /// bag yields no rows. Bags expand into single-property bags, or <c>[key, value]</c> arrays when
    /// <paramref name="bagAsArray"/> is set.
    /// </summary>
    public static IEnumerable<TOut> MvExpand<TIn, TOut>(IEnumerable<TIn> rows, Func<TIn, object?[]> values,
        Func<TIn, object?[], long, TOut> make, bool bagAsArray, long? limit)
    {
        foreach (var row in rows)
        {
            var lists = values(row).Select(v => Elements(v, bagAsArray)).ToArray();
            var count = lists.Length == 0 ? 0 : lists.Max(l => l.Count);
            if (limit is { } max) count = (int)Math.Min(count, Math.Max(0, max));
            for (var i = 0; i < count; i++)
            {
                var items = new object?[lists.Length];
                for (var j = 0; j < lists.Length; j++) items[j] = i < lists[j].Count ? lists[j][i] : null;
                yield return make(row, items, i);
            }
        }
    }

    /// <summary>
    /// mv-apply: expands each record (like mv-expand) into a sub-table, runs the sub-query over it, and combines each
    /// sub-query row with the record.
    /// </summary>
    public static IEnumerable<TOut> MvApply<TIn, TContext, TSub, TOut>(IEnumerable<TIn> rows, Func<TIn, object?[]> values,
        Func<TIn, object?[], long, TContext> makeContext, long? limit, Func<List<TContext>, IEnumerable<TSub>> subQuery,
        Func<TIn, TSub, TOut> make)
    {
        foreach (var row in rows)
        {
            var lists = values(row).Select(v => Elements(v, bagAsArray: false)).ToArray();
            var count = lists.Length == 0 ? 0 : lists.Max(l => l.Count);
            if (limit is { } max) count = (int)Math.Min(count, Math.Max(0, max));
            var context = new List<TContext>(count);
            for (var i = 0; i < count; i++)
            {
                var items = new object?[lists.Length];
                for (var j = 0; j < lists.Length; j++) items[j] = i < lists[j].Count ? lists[j][i] : null;
                context.Add(makeContext(row, items, i));
            }
            foreach (var sub in subQuery(context)) yield return make(row, sub);
        }
    }

    /// <summary>parse-kv: extracts the listed keys and converts them (a value that won't convert becomes null).</summary>
    public static IEnumerable<TOut> ParseKv<TIn, TOut>(IEnumerable<TIn> rows, Func<TIn, string?> source, string[] keys, Type[] types,
        KeyValueSpec spec, Func<TIn, object?[], TOut> make)
    {
        foreach (var row in rows)
        {
            var raw = KeyValueParser.Extract(source(row), keys, spec);
            var values = new object?[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                values[i] = types[i] == typeof(object) ? Dynamic.Parse(raw[i])
                    : types[i] == typeof(string) ? raw[i] ?? ""
                    : KqlConvert.ChangeType(raw[i], types[i]);
            yield return make(row, values);
        }
    }

    private static List<object?> Elements(object? value, bool bagAsArray) => Dynamic.Normalize(value) switch
    {
        null => [null],
        List<object?> list => list,
        Dictionary<string, object?> bag => bag.Select(kv => bagAsArray
            ? (object?)new List<object?> { kv.Key, kv.Value }
            : new Dictionary<string, object?> { [kv.Key] = kv.Value }).ToList(),
        var scalar => [scalar]
    };

    /// <summary>
    /// parse / parse-where: matches <paramref name="regex"/> (with named groups k0..kN) against the source string
    /// and converts each group to its column type. In strict modes a failed conversion nulls every column;
    /// in relaxed mode only that column. With <paramref name="whereOnly"/>, non-matching rows are dropped.
    /// </summary>
    public static IEnumerable<TOut> Parse<TIn, TOut>(IEnumerable<TIn> rows, Func<TIn, string?> source, Regex regex,
        Type[] types, bool relaxed, Func<TIn, object?[], TOut> make, bool whereOnly)
    {
        foreach (var row in rows)
        {
            var values = new object?[types.Length];
            var s = source(row);
            var m = s is null ? null : regex.Match(s);
            var ok = m is { Success: true };
            if (ok)
            {
                for (var i = 0; i < types.Length; i++)
                {
                    var text = m!.Groups["k" + i].Value;
                    var v = KqlConvert.ChangeType(text, types[i]);
                    if (v is null && types[i] != typeof(string) && types[i] != typeof(object) && !relaxed)
                    {
                        ok = false;
                        break;
                    }
                    values[i] = types[i] == typeof(object) ? Dynamic.Parse(text) : v;
                }
                if (!ok) Array.Clear(values);
            }
            if (whereOnly && !ok) continue;
            yield return make(row, values);
        }
    }
}
