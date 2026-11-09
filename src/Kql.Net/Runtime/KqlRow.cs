using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Kql.Net.Runtime;

/// <summary>
/// Base class for row types emitted at runtime. Provides value equality so that in-memory
/// <c>Distinct</c>/<c>GroupBy</c>/<c>Join</c> behave like their SQL counterparts.
/// </summary>
public abstract class KqlRow : IEquatable<KqlRow>
{
    private static readonly ConcurrentDictionary<Type, Func<KqlRow, object?[]>> ValueReaders = new();

    /// <summary>The values of this row, in column order.</summary>
    public object?[] GetValues() => ValueReaders.GetOrAdd(GetType(), BuildReader)(this);

    private static Func<KqlRow, object?[]> BuildReader(Type type)
    {
        var p = Expression.Parameter(typeof(KqlRow), "r");
        var typed = Expression.Convert(p, type);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(x => x.MetadataToken);
        var values = props.Select(x => (Expression)Expression.Convert(Expression.Property(typed, x), typeof(object)));
        return Expression.Lambda<Func<KqlRow, object?[]>>(Expression.NewArrayInit(typeof(object), values), p).Compile();
    }

    public bool Equals(KqlRow? other)
    {
        if (other is null || other.GetType() != GetType()) return false;
        if (ReferenceEquals(this, other)) return true;
        var a = GetValues();
        var b = other.GetValues();
        for (var i = 0; i < a.Length; i++)
        {
            if (!KqlComparer.ValueEquals(a[i], b[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is KqlRow r && Equals(r);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in GetValues()) hash.Add(KqlComparer.ValueHash(v));
        return hash.ToHashCode();
    }

    public override string ToString() =>
        "{" + string.Join(", ", GetType().GetProperties().OrderBy(x => x.MetadataToken).Select(p => $"{p.Name}={p.GetValue(this)}")) + "}";
}
