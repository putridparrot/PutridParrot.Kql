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
