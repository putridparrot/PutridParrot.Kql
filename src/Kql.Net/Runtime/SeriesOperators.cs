namespace Kql.Net.Runtime;

/// <summary>The bins of a make-series axis.</summary>
/// <param name="From">Start (null: per series, the first bin with data).</param>
/// <param name="To">Exclusive end for from/to/step (null: per series, the end of the last bin with data); inclusive stop for <c>in range</c>.</param>
/// <param name="Step">Bin size (timespan for datetime/timespan axes, numeric otherwise).</param>
/// <param name="InRange">True for the <c>in range(start, stop, step)</c> form (bins aligned with bin(), stop inclusive).</param>
public sealed record SeriesRange(object? From, object? To, object Step, bool InRange);

/// <summary>A level of top-nested.</summary>
public sealed record TopNestedLevel<TRow>(Func<TRow, object?> Key, Func<IEnumerable<TRow>, object?> Aggregate, long? Count,
    bool HasOthers, object? Others, bool Ascending);

/// <summary>In-memory implementations of make-series and top-nested.</summary>
public static class SeriesOperators
{
    /// <summary>
    /// make-series: groups rows by key, bins the axis, aggregates each bin, and fills empty bins with the default.
    /// <paramref name="make"/> receives (group key, aggregate arrays, axis array).
    /// </summary>
    public static IEnumerable<TOut> MakeSeries<TRow, TOut>(IEnumerable<TRow> rows, Func<TRow, object?> groupKey, Func<TRow, object?> axis,
        Func<IEnumerable<TRow>, object?>[] aggregates, object?[] defaults, Type[] aggregateTypes, SeriesRange range, bool nonEmpty,
        Func<object?, List<object?>[], List<object?>, TOut> make)
    {
        var groups = new List<(object? Key, List<(TRow Row, double Position)> Rows)>();
        var index = new Dictionary<object, int>();
        Type? axisType = null;
        foreach (var row in rows)
        {
            var a = axis(row);
            if (a is null) continue;
            axisType ??= a.GetType();
            var key = groupKey(row);
            var k = key ?? NullKey.Instance;
            if (!index.TryGetValue(k, out var gi))
            {
                index[k] = gi = groups.Count;
                groups.Add((key, []));
            }
            groups[gi].Rows.Add((row, Position(a)));
        }

        axisType ??= range.From?.GetType() ?? range.To?.GetType() ?? (range.Step is TimeSpan ? typeof(DateTime) : typeof(double));
        var step = Position(range.Step);
        if (step <= 0) throw new KqlException("make-series step must be positive.");

        if (groups.Count == 0 && nonEmpty && range.From is not null && range.To is not null)
            groups.Add((null, []));

        foreach (var (key, groupRows) in groups)
        {
            if (!Bins(range, groupRows, step, out var start, out var count)) continue;

            var perBin = new List<TRow>[count];
            foreach (var (row, pos) in groupRows)
            {
                var k = (long)Math.Floor((pos - start) / step + 1e-9);
                if (k < 0 || k >= count) continue; // outside the range
                (perBin[k] ??= []).Add(row);
            }

            var arrays = new List<object?>[aggregates.Length];
            for (var a = 0; a < aggregates.Length; a++)
            {
                var fallback = Dynamic.Normalize(KqlConvert.ChangeType(defaults[a], aggregateTypes[a]));
                var values = new List<object?>(count);
                for (var k = 0; k < count; k++)
                    values.Add(perBin[k] is { } binRows ? Dynamic.Normalize(aggregates[a](binRows)) : fallback);
                arrays[a] = values;
            }

            var axisValues = new List<object?>(count);
            for (var k = 0; k < count; k++) axisValues.Add(FromPosition(start + k * step, axisType, range.Step));
            yield return make(key, arrays, axisValues);
        }
    }

    private static bool Bins<TRow>(SeriesRange range, List<(TRow Row, double Position)> rows, double step, out double start, out int count)
    {
        start = 0;
        count = 0;
        double end;
        if (range.InRange)
        {
            if (range.From is null || range.To is null) return false;
            start = Math.Floor(Position(range.From) / step) * step;
            var last = Math.Floor(Position(range.To) / step) * step;
            end = last + step;
        }
        else
        {
            if (range.From is not null) start = Position(range.From);
            else if (rows.Count > 0) start = Math.Floor(rows.Min(r => r.Position) / step) * step;
            else return false;

            if (range.To is not null) end = Position(range.To);
            else if (rows.Count > 0) end = start + (Math.Floor((rows.Max(r => r.Position) - start) / step) + 1) * step;
            else return false;
        }
        var n = Math.Ceiling((end - start) / step - 1e-9);
        if (n <= 0) return false;
        if (n > 1 << 20) throw new KqlException("make-series would produce more than 1,048,576 bins.");
        count = (int)n;
        return true;
    }

    private sealed class NullKey
    {
        public static readonly NullKey Instance = new();
    }

    private static double Position(object value) => value switch
    {
        DateTime dt => dt.Ticks,
        TimeSpan ts => ts.Ticks,
        _ => KqlConvert.ToDouble(value) ?? throw new KqlException($"make-series can't bin value '{value}'.")
    };

    private static object FromPosition(double position, Type axisType, object step)
    {
        if (axisType == typeof(DateTime)) return new DateTime((long)Math.Round(position), DateTimeKind.Utc);
        if (axisType == typeof(TimeSpan)) return TimeSpan.FromTicks((long)Math.Round(position));
        var integral = (axisType == typeof(long) || axisType == typeof(int)) && step is long or int;
        return integral ? (object)(long)Math.Round(position) : position;
    }

    /// <summary>
    /// top-nested: at each level, groups the rows by the level's key, aggregates, keeps the top N (and optionally an
    /// "others" row aggregating the rest), and recurses into each kept group. <paramref name="make"/> receives the
    /// (key, aggregate) values of every level, flattened.
    /// </summary>
    public static IEnumerable<TOut> TopNested<TRow, TOut>(IEnumerable<TRow> rows, TopNestedLevel<TRow>[] levels, Func<object?[], TOut> make)
    {
        var values = new object?[levels.Length * 2];
        var results = new List<TOut>();
        Recurse(rows.ToList(), 0);
        return results;

        void Recurse(List<TRow> input, int level)
        {
            if (level == levels.Length)
            {
                results.Add(make((object?[])values.Clone()));
                return;
            }

            var l = levels[level];
            var groups = new List<(object? Key, List<TRow> Rows)>();
            var index = new Dictionary<object, int>();
            foreach (var row in input)
            {
                var key = l.Key(row);
                var k = (object?)Dynamic.Normalize(key) ?? NullKey.Instance;
                if (!index.TryGetValue(k, out var gi))
                {
                    index[k] = gi = groups.Count;
                    groups.Add((key, []));
                }
                groups[gi].Rows.Add(row);
            }

            var scored = groups.Select(g => (g.Key, g.Rows, Agg: l.Aggregate(g.Rows))).ToList();
            var comparer = KqlComparer.For<object?>();
            var ordered = l.Ascending
                ? scored.OrderBy(g => g.Agg, comparer).ToList()
                : scored.OrderByDescending(g => g.Agg, comparer).ToList();
            var top = l.Count is { } n ? ordered.Take((int)Math.Min(n, int.MaxValue)).ToList() : ordered;

            foreach (var (key, groupRows, agg) in top)
            {
                values[level * 2] = key;
                values[level * 2 + 1] = agg;
                Recurse(groupRows, level + 1);
            }

            if (l.HasOthers && top.Count < ordered.Count)
            {
                var rest = ordered.Skip(top.Count).SelectMany(g => g.Rows).ToList();
                values[level * 2] = l.Others;
                values[level * 2 + 1] = l.Aggregate(rest);
                for (var i = (level + 1) * 2; i < values.Length; i++) values[i] = null; // deeper levels aren't broken down
                results.Add(make((object?[])values.Clone()));
            }
        }
    }
}
