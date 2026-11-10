namespace PutridParrot.Kql.Runtime;

/// <summary>In-memory implementations of KQL aggregate functions that have no LINQ equivalent.</summary>
public static class KqlAggregates
{
    public static object? MakeList(IEnumerable<object?> values, long? maxSize) =>
        values.Where(v => v is not null).Select(Dynamic.Normalize).Take(Limit(maxSize)).ToList();

    public static object? MakeListWithNulls(IEnumerable<object?> values, long? maxSize) =>
        values.Select(Dynamic.Normalize).Take(Limit(maxSize)).ToList();

    public static object? MakeSet(IEnumerable<object?> values, long? maxSize)
    {
        var seen = new HashSet<object?>(KqlComparer.Values);
        var result = new List<object?>();
        var limit = Limit(maxSize);
        foreach (var v in values.Where(v => v is not null).Select(Dynamic.Normalize))
        {
            if (seen.Add(v)) result.Add(v);
            if (result.Count >= limit) break;
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

    /// <summary>percentiles_array: one value per requested percentile (literals or a dynamic array of them).</summary>
    public static object? PercentilesArray(IEnumerable<object?> values, object?[] percentiles)
    {
        var list = values.ToList();
        return percentiles.SelectMany(p => Dynamic.Normalize(p) is List<object?> l ? l : [p])
            .Select(p => Percentile(list, KqlConvert.ToDouble(p))).ToList();
    }

    /// <summary>Weighted nearest-rank percentile over (value, weight) pairs.</summary>
    public static object? WeightedPercentile(IEnumerable<object?[]> rows, double? percentile)
    {
        if (percentile is null) return null;
        var pairs = rows.Where(r => r[0] is not null && KqlConvert.ToDouble(r[1]) is > 0)
            .Select(r => (Value: r[0], Weight: KqlConvert.ToDouble(r[1])!.Value)).ToList();
        if (pairs.Count == 0) return null;
        pairs.Sort((a, b) => KqlComparer.For<object?>().Compare(a.Value, b.Value));
        var total = pairs.Sum(p => p.Weight);
        var target = percentile.Value / 100.0 * total;
        double running = 0;
        foreach (var (value, weight) in pairs)
        {
            running += weight;
            if (running >= target - 1e-9) return value;
        }
        return pairs[^1].Value;
    }

    public static object? WeightedPercentilesArray(IEnumerable<object?[]> rows, object?[] percentiles)
    {
        var list = rows.ToList();
        return percentiles.SelectMany(p => Dynamic.Normalize(p) is List<object?> l ? l : [p])
            .Select(p => WeightedPercentile(list, KqlConvert.ToDouble(p))).ToList();
    }

    /// <summary>Covariance of (x, y) pairs; sample (n - 1) or population (n).</summary>
    public static double? Covariance(IEnumerable<object?[]> rows, bool sample)
    {
        var pairs = rows.Select(r => (X: KqlConvert.ToDouble(r[0]), Y: KqlConvert.ToDouble(r[1])))
            .Where(p => p.X.HasValue && p.Y.HasValue).Select(p => (X: p.X!.Value, Y: p.Y!.Value)).ToList();
        if (pairs.Count == 0 || (sample && pairs.Count == 1)) return pairs.Count == 1 ? 0 : null;
        var mx = pairs.Average(p => p.X);
        var my = pairs.Average(p => p.Y);
        return pairs.Sum(p => (p.X - mx) * (p.Y - my)) / (sample ? pairs.Count - 1 : pairs.Count);
    }

    public static long? BinaryAllAnd(IEnumerable<object?> values) => Bits(values)?.Aggregate(-1L, (a, b) => a & b);
    public static long? BinaryAllOr(IEnumerable<object?> values) => Bits(values)?.Aggregate(0L, (a, b) => a | b);
    public static long? BinaryAllXor(IEnumerable<object?> values) => Bits(values)?.Aggregate(0L, (a, b) => a ^ b);

    private static List<long>? Bits(IEnumerable<object?> values)
    {
        var list = values.Select(KqlConvert.ToLong).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list;
    }

    // Sketches. PutridParrot.Kql keeps them exact: hll = the distinct values, tdigest = all values (weights expanded).

    public static object? Hll(IEnumerable<object?> values) =>
        new Dictionary<string, object?> { ["kql_hll"] = MakeSet(values, null) };

    public static object? HllMerge(IEnumerable<object?> sketches) =>
        Hll(sketches.SelectMany(s => Dynamic.Normalize(s) is Dictionary<string, object?> d && d.GetValueOrDefault("kql_hll") is List<object?> l ? l : []));

    public static object? Tdigest(IEnumerable<object?> values) =>
        new Dictionary<string, object?> { ["kql_tdigest"] = values.Where(v => v is not null).Select(Dynamic.Normalize).ToList() };

    public static object? TdigestWeighted(IEnumerable<object?[]> rows) =>
        Tdigest(rows.SelectMany(r => Enumerable.Repeat(r[0], (int)Math.Max(0, KqlConvert.ToLong(r[1]) ?? 1))));

    public static object? TdigestMerge(IEnumerable<object?> digests) =>
        Tdigest(digests.SelectMany(s => Dynamic.Normalize(s) is Dictionary<string, object?> d && d.GetValueOrDefault("kql_tdigest") is List<object?> l ? l : []));

    public static object? MinObject<T>(IEnumerable<T> rows, Func<T, object?> selector) =>
        rows.Select(selector).Where(v => v is not null).OrderBy(v => v, KqlComparer.For<object?>()).FirstOrDefault();

    public static object? MaxObject<T>(IEnumerable<T> rows, Func<T, object?> selector) =>
        rows.Select(selector).Where(v => v is not null).OrderByDescending(v => v, KqlComparer.For<object?>()).FirstOrDefault();

    private static int Limit(long? maxSize) => maxSize is null or <= 0 or > int.MaxValue ? int.MaxValue : (int)maxSize.Value;
}
