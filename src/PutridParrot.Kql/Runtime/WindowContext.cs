namespace PutridParrot.Kql.Runtime;

/// <summary>
/// Runtime state for KQL window functions (<c>row_number</c>, <c>prev</c>, <c>next</c>, <c>row_cumsum</c>,
/// <c>row_rank_dense</c>, <c>row_rank_min</c>) over a serialized (ordered) row set. Rows are evaluated in
/// order; each call site keeps its own running state, identified by a site id assigned at compile time.
/// </summary>
public sealed class WindowContext<T>
{
    private readonly IReadOnlyList<T> _rows;
    private readonly Dictionary<int, object?> _state = new();

    private WindowContext(IReadOnlyList<T> rows) => _rows = rows;

    public int Index { get; private set; }

    public T Current => _rows[Index];

    public bool Has(int offset) => Index + offset >= 0 && Index + offset < _rows.Count;

    public T At(int offset) => Has(offset) ? _rows[Index + offset] : default!;

    public long? RowNumber(int site, long? start, bool restart)
    {
        var first = !_state.TryGetValue(site, out var previous);
        var value = first || restart ? start ?? 1 : (long)previous! + 1;
        _state[site] = value;
        return value;
    }

    public long? CumSumLong(int site, long? value, bool restart) => CumSum(site, value ?? 0, restart, (a, b) => a + b);
    public double? CumSumDouble(int site, double? value, bool restart) => CumSum(site, value ?? 0, restart, (a, b) => a + b);
    public decimal? CumSumDecimal(int site, decimal? value, bool restart) => CumSum(site, value ?? 0, restart, (a, b) => a + b);

    private TV CumSum<TV>(int site, TV value, bool restart, Func<TV, TV, TV> add)
    {
        var sum = !restart && _state.TryGetValue(site, out var previous) ? add((TV)previous!, value) : value;
        _state[site] = sum;
        return sum;
    }

    /// <summary>Dense rank: increments by one whenever the value differs from the previous row's.</summary>
    public long? RankDense(int site, object? value, bool restart)
    {
        var rank = !restart && _state.TryGetValue(site, out var s) && s is ValueTuple<long, object?> prev
            ? KqlComparer.ValueEquals(prev.Item2, value) ? prev.Item1 : prev.Item1 + 1
            : 1;
        _state[site] = ((long)rank, value);
        return rank;
    }

    /// <summary>Min rank: ties share the rank of their first row; otherwise the rank is the row's position.</summary>
    public long? RankMin(int site, object? value, bool restart)
    {
        // state: (rank, value, position-within-partition)
        long rank, position;
        if (!restart && _state.TryGetValue(site, out var s) && s is ValueTuple<long, object?, long> prev)
        {
            position = prev.Item3 + 1;
            rank = KqlComparer.ValueEquals(prev.Item2, value) ? prev.Item1 : position;
        }
        else
        {
            position = 1;
            rank = 1;
        }
        _state[site] = (rank, value, position);
        return rank;
    }

    public static List<TOut> Select<TOut>(IEnumerable<T> rows, Func<WindowContext<T>, TOut> selector)
    {
        var ctx = new WindowContext<T>(rows as IReadOnlyList<T> ?? rows.ToList());
        var result = new List<TOut>(ctx._rows.Count);
        for (ctx.Index = 0; ctx.Index < ctx._rows.Count; ctx.Index++) result.Add(selector(ctx));
        return result;
    }

    public static List<T> Where(IEnumerable<T> rows, Func<WindowContext<T>, bool> predicate)
    {
        var ctx = new WindowContext<T>(rows as IReadOnlyList<T> ?? rows.ToList());
        var result = new List<T>();
        for (ctx.Index = 0; ctx.Index < ctx._rows.Count; ctx.Index++)
            if (predicate(ctx)) result.Add(ctx.Current);
        return result;
    }
}
