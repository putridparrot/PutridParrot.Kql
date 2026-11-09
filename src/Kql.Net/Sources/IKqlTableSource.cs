using System.Collections;
using System.Data;
using Kql.Net.Compilation;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;

namespace Kql.Net;

/// <summary>
/// A table that KQL queries can read. The element type of <see cref="GetQuery"/> defines the columns:
/// each public readable property/field becomes a column. Return a provider-backed <see cref="IQueryable"/>
/// (e.g. an EF Core <c>DbSet</c>, a Redis.OM collection) to have operators translated by that provider.
/// </summary>
public interface IKqlTableSource
{
    /// <summary>The CLR type of each row.</summary>
    Type ElementType { get; }

    /// <summary>Returns a fresh query over the table's rows.</summary>
    IQueryable GetQuery();
}

/// <summary>A source over any <see cref="IQueryable{T}"/> or <see cref="IEnumerable{T}"/>.</summary>
public sealed class QueryableTableSource : IKqlTableSource
{
    private readonly Func<IQueryable> _factory;

    public QueryableTableSource(IQueryable query)
    {
        ElementType = query.ElementType;
        _factory = () => query;
    }

    public QueryableTableSource(IEnumerable rows, Type elementType)
    {
        ElementType = elementType;
        _factory = () => ExpressionHelpers.ToClientQuery(rows, elementType);
    }

    public Type ElementType { get; }

    public IQueryable GetQuery() => _factory();
}

/// <summary>
/// A source of loosely-typed rows (DataTable, dictionaries / ExpandoObject, JSON). Rows are converted into
/// runtime-emitted row types so they can be queried like any other table.
/// </summary>
public sealed class RowSetTableSource : IKqlTableSource
{
    private readonly Func<IEnumerable<object?[]>> _rows;
    private readonly Func<object?[], object> _create;

    /// <param name="columns">Column names and their CLR types (use <c>object</c> for dynamic columns).</param>
    /// <param name="rows">Row values, in column order.</param>
    public RowSetTableSource(IReadOnlyList<(string Name, Type Type)> columns, Func<IEnumerable<object?[]>> rows)
    {
        var kqlColumns = columns.Select(c => (c.Name, c.Type == typeof(object) ? ScalarTypes.Dynamic : KqlTypes.FromClr(c.Type).KqlType)).ToArray();
        var schema = RowSchema.Emit(kqlColumns);
        ElementType = schema.RowType;
        _rows = rows;
        var ctor = schema.Constructor!;
        var types = schema.Columns.Select(c => c.ClrType).ToArray();
        _create = values =>
        {
            var args = new object?[types.Length];
            for (var i = 0; i < types.Length; i++)
            {
                var v = i < values.Length ? values[i] : null;
                if (v is DBNull) v = null;
                args[i] = types[i] == typeof(object) ? Dynamic.Normalize(v) : KqlConvert.ChangeType(v, types[i]);
            }
            return ctor.Invoke(args);
        };
    }

    public Type ElementType { get; }

    public IQueryable GetQuery()
    {
        var typed = (IEnumerable)typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(ElementType)
            .Invoke(null, [_rows().Select(_create)])!;
        return ExpressionHelpers.ToClientQuery(typed, ElementType);
    }

    /// <summary>Creates a source over a <see cref="DataTable"/>.</summary>
    public static RowSetTableSource FromDataTable(DataTable table) =>
        new(table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, KqlTypes.IsScalarClrType(c.DataType) ? c.DataType : typeof(object))).ToArray(),
            () => table.Rows.Cast<DataRow>().Select(r => r.ItemArray));

    /// <summary>
    /// Creates a source over property bags (<c>IDictionary&lt;string, object?&gt;</c>, e.g. ExpandoObject).
    /// The schema is inferred from the rows unless <paramref name="columns"/> is supplied; keys with mixed
    /// value types become <c>dynamic</c>.
    /// </summary>
    public static RowSetTableSource FromDictionaries(IEnumerable<IDictionary<string, object?>> rows, IReadOnlyList<(string Name, Type Type)>? columns = null)
    {
        columns ??= InferColumns(rows);
        var names = columns.Select(c => c.Name).ToArray();
        return new RowSetTableSource(columns, () => rows.Select(r => names.Select(n => r.TryGetValue(n, out var v) ? v : null).ToArray()));
    }

    /// <summary>Creates a source from a JSON array of objects.</summary>
    public static RowSetTableSource FromJson(string json)
    {
        var parsed = Dynamic.Parse(json);
        var rows = parsed switch
        {
            List<object?> list => list.OfType<Dictionary<string, object?>>().Cast<IDictionary<string, object?>>().ToList(),
            Dictionary<string, object?> one => [one],
            _ => throw new KqlException("JSON table data must be an array of objects.")
        };
        return FromDictionaries(rows);
    }

    private static IReadOnlyList<(string Name, Type Type)> InferColumns(IEnumerable<IDictionary<string, object?>> rows)
    {
        var order = new List<string>();
        var types = new Dictionary<string, Type?>(StringComparer.Ordinal);
        foreach (var row in rows.Take(10_000))
        {
            foreach (var (key, value) in row)
            {
                if (!types.ContainsKey(key))
                {
                    order.Add(key);
                    types[key] = null;
                }
                if (value is null) continue;
                var t = KqlTypes.IsScalarClrType(value.GetType()) ? KqlTypes.ClrTypeFor(KqlTypes.FromClr(value.GetType()).KqlType) : typeof(object);
                var existing = types[key];
                types[key] = existing is null || existing == t ? t
                    : KqlTypes.IsNumeric(existing) && KqlTypes.IsNumeric(t) ? KqlTypes.WidestNumeric(existing, t)
                    : typeof(object);
            }
        }
        return order.Select(k => (k, types[k] ?? typeof(object))).ToArray();
    }
}
