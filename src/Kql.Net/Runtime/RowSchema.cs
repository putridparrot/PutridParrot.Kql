using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Schema;
using Kusto.Language.Symbols;

namespace Kql.Net.Runtime;

/// <summary>A column of a row type: its KQL name/type and how to read it from a row expression.</summary>
internal sealed class RowColumn
{
    public RowColumn(string name, ScalarSymbol kqlType, Func<Expression, Expression> read, bool clientOnly = false)
    {
        Name = name;
        KqlType = kqlType;
        Read = read;
        ClientOnly = clientOnly;
        ClrType = KqlTypes.ClrTypeFor(kqlType);
    }

    public string Name { get; }
    public ScalarSymbol KqlType { get; }

    /// <summary>The runtime (normalised, nullable) CLR type of the column's values.</summary>
    public Type ClrType { get; }

    /// <summary>Builds an expression reading this column from a row expression; the result has type <see cref="ClrType"/>.</summary>
    public Func<Expression, Expression> Read { get; }

    /// <summary>True when reading the column needs in-memory evaluation (e.g. enum to string conversion).</summary>
    public bool ClientOnly { get; }
}

/// <summary>
/// Describes the CLR shape of rows flowing through a query stage.
/// </summary>
internal sealed class RowSchema
{
    private readonly Dictionary<string, RowColumn> _byName;

    public RowSchema(Type rowType, IReadOnlyList<RowColumn> columns, ConstructorInfo? constructor = null)
    {
        RowType = rowType;
        Columns = columns;
        Constructor = constructor;
        _byName = new Dictionary<string, RowColumn>(StringComparer.Ordinal);
        foreach (var c in columns) _byName.TryAdd(c.Name, c);
    }

    public Type RowType { get; }
    public IReadOnlyList<RowColumn> Columns { get; }

    /// <summary>Set for emitted row types: constructor taking all column values in order.</summary>
    public ConstructorInfo? Constructor { get; }

    public RowColumn? Find(string name) => _byName.GetValueOrDefault(name);

    public RowColumn Get(string name) =>
        Find(name) ?? throw new KqlException($"Column '{name}' not found. Available: {string.Join(", ", Columns.Select(c => c.Name))}");

    public TableSymbol ToTableSymbol(string name) =>
        new(name, Columns.Select(c => new ColumnSymbol(c.Name, c.KqlType)));

    /// <summary>Creates (or reuses) an emitted row type for the given KQL columns.</summary>
    public static RowSchema Emit(IReadOnlyList<(string Name, ScalarSymbol Type)> columns)
    {
        var rowType = RowTypeFactory.GetRowType(columns.Select(c => (c.Name, KqlTypes.ClrTypeFor(c.Type))).ToArray());
        var props = RowTypeFactory.GetProperties(rowType);
        var cols = columns.Select((c, i) =>
        {
            var prop = props[i];
            return new RowColumn(c.Name, c.Type, row => Expression.Property(row, prop));
        }).ToArray();
        return new RowSchema(rowType, cols, rowType.GetConstructors().Single());
    }

    public static RowSchema Emit(TableSymbol table) =>
        Emit(table.Columns.Select(c => (c.Name, AsScalar(c.Type))).ToArray());

    internal static ScalarSymbol AsScalar(TypeSymbol type) => type as ScalarSymbol ?? ScalarTypes.Dynamic;

    /// <summary>Builds a <c>new Row(v0, v1, ...)</c> expression with member bindings (anonymous-type shape).</summary>
    public NewExpression New(IReadOnlyList<Expression> values)
    {
        if (Constructor is null) throw new InvalidOperationException("Row type has no constructor.");
        var props = RowTypeFactory.GetProperties(RowType);
        var args = values.Select((v, i) => Compilation.ExpressionHelpers.Coerce(v, Columns[i].ClrType)).ToArray();
        return Expression.New(Constructor, args, props);
    }

    /// <summary>Schema for an arbitrary CLR type: one column per public readable property / field.</summary>
    public static RowSchema FromClrType(Type type)
    {
        if (RowTypeFactory.TryGetColumnNames(type, out var emittedNames))
        {
            var props = RowTypeFactory.GetProperties(type);
            return Emit(emittedNames.Select((n, i) => (n, KqlTypes.FromClr(props[i].PropertyType).KqlType)).ToArray());
        }

        if (KqlTypes.IsScalarClrType(type))
        {
            var (kql, adapt, clientOnly) = KqlTypes.FromClr(type);
            return new RowSchema(type, [new RowColumn("value", kql, adapt, clientOnly)]);
        }

        var cols = new List<RowColumn>();
        var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            .OrderBy(m => m.MetadataToken);
        foreach (var m in members)
        {
            var memberType = m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
            var (kql, adapt, clientOnly) = KqlTypes.FromClr(memberType);
            var member = m;
            cols.Add(new RowColumn(m.Name, kql, row => adapt(Expression.MakeMemberAccess(row, member)), clientOnly));
        }
        return new RowSchema(type, cols);
    }
}
