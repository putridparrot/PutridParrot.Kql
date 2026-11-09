using System.Collections;

namespace Kql.Net.Runtime;

/// <summary>Value comparison helpers (KQL string comparisons are ordinal).</summary>
public static class KqlComparer
{
    public static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count) return false;
            for (var i = 0; i < la.Count; i++) if (!ValueEquals(la[i], lb[i])) return false;
            return true;
        }
        if (a is IDictionary<string, object?> da && b is IDictionary<string, object?> db)
        {
            return da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var v) && ValueEquals(kv.Value, v));
        }
        return a.Equals(b);
    }

    public static int ValueHash(object? v) => v switch
    {
        null => 0,
        string s => StringComparer.Ordinal.GetHashCode(s),
        IList l => l.Cast<object?>().Aggregate(17, (h, x) => h * 31 + ValueHash(x)),
        IDictionary<string, object?> d => d.Count,
        _ => v.GetHashCode()
    };

    /// <summary>Ordering comparer for in-memory sorting (ordinal strings, nulls first).</summary>
    public static IComparer<T> For<T>() => typeof(T) == typeof(string)
        ? (IComparer<T>)(object)StringComparer.Ordinal
        : typeof(T) == typeof(object) ? (IComparer<T>)(object)ObjectComparer.Instance : Comparer<T>.Default;

    private sealed class ObjectComparer : IComparer<object?>
    {
        public static readonly ObjectComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
            if (x is string sx && y is string sy) return string.CompareOrdinal(sx, sy);
            if (x.GetType() == y.GetType() && x is IComparable cx) return cx.CompareTo(y);
            if (KqlConvert.ToDouble(x) is { } dx && KqlConvert.ToDouble(y) is { } dy) return dx.CompareTo(dy);
            return string.CompareOrdinal(KqlConvert.ToStringValue(x), KqlConvert.ToStringValue(y));
        }
    }
}
