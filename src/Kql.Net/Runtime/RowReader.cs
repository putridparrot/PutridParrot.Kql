using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Kql.Net.Runtime;

/// <summary>Compiles (and caches) a delegate that reads all column values of a row into an object array.</summary>
internal static class RowReader
{
    private static readonly ConditionalWeakTable<RowSchema, Func<object, object?[]>> Cache = new();

    public static Func<object, object?[]> For(RowSchema schema) => Cache.GetValue(schema, Build);

    private static Func<object, object?[]> Build(RowSchema schema)
    {
        var p = Expression.Parameter(typeof(object), "o");
        var row = Expression.Convert(p, schema.RowType);
        var values = schema.Columns.Select(c => (Expression)Expression.Convert(c.Read(row), typeof(object)));
        return Expression.Lambda<Func<object, object?[]>>(Expression.NewArrayInit(typeof(object), values), p).Compile();
    }
}

/// <summary>
/// Compiles (and caches) a delegate that creates a row of an emitted type from its column values. The values must
/// already have the column types (or be null); this replaces <c>ConstructorInfo.Invoke</c> on per-row paths.
/// </summary>
internal static class RowFactory
{
    private static readonly ConcurrentDictionary<Type, Func<object?[], object>> Cache = new();

    public static Func<object?[], object> For(RowSchema schema) => For(schema.RowType);

    public static Func<object?[], object> For(Type rowType) => Cache.GetOrAdd(rowType, Build);

    private static Func<object?[], object> Build(Type rowType)
    {
        var ctor = rowType.GetConstructors().Single();
        var p = Expression.Parameter(typeof(object?[]), "values");
        var args = ctor.GetParameters().Select((x, i) =>
            (Expression)Expression.Convert(Expression.ArrayIndex(p, Expression.Constant(i)), x.ParameterType));
        return Expression.Lambda<Func<object?[], object>>(Expression.Convert(Expression.New(ctor, args), typeof(object)), p).Compile();
    }
}
