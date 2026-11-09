using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;
using Kql.Net.Schema;

namespace Kql.Net.Compilation;

internal static class ExpressionHelpers
{
    private static readonly MethodInfo ToGeneric = typeof(KqlConvert).GetMethod(nameof(KqlConvert.To))!;
    private static readonly MethodInfo ToStringValue = typeof(KqlConvert).GetMethod(nameof(KqlConvert.ToStringValue))!;

    /// <summary>Converts an expression to the target runtime type using KQL conversion rules.</summary>
    public static Expression Coerce(Expression e, Type target)
    {
        if (e.Type == target) return e;
        if (target == typeof(object)) return Expression.Convert(e, typeof(object));

        // null literal
        if (e is ConstantExpression { Value: null }) return Expression.Constant(null, KqlTypes.MakeNullable(target));

        if (e.Type == typeof(object)) return Expression.Call(ToGeneric.MakeGenericMethod(target), e);

        var src = Nullable.GetUnderlyingType(e.Type) ?? e.Type;
        var dst = Nullable.GetUnderlyingType(target) ?? target;

        if (src == dst) return Expression.Convert(e, target);
        if (KqlTypes.IsNumeric(e.Type) && KqlTypes.IsNumeric(target)) return Expression.Convert(e, target);
        if (target == typeof(string)) return Expression.Call(ToStringValue, Expression.Convert(e, typeof(object)));

        // Fall back to runtime conversion (e.g. string -> long)
        return Expression.Call(ToGeneric.MakeGenericMethod(target), Expression.Convert(e, typeof(object)));
    }

    /// <summary>Turns a KQL boolean (bool?) into a CLR predicate value (null => false).</summary>
    public static Expression ToPredicate(Expression e)
    {
        if (e.Type == typeof(bool)) return e;
        if (e.Type == typeof(bool?)) return Expression.Equal(e, Expression.Constant(true, typeof(bool?)));
        return Expression.Equal(Coerce(e, typeof(bool?)), Expression.Constant(true, typeof(bool?)));
    }

    public static Expression IsNull(Expression e) =>
        e.Type.IsValueType && Nullable.GetUnderlyingType(e.Type) is null
            ? Expression.Constant(false)
            : Expression.Equal(e, Expression.Constant(null, e.Type));

    /// <summary>Calls a static <see cref="Queryable"/> method on a query.</summary>
    public static IQueryable CallQueryable(IQueryable source, string method, Type[] typeArgs, params Expression[] args)
    {
        var mi = FindQueryable(method, typeArgs.Length, args.Length + 1, args.Select(a => a.Type).ToArray());
        var call = Expression.Call(null, mi.MakeGenericMethod(typeArgs), [source.Expression, .. args]);
        return source.Provider.CreateQuery(call);
    }

    /// <summary>Executes a scalar-returning <see cref="Queryable"/> method (Count, Any...).</summary>
    public static object? ExecuteQueryable(IQueryable source, string method, Type[] typeArgs, params Expression[] args)
    {
        var mi = FindQueryable(method, typeArgs.Length, args.Length + 1, args.Select(a => a.Type).ToArray());
        var call = Expression.Call(null, mi.MakeGenericMethod(typeArgs), [source.Expression, .. args]);
        return source.Provider.Execute(call);
    }

    private static MethodInfo FindQueryable(string name, int genericArgs, int paramCount, Type[] argTypes)
    {
        var candidates = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name && m.GetGenericArguments().Length == genericArgs && m.GetParameters().Length == paramCount)
            .ToList();
        if (candidates.Count == 1) return candidates[0];

        // Disambiguate: lambda args are quoted Expression<Func<...>>; prefer overloads whose
        // parameter at that position is an Expression<Func<T, X>> with matching arity; else the comparer overloads.
        foreach (var c in candidates)
        {
            var ps = c.GetParameters();
            var ok = true;
            for (var i = 0; i < argTypes.Length && ok; i++)
            {
                var pt = ps[i + 1].ParameterType;
                var at = argTypes[i];
                if (at.IsGenericType && at.GetGenericTypeDefinition() == typeof(Expression<>))
                {
                    var funcArity = at.GetGenericArguments()[0].GetGenericArguments().Length;
                    ok = pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Expression<>)
                         && pt.GetGenericArguments()[0].GetGenericArguments().Length == funcArity;
                }
                else if (at == typeof(int))
                {
                    ok = pt == typeof(int);
                }
                else
                {
                    ok = !(pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Expression<>)) && pt != typeof(int);
                }
            }
            if (ok) return c;
        }
        throw new InvalidOperationException($"No Queryable.{name} overload matched.");
    }

    /// <summary>Creates an in-memory query (EnumerableQuery) over a sequence, forcing client-side evaluation.</summary>
    public static IQueryable ToClientQuery(IEnumerable source, Type elementType)
    {
        // A provider-backed IQueryable must be hidden behind a plain iterator, otherwise EnumerableQuery would
        // compose further operators back onto the provider's query instead of running them in memory.
        if (source is IQueryable q && !IsInMemory(q))
            source = (IEnumerable)typeof(ExpressionHelpers).GetMethod(nameof(Iterate), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(elementType).Invoke(null, [source])!;
        return (IQueryable)Activator.CreateInstance(typeof(EnumerableQuery<>).MakeGenericType(elementType), source)!;
    }

    private static IEnumerable<T> Iterate<T>(IEnumerable<T> source)
    {
        foreach (var item in source) yield return item;
    }

    public static bool IsInMemory(IQueryable q) => q.Provider is EnumerableQuery;
}
