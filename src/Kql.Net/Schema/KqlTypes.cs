using System.Linq.Expressions;
using Kusto.Language.Symbols;

namespace Kql.Net.Schema;

/// <summary>
/// Maps between KQL scalar types and the CLR types used at runtime.
/// Every KQL scalar is nullable, so value types are always represented as <see cref="Nullable{T}"/>.
/// <c>dynamic</c> is represented as <see cref="object"/> (a plain CLR object graph).
/// </summary>
public static class KqlTypes
{
    /// <summary>The runtime CLR type for a KQL type.</summary>
    public static Type ClrTypeFor(TypeSymbol type)
    {
        if (type == ScalarTypes.Bool) return typeof(bool?);
        if (type == ScalarTypes.Int) return typeof(int?);
        if (type == ScalarTypes.Long) return typeof(long?);
        if (type == ScalarTypes.Real) return typeof(double?);
        if (type == ScalarTypes.Decimal) return typeof(decimal?);
        if (type == ScalarTypes.DateTime) return typeof(DateTime?);
        if (type == ScalarTypes.TimeSpan) return typeof(TimeSpan?);
        if (type == ScalarTypes.Guid) return typeof(Guid?);
        if (type == ScalarTypes.String) return typeof(string);
        return typeof(object);
    }

    /// <summary>
    /// Maps a CLR member type to a KQL type, plus an optional adapter that converts a raw member
    /// access into the runtime representation (e.g. <c>int</c> to <c>int?</c>, enum to string).
    /// </summary>
    public static (ScalarSymbol KqlType, Func<Expression, Expression> Adapt, bool ClientOnly) FromClr(Type clr)
    {
        var underlying = Nullable.GetUnderlyingType(clr);
        var core = underlying ?? clr;

        if (core.IsEnum)
        {
            // enum.ToString() is translated by EF Core (to a CASE over the enum's values)
            var toString = typeof(object).GetMethod(nameof(ToString))!;
            return (ScalarTypes.String, e => underlying is null
                ? Expression.Call(e, toString)
                : Expression.Condition(
                    Expression.Property(e, "HasValue"),
                    Expression.Call(Expression.Property(e, "Value"), toString),
                    Expression.Constant(null, typeof(string))), false);
        }

        if (core == typeof(DateTimeOffset))
        {
            return (ScalarTypes.DateTime, e => underlying is null
                ? Expression.Convert(Expression.Property(e, nameof(DateTimeOffset.UtcDateTime)), typeof(DateTime?))
                : Expression.Condition(
                    Expression.Property(e, "HasValue"),
                    Expression.Convert(Expression.Property(Expression.Property(e, "Value"), nameof(DateTimeOffset.UtcDateTime)), typeof(DateTime?)),
                    Expression.Constant(null, typeof(DateTime?))), true);
        }

        if (core == typeof(char))
        {
            return (ScalarTypes.String, e => Expression.Call(
                typeof(Runtime.KqlConvert).GetMethod(nameof(Runtime.KqlConvert.ToStringValue))!,
                Expression.Convert(e, typeof(object))), true);
        }

        ScalarSymbol? kql = core switch
        {
            _ when core == typeof(bool) => ScalarTypes.Bool,
            _ when core == typeof(int) || core == typeof(short) || core == typeof(byte) || core == typeof(sbyte) || core == typeof(ushort) => ScalarTypes.Int,
            _ when core == typeof(long) || core == typeof(uint) || core == typeof(ulong) => ScalarTypes.Long,
            _ when core == typeof(double) || core == typeof(float) => ScalarTypes.Real,
            _ when core == typeof(decimal) => ScalarTypes.Decimal,
            _ when core == typeof(DateTime) => ScalarTypes.DateTime,
            _ when core == typeof(TimeSpan) => ScalarTypes.TimeSpan,
            _ when core == typeof(Guid) => ScalarTypes.Guid,
            _ when core == typeof(string) => ScalarTypes.String,
            _ => null
        };

        if (kql is null)
            return (ScalarTypes.Dynamic, e => e.Type == typeof(object) ? e : Expression.Convert(e, typeof(object)), true);

        var target = ClrTypeFor(kql);
        return (kql, e => e.Type == target ? e : Expression.Convert(e, target), false);
    }

    /// <summary>True when the CLR type maps onto a KQL scalar (as opposed to being a row/object type).</summary>
    public static bool IsScalarClrType(Type clr)
    {
        var core = Nullable.GetUnderlyingType(clr) ?? clr;
        return core.IsPrimitive || core.IsEnum || core == typeof(string) || core == typeof(decimal)
               || core == typeof(DateTime) || core == typeof(DateTimeOffset) || core == typeof(TimeSpan) || core == typeof(Guid);
    }

    internal static bool IsNumeric(Type t)
    {
        var c = Nullable.GetUnderlyingType(t) ?? t;
        return c == typeof(int) || c == typeof(long) || c == typeof(double) || c == typeof(decimal);
    }

    /// <summary>Widest of two numeric runtime types (int &lt; long &lt; decimal &lt; real).</summary>
    internal static Type WidestNumeric(Type a, Type b)
    {
        static int Rank(Type t)
        {
            var c = Nullable.GetUnderlyingType(t) ?? t;
            return c == typeof(int) ? 0 : c == typeof(long) ? 1 : c == typeof(decimal) ? 2 : 3;
        }
        return Rank(a) >= Rank(b) ? MakeNullable(a) : MakeNullable(b);
    }

    internal static Type MakeNullable(Type t) =>
        t.IsValueType && Nullable.GetUnderlyingType(t) is null ? typeof(Nullable<>).MakeGenericType(t) : t;
}
