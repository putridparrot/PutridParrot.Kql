using System.Collections;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using PutridParrot.Kql.Runtime;

namespace PutridParrot.Kql;

/// <summary>A result column: its name, KQL type name (e.g. <c>long</c>) and CLR value type.</summary>
public sealed record KqlColumn(string Name, string KqlType, Type ClrType);

/// <summary>
/// The tabular result of a KQL query. Values are plain CLR values: <c>long</c>, <c>int</c>, <c>double</c>,
/// <c>decimal</c>, <c>bool</c>, <c>string</c>, <c>DateTime</c>, <c>TimeSpan</c>, <c>Guid</c>, or for
/// <c>dynamic</c> columns a CLR graph (<c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c>, scalars) — or null.
/// </summary>
public sealed class KqlResult : IReadOnlyList<IReadOnlyList<object?>>
{
    private readonly Dictionary<string, int> _ordinals;

    public KqlResult(IReadOnlyList<KqlColumn> columns, IReadOnlyList<object?[]> rows)
    {
        Columns = columns;
        Rows = rows;
        _ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++) _ordinals.TryAdd(columns[i].Name, i);
    }

    public IReadOnlyList<KqlColumn> Columns { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    public int Count => Rows.Count;

    public IReadOnlyList<object?> this[int index] => Rows[index];

    public object? this[int row, string column] => Rows[row][Ordinal(column)];

    public int Ordinal(string column) =>
        _ordinals.TryGetValue(column, out var i) ? i : throw new KeyNotFoundException($"Column '{column}' is not in the result.");

    /// <summary>All values of one column.</summary>
    public IEnumerable<object?> Column(string column)
    {
        var i = Ordinal(column);
        return Rows.Select(r => r[i]);
    }

    /// <summary>The first column of the first row (or null).</summary>
    public object? Scalar() => Rows.Count > 0 && Columns.Count > 0 ? Rows[0][0] : null;

    public IEnumerator<IReadOnlyList<object?>> GetEnumerator() => Rows.Cast<IReadOnlyList<object?>>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Each row as a name -> value dictionary.</summary>
    public IEnumerable<IReadOnlyDictionary<string, object?>> AsDictionaries() =>
        Rows.Select(r => (IReadOnlyDictionary<string, object?>)Columns.Select((c, i) => (c.Name, r[i])).ToDictionary(x => x.Name, x => x.Item2));

    /// <summary>
    /// Maps rows onto <typeparamref name="T"/>. For scalar types, the first column is used; otherwise columns are
    /// matched to public settable properties/fields by name (case-insensitive), or to constructor parameters (records).
    /// </summary>
    public IEnumerable<T> As<T>()
    {
        var t = typeof(T);
        if (Schema.KqlTypes.IsScalarClrType(t) || t == typeof(object))
            return Rows.Select(r => (T)Convert(r.Length > 0 ? r[0] : null, t)!);
        var map = Mapper(t);
        return Rows.Select(r => (T)map(r));
    }

    private static readonly MethodInfo ConvertMethod = typeof(KqlResult).GetMethod(nameof(Convert), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Builds (once per <see cref="As{T}"/> call) a compiled delegate that maps a row onto <paramref name="type"/>:
    /// through the constructor with the most parameters when there is no parameterless one (records), otherwise by
    /// setting public properties/fields whose names match columns.
    /// </summary>
    private Func<object?[], object> Mapper(Type type)
    {
        var row = Expression.Parameter(typeof(object?[]), "row");
        Expression Value(int ordinal, Type target) => Expression.Convert(
            Expression.Call(ConvertMethod, Expression.ArrayIndex(row, Expression.Constant(ordinal)), Expression.Constant(target, typeof(Type))), target);

        Expression body;
        var ctor = type.GetConstructor(Type.EmptyTypes);
        if (ctor is null && !type.IsValueType)
        {
            // records / immutable types: bind constructor parameters by name
            var best = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            body = Expression.New(best, best.GetParameters().Select(p =>
                _ordinals.TryGetValue(p.Name!, out var i) || TryOrdinalIgnoreCase(p.Name!, out i)
                    ? Value(i, p.ParameterType)
                    : (Expression)Expression.Default(p.ParameterType)));
        }
        else
        {
            var bindings = new List<MemberBinding>();
            var bound = new HashSet<MemberInfo>();
            for (var i = 0; i < Columns.Count; i++)
            {
                var name = Columns[i].Name;
                MemberInfo? member = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is { CanWrite: true } prop
                    ? prop
                    : type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is { IsInitOnly: false } field ? field : null;
                if (member is null || !bound.Add(member)) continue;
                var memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;
                bindings.Add(Expression.Bind(member, Value(i, memberType)));
            }
            body = Expression.MemberInit(ctor is null ? Expression.New(type) : Expression.New(ctor), bindings);
        }
        return Expression.Lambda<Func<object?[], object>>(Expression.Convert(body, typeof(object)), row).Compile();
    }

    private bool TryOrdinalIgnoreCase(string name, out int ordinal)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    private static object? Convert(object? value, Type target)
    {
        if (value is null) return DefaultOf(target);
        if (target.IsInstanceOfType(value)) return value;
        var core = Nullable.GetUnderlyingType(target) ?? target;
        if (core.IsEnum) return value is string s ? Enum.Parse(core, s, true) : Enum.ToObject(core, value);
        if (core == typeof(DateTimeOffset) && value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        if (core == typeof(string)) return KqlConvert.ToStringValue(value);
        if (Schema.KqlTypes.IsScalarClrType(core))
        {
            var converted = KqlConvert.ChangeType(value, core);
            return converted is null ? DefaultOf(target) : System.Convert.ChangeType(converted, core, System.Globalization.CultureInfo.InvariantCulture);
        }
        // complex target from a dynamic value: round-trip through JSON
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(value), target);
    }

    /// <summary>Converts the result to a <see cref="DataTable"/>.</summary>
    public DataTable ToDataTable(string? tableName = null)
    {
        var table = new DataTable(tableName ?? "Result");
        foreach (var c in Columns) table.Columns.Add(c.Name, c.ClrType == typeof(object) ? typeof(object) : c.ClrType);
        foreach (var r in Rows) table.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
        return table;
    }

    /// <summary>Serialises the rows as a JSON array of objects.</summary>
    public string ToJson(JsonSerializerOptions? options = null) => JsonSerializer.Serialize(AsDictionaries(), options);

    /// <summary>A simple fixed-width text rendering, handy for consoles and test output.</summary>
    public override string ToString()
    {
        var cells = new List<string[]> { Columns.Select(c => c.Name).ToArray() };
        cells.AddRange(Rows.Select(r => r.Select(v => v is null ? "" : v is IDictionary or IList ? KqlConvert.ToJson(v) : KqlConvert.ToStringValue(v) ?? "").ToArray()));
        var widths = Enumerable.Range(0, Columns.Count).Select(i => cells.Max(r => r[i].Length)).ToArray();
        var lines = cells.Select(r => string.Join(" | ", r.Select((v, i) => v.PadRight(widths[i])))).ToList();
        lines.Insert(1, string.Join("-+-", widths.Select(w => new string('-', w))));
        return string.Join(Environment.NewLine, lines);
    }
}
