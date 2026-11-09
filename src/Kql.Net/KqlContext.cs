using System.Collections;
using System.Data;
using Kql.Net.Compilation;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace Kql.Net;

/// <summary>
/// Entry point: register tables (in-memory collections, <see cref="IQueryable{T}"/>s such as EF Core DbSets,
/// DataTables, dictionaries, JSON) and run KQL queries over them.
/// </summary>
/// <example>
/// <code>
/// var kql = new KqlContext()
///     .AddTable("Orders", orders)
///     .AddTable("Customers", db.Customers);
/// var result = kql.Execute("Orders | where Amount > 100 | summarize Total = sum(Amount) by Region");
/// </code>
/// </example>
public sealed class KqlContext
{
    private readonly Dictionary<string, TableBinding> _tables = new(StringComparer.Ordinal);
    private GlobalState? _globals;

    /// <summary>The database name used for the KQL schema (rarely matters).</summary>
    public string DatabaseName { get; init; } = "db";

    public IReadOnlyCollection<string> TableNames => _tables.Keys;

    /// <summary>
    /// Registers a table over a collection or query. Each public property/field of <typeparamref name="T"/> becomes
    /// a column; collections of scalars (e.g. <c>int[]</c>) become a single column named <c>value</c>;
    /// dictionaries become <c>Key</c>/<c>Value</c> columns; property bags (<c>IDictionary&lt;string, object?&gt;</c>,
    /// ExpandoObject) have their schema inferred.
    /// </summary>
    public KqlContext AddTable<T>(string name, IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (typeof(IDictionary<string, object?>).IsAssignableFrom(typeof(T)))
            return AddTable(name, RowSetTableSource.FromDictionaries(rows.Cast<IDictionary<string, object?>>()));
        return rows is IQueryable<T> q
            ? AddTable(name, new QueryableTableSource(q))
            : AddTable(name, new QueryableTableSource(rows, typeof(T)));
    }

    /// <summary>Registers a query (e.g. an EF Core DbSet); operators are translated by its provider where possible.</summary>
    public KqlContext AddTable<T>(string name, IQueryable<T> rows) => AddTable(name, new QueryableTableSource(rows));

    /// <summary>Registers a non-generic <see cref="IQueryable"/>.</summary>
    public KqlContext AddTable(string name, IQueryable rows) => AddTable(name, new QueryableTableSource(rows));

    /// <summary>Registers a <see cref="DataTable"/>.</summary>
    public KqlContext AddTable(string name, DataTable table) => AddTable(name, RowSetTableSource.FromDataTable(table));

    /// <summary>Registers a table from a JSON array of objects.</summary>
    public KqlContext AddJsonTable(string name, string json) => AddTable(name, RowSetTableSource.FromJson(json));

    /// <summary>Registers a custom source.</summary>
    public KqlContext AddTable(string name, IKqlTableSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        _tables[name] = new TableBinding(name, source, RowSchema.FromClrType(source.ElementType));
        _globals = null;
        return this;
    }

    public KqlContext RemoveTable(string name)
    {
        if (_tables.Remove(name)) _globals = null;
        return this;
    }

    /// <summary>The Kusto.Language global state describing the registered tables (useful for editors / IntelliSense).</summary>
    public GlobalState Globals => _globals ??= GlobalState.Default.WithDatabase(
        new DatabaseSymbol(DatabaseName, _tables.Values.Select(t => (Symbol)t.Schema.ToTableSymbol(t.Name)).ToArray()));

    /// <summary>Parses and binds a query, returning any errors (empty when the query is valid).</summary>
    public IReadOnlyList<KqlDiagnostic> Validate(string query)
    {
        var code = KustoCode.ParseAndAnalyze(query, Globals);
        return code.GetDiagnostics()
            .Where(d => d.Severity == "Error")
            .Select(d => new KqlDiagnostic(d.Code, d.Severity, d.Message, d.Start, d.Length))
            .ToList();
    }

    /// <summary>Runs a query and returns its (last) result set.</summary>
    public KqlResult Execute(string query) => ExecuteAll(query)[^1];

    /// <summary>Runs a query and returns every result set (one per tabular expression statement).</summary>
    public IReadOnlyList<KqlResult> ExecuteAll(string query)
    {
        var plans = Compile(query);
        return plans.Select(Materialize).ToList();
    }

    /// <summary>Runs a query and maps the rows onto <typeparamref name="T"/> (see <see cref="KqlResult.As{T}"/>).</summary>
    public IReadOnlyList<T> Execute<T>(string query) => Execute(query).As<T>().ToList();

    /// <summary>
    /// Runs a query and returns the underlying (lazy) <see cref="IQueryable"/>. Its element type is a runtime-emitted
    /// row type unless the query is a pass-through of a registered table.
    /// </summary>
    public IQueryable AsQueryable(string query) => Compile(query)[^1].Execute();

    private IReadOnlyList<PlanNode> Compile(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var code = KustoCode.ParseAndAnalyze(query, Globals);
        var errors = code.GetDiagnostics()
            .Where(d => d.Severity == "Error")
            .Select(d => new KqlDiagnostic(d.Code, d.Severity, d.Message, d.Start, d.Length))
            .ToList();
        if (errors.Count > 0) throw new KqlException(errors);
        return new QueryCompiler(code, _tables).Compile();
    }

    private static KqlResult Materialize(PlanNode plan)
    {
        var columns = plan.Schema.Columns
            .Select(c => new KqlColumn(c.Name, c.KqlType.Name, Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType))
            .ToList();
        var reader = RowReader.For(plan.Schema);
        var rows = new List<object?[]>();
        foreach (var row in (IEnumerable)plan.Execute()) rows.Add(reader(row!));
        return new KqlResult(columns, rows);
    }
}
