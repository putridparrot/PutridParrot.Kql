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
    private readonly List<IKqlQueryTranslator> _translators = [];
    private GlobalState? _globals;
    private int _globalsFunctionVersion = -1;

    /// <summary>
    /// Scalar functions for this context: the built-ins plus anything registered here. Registering affects only
    /// this context.
    /// </summary>
    public FunctionRegistry Functions { get; } = new();

    /// <summary>Registers every <see cref="Runtime.KqlFunctionAttribute"/>-marked static method of a type as a KQL function.</summary>
    public KqlContext AddFunctions(Type type)
    {
        Functions.Register(type);
        return this;
    }

    /// <summary>Registers a delegate as a KQL function, e.g. <c>AddFunction("double_it", (long? x) =&gt; x * 2)</c>.</summary>
    public KqlContext AddFunction(string name, Delegate implementation)
    {
        Functions.Register(name, implementation);
        return this;
    }

    /// <summary>Registered query translators (see <see cref="AddTranslator"/>).</summary>
    public IReadOnlyList<IKqlQueryTranslator> Translators => _translators;

    /// <summary>
    /// Registers a translator that lets a LINQ provider (e.g. EF Core, via the Kql.Net.EntityFrameworkCore package)
    /// evaluate more KQL operators and functions natively. Without one, provider-backed tables still work, but
    /// the query moves to in-memory evaluation at the first operator the provider can't translate.
    /// </summary>
    public KqlContext AddTranslator(IKqlQueryTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        if (!_translators.Contains(translator)) _translators.Add(translator);
        return this;
    }

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

    /// <summary>
    /// Registers a CSV file. Column types are inferred from the data when the table is added; the file is read again
    /// on each query. For CSV text in memory, use <c>AddTable(name, CsvTableSource.FromText(csv))</c>.
    /// </summary>
    public KqlContext AddCsvTable(string name, string path, CsvOptions? options = null) =>
        AddTable(name, CsvTableSource.FromFile(path, options));

    /// <summary>
    /// Registers an asynchronous sequence. It's enumerated each time a query runs, so it should be re-enumerable
    /// (e.g. a method that opens a new stream on each enumeration).
    /// </summary>
    public KqlContext AddTable<T>(string name, IAsyncEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return AddTable(name, new QueryableTableSource(new AsyncEnumerableAdapter<T>(rows), typeof(T)));
    }

    private sealed class AsyncEnumerableAdapter<T>(IAsyncEnumerable<T> source) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => source.ToBlockingEnumerable().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
    public GlobalState Globals
    {
        get
        {
            if (_globals is not null && _globalsFunctionVersion == Functions.Version) return _globals;
            var globals = GlobalState.Default.WithDatabase(
                new DatabaseSymbol(DatabaseName, _tables.Values.Select(t => (Symbol)t.Schema.ToTableSymbol(t.Name)).ToArray()));
            var custom = Functions.CustomSymbols(globals);
            if (custom.Count > 0) globals = globals.WithFunctions(globals.Functions.Concat(custom).ToArray());
            _globalsFunctionVersion = Functions.Version;
            return _globals = globals;
        }
    }

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
        return new QueryCompiler(code, _tables, _translators, Functions).Compile();
    }

    private static List<KqlColumn> ResultColumns(PlanNode plan) => plan.Schema.Columns
        .Select(c => new KqlColumn(c.Name, c.KqlType.Name, Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType))
        .ToList();

    private static KqlResult Materialize(PlanNode plan)
    {
        var reader = RowReader.For(plan.Schema);
        var rows = new List<object?[]>();
        foreach (var row in (IEnumerable)plan.Execute()) rows.Add(reader(row!));
        return new KqlResult(ResultColumns(plan), rows);
    }

    /// <summary>
    /// Runs a query asynchronously and returns its (last) result set. Provider-backed queries (e.g. EF Core) are
    /// read with the provider's async enumeration.
    /// </summary>
    public async Task<KqlResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        var plan = Compile(query)[^1];
        var rows = new List<object?[]>();
        await foreach (var row in Stream(plan, cancellationToken).ConfigureAwait(false)) rows.Add(row);
        return new KqlResult(ResultColumns(plan), rows);
    }

    /// <summary>
    /// Runs a query and streams its rows (values in column order) as they're produced, without collecting the whole
    /// result. Operators that need all their input (sort, summarize, joins in memory...) still buffer internally.
    /// </summary>
    public IAsyncEnumerable<object?[]> StreamAsync(string query, CancellationToken cancellationToken = default) =>
        Stream(Compile(query)[^1], cancellationToken);

    /// <summary>The columns a query would return, without running it.</summary>
    public IReadOnlyList<KqlColumn> GetResultColumns(string query) => ResultColumns(Compile(query)[^1]);

    private static async IAsyncEnumerable<object?[]> Stream(PlanNode plan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = RowReader.For(plan.Schema);
        var query = plan.Execute();
        if (query is IAsyncEnumerable<object> async)
        {
            await foreach (var row in async.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return reader(row);
            yield break;
        }
        foreach (var row in (IEnumerable)query)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reader(row!);
        }
    }
}
