namespace Kql.Net.Runtime;

/// <summary>In-memory implementations of KQL aggregate functions that have no LINQ equivalent.</summary>
public static class KqlAggregates
{
    public static object? MakeList(IEnumerable<object?> values, long? maxSize) =>
        values.Where(v => v is not null).Select(Dynamic.Normalize).Take(Limit(maxSize)).ToList();

    public static object? MakeListWithNulls(IEnumerable<object?> values, long? maxSize) =>
        values.Select(Dynamic.Normalize).Take(Limit(maxSize)).ToList();

    public static object? MakeSet(IEnumerable<object?> values, long? maxSize)
    {
        var result = new List<object?>();
        foreach (var v in values.Where(v => v is not null).Select(Dynamic.Normalize))
        {
            if (!result.Any(x => KqlComparer.ValueEquals(x, v))) result.Add(v);
            if (result.Count >= Limit(maxSize)) break;
        }
        return result;
    }

    public static object? MakeBag(IEnumerable<object?> values, long? maxSize)
    {
        var bag = new Dictionary<string, object?>();
        foreach (var v in values.Select(Dynamic.Normalize))
        {
            if (v is not Dictionary<string, object?> d) continue;
            foreach (var kv in d)
            {
                if (bag.Count >= Limit(maxSize)) return bag;
                bag.TryAdd(kv.Key, kv.Value);
            }
        }
        return bag;
    }

    public static double? Variance(IEnumerable<object?> values) => VarianceCore(values, sample: true);
    public static double? VarianceP(IEnumerable<object?> values) => VarianceCore(values, sample: false);
    public static double? Stdev(IEnumerable<object?> values) => Variance(values) is { } v ? Math.Sqrt(v) : null;
    public static double? StdevP(IEnumerable<object?> values) => VarianceP(values) is { } v ? Math.Sqrt(v) : null;

    private static double? VarianceCore(IEnumerable<object?> values, bool sample)
    {
        var xs = values.Select(KqlConvert.ToDouble).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (xs.Count == 0) return null;
        if (sample && xs.Count == 1) return 0;
        var mean = xs.Average();
        var sum = xs.Sum(x => (x - mean) * (x - mean));
        return sum / (sample ? xs.Count - 1 : xs.Count);
    }

    /// <summary>Nearest-rank percentile (KQL percentile() is an estimate; nearest-rank matches it for small sets).</summary>
    public static object? Percentile(IEnumerable<object?> values, double? percentile)
    {
        if (percentile is null) return null;
        var xs = values.Where(v => v is not null).ToList();
        if (xs.Count == 0) return null;
        xs.Sort(KqlComparer.For<object?>());
        var rank = (int)Math.Ceiling(percentile.Value / 100.0 * xs.Count);
        return xs[Math.Clamp(rank - 1, 0, xs.Count - 1)];
    }

    public static object? MinObject<T>(IEnumerable<T> rows, Func<T, object?> selector) =>
        rows.Select(selector).Where(v => v is not null).OrderBy(v => v, KqlComparer.For<object?>()).FirstOrDefault();

    public static object? MaxObject<T>(IEnumerable<T> rows, Func<T, object?> selector) =>
        rows.Select(selector).Where(v => v is not null).OrderByDescending(v => v, KqlComparer.For<object?>()).FirstOrDefault();

    private static int Limit(long? maxSize) => maxSize is null or <= 0 or > int.MaxValue ? int.MaxValue : (int)maxSize.Value;
}
