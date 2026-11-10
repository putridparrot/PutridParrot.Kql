using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace Kql.Net.Runtime;

/// <summary>
/// Base class for row types emitted at runtime. Provides value equality so that in-memory
/// <c>Distinct</c>/<c>GroupBy</c>/<c>Join</c> behave like their SQL counterparts. Emitted types override
/// <see cref="EqualsCore"/>, <see cref="GetHashCode"/> and <see cref="HasNullValue"/> with typed, allocation-free code.
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

    public bool Equals(KqlRow? other) =>
        other is not null && (ReferenceEquals(this, other) || (other.GetType() == GetType() && EqualsCore(other)));

    /// <summary>Compares the column values with another row of the same type.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected virtual bool EqualsCore(KqlRow other)
    {
        var a = GetValues();
        var b = other.GetValues();
        for (var i = 0; i < a.Length; i++)
        {
            if (!KqlComparer.ValueEquals(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>True when any column value is null.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public virtual bool HasNullValue() => GetValues().Any(v => v is null);

    public override bool Equals(object? obj) => obj is KqlRow r && Equals(r);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var v in GetValues()) hash = hash * 31 + KqlComparer.ValueHash(v);
        return hash;
    }

    public override string ToString() =>
        "{" + string.Join(", ", GetType().GetProperties().OrderBy(x => x.MetadataToken).Select(p => $"{p.Name}={p.GetValue(this)}")) + "}";
}
