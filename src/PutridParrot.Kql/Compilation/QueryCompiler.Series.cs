using System.Collections;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using PutridParrot.Kql.Runtime;
using PutridParrot.Kql.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = PutridParrot.Kql.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace PutridParrot.Kql.Compilation;

// make-series, top-nested, mv-apply, parse-kv. These run in memory.
internal sealed partial class QueryCompiler
{
    /// <summary>The implicit input of operator-rooted sub-queries (mv-apply's `on (...)`).</summary>
    private readonly Stack<PlanNode> _contextInputs = new();

    /// <summary>Compiles an aggregate expression into <c>Func&lt;IEnumerable&lt;TRow&gt;, object?&gt;</c>.</summary>
    private Delegate AggregateOverRows(PlanNode input, KExpr aggregate)
    {
        var seqType = typeof(IEnumerable<>).MakeGenericType(input.Schema.RowType);
        var g = LExpr.Parameter(seqType, "rows");
        var compiler = new ScalarCompiler(this, null, null, new GroupContext(g, input.Schema));
        var body = LExpr.Convert(compiler.Compile(aggregate), typeof(object));
        return LExpr.Lambda(typeof(Func<,>).MakeGenericType(seqType, typeof(object)), body, g).Compile();
    }

    /// <summary>Compiles a scalar expression over one row into <c>Func&lt;TRow, object?&gt;</c>.</summary>
    private Delegate RowFunction(PlanNode input, KExpr expression, Func<LExpr, LExpr>? wrap = null)
    {
        var p = RowParam(input.Schema);
        var value = new ScalarCompiler(this, input.Schema, p).Compile(expression);
        if (wrap is not null) value = wrap(value);
        return LExpr.Lambda(typeof(Func<,>).MakeGenericType(input.Schema.RowType, typeof(object)), LExpr.Convert(value, typeof(object)), p).Compile();
    }

    private static Array TypedArray(Type elementType, IEnumerable<object> items)
    {
        var list = items.ToList();
        var array = Array.CreateInstance(elementType, list.Count);
        for (var i = 0; i < list.Count; i++) array.SetValue(list[i], i);
        return array;
    }

    private static KExpr Unname(KExpr e) => e is SimpleNamedExpression sn ? sn.Expression : e;

    // ------------------------------------------------------------------ make-series

    private PlanNode MakeSeries(PlanNode input, MakeSeriesOperator op, TableSymbol output)
    {
        var rowType = input.Schema.RowType;
        var byExprs = op.ByClause?.Expressions.Select(x => x.Element).ToList() ?? [];
        var aggExprs = op.Aggregates.Select(x => x.Element).ToList();
        var nonEmpty = string.Equals(ParameterValue(op.Parameters, "kind"), "nonempty", StringComparison.OrdinalIgnoreCase);

        var range = op.RangeClause switch
        {
            MakeSeriesFromToStepClause ft => new SeriesRange(
                ft.MakeSeriesFromClause is { } from ? EvaluateScalar(from.Expression) : null,
                ft.MakeSeriesToClause is { } to ? EvaluateScalar(to.Expression) : null,
                EvaluateScalar(ft.MakeSeriesStepClause.Expression) ?? throw new KqlException("make-series needs a step."),
                InRange: false),
            MakeSeriesInRangeClause ir => new SeriesRange(
                EvaluateScalar(ir.Arguments.Expressions[0].Element),
                EvaluateScalar(ir.Arguments.Expressions[1].Element),
                EvaluateScalar(ir.Arguments.Expressions[2].Element)!,
                InRange: true),
            _ => throw new KqlNotSupportedException("make-series range", op.TextStart)
        };

        // group key: new Key(by...) (or null with no by)
        var outCols = Columns(output);
        var keySchema = byExprs.Count == 0 ? null : RowSchema.Emit(outCols.Take(byExprs.Count).ToArray());
        var p = RowParam(input.Schema);
        var keyCompiler = new ScalarCompiler(this, input.Schema, p);
        LExpr keyBody = keySchema is null ? LExpr.Constant(null, typeof(object)) : keySchema.New(byExprs.Select(keyCompiler.Compile).ToList());
        var keyFn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(object)), LExpr.Convert(keyBody, typeof(object)), p).Compile();

        var axisFn = RowFunction(input, op.OnClause.Expression);
        var aggregates = TypedArray(typeof(Func<,>).MakeGenericType(typeof(IEnumerable<>).MakeGenericType(rowType), typeof(object)),
            aggExprs.Select(a => (object)AggregateOverRows(input, Unname(((MakeSeriesExpression)a).Expression))));
        var defaults = aggExprs.Select(a => ((MakeSeriesExpression)a).DefaultExpression is { } d ? EvaluateScalar(d.Expression) : 0L).ToArray();
        var aggTypes = aggExprs.Select(a => KqlTypes.ClrTypeFor(Unname(((MakeSeriesExpression)a).Expression).ResultType)).ToArray();

        // output row: (key, arrays, axis) => new Out(key.k..., arrays..., axis)
        var outSchema = RowSchema.Emit(outCols);
        var keyParam = LExpr.Parameter(typeof(object), "key");
        var arraysParam = LExpr.Parameter(typeof(List<object?>[]), "arrays");
        var axisParam = LExpr.Parameter(typeof(List<object?>), "axis");
        var values = new List<LExpr>();
        if (keySchema is not null)
        {
            var typedKey = LExpr.Convert(keyParam, keySchema.RowType);
            values.AddRange(keySchema.Columns.Select(c => c.Read(typedKey)));
        }
        values.AddRange(aggExprs.Select((_, i) => (LExpr)LExpr.Convert(LExpr.ArrayIndex(arraysParam, LExpr.Constant(i)), typeof(object))));
        values.Add(LExpr.Convert(axisParam, typeof(object)));
        var make = LExpr.Lambda(typeof(Func<,,,>).MakeGenericType(typeof(object), typeof(List<object?>[]), typeof(List<object?>), outSchema.RowType),
            outSchema.New(values), keyParam, arraysParam, axisParam).Compile();

        var run = typeof(SeriesOperators).GetMethod(nameof(SeriesOperators.MakeSeries))!.MakeGenericMethod(rowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q => ExpressionHelpers.ToClientQuery(
            (IEnumerable)run.Invoke(null, [q, keyFn, axisFn, aggregates, defaults, aggTypes, range, nonEmpty, make])!, outSchema.RowType), "make-series");
    }

    // ------------------------------------------------------------------ top-nested

    private PlanNode TopNested(PlanNode input, TopNestedOperator op, TableSymbol output)
    {
        var rowType = input.Schema.RowType;
        var levelType = typeof(TopNestedLevel<>).MakeGenericType(rowType);
        var levels = new List<object>();
        foreach (var clause in op.Clauses.Select(c => c.Element))
        {
            var by = clause.ByExpression;
            var ascending = false;
            if (by is OrderedExpression oe)
            {
                ascending = oe.Ordering?.AscOrDescKeyword?.Kind == SyntaxKind.AscKeyword;
                by = oe.Expression;
            }
            long? count = clause.Expression is { IsMissing: false } n && n.Kind != SyntaxKind.None ? KqlConvert.ToLong(EvaluateScalar(n)) : null;
            var others = clause.WithOthersClause is { } wo ? EvaluateScalar(wo.Expression) : null;
            levels.Add(Activator.CreateInstance(levelType,
                RowFunction(input, Unname(clause.OfExpression)), AggregateOverRows(input, Unname(by)),
                count, clause.WithOthersClause is not null, others, ascending)!);
        }

        var outSchema = RowSchema.Emit(Columns(output));
        var valuesParam = LExpr.Parameter(typeof(object[]), "values");
        var make = LExpr.Lambda(typeof(Func<,>).MakeGenericType(typeof(object[]), outSchema.RowType),
            outSchema.New(outSchema.Columns.Select((c, i) => ExpressionHelpers.Coerce(LExpr.ArrayIndex(valuesParam, LExpr.Constant(i)), c.ClrType)).ToList()),
            valuesParam).Compile();

        var levelArray = TypedArray(levelType, levels);
        var run = typeof(SeriesOperators).GetMethod(nameof(SeriesOperators.TopNested))!.MakeGenericMethod(rowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q => ExpressionHelpers.ToClientQuery(
            (IEnumerable)run.Invoke(null, [q, levelArray, make])!, outSchema.RowType), "top-nested");
    }

    // ------------------------------------------------------------------ mv-apply

    private sealed class ContextRows
    {
        public IEnumerable Rows { get; set; } = Array.Empty<object>();
    }

    private PlanNode MvApply(PlanNode input, MvApplyOperator op, TableSymbol output)
    {
        MarkNotCacheable(); // the sub-query input is passed through shared state
        var rowType = input.Schema.RowType;
        var indexColumn = ParameterValue(op.Parameters, "with_itemindex");
        long? limit = op.RowLimitClause is { } lc ? KqlConvert.ToLong(EvaluateScalar(lc.RowLimit)) : null;

        // the sub-query's input: the record's columns, with expanded columns replaced/added, plus the item index
        var row = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, row);
        var contextCols = input.Schema.Columns.Select(c => (c.Name, c.KqlType)).ToList();
        var expansions = new List<(string Name, LExpr Value)>();
        foreach (var mv in op.Expressions.Select(x => x.Element))
        {
            var (name, value) = mv.Expression switch
            {
                SimpleNamedExpression sn => (sn.Name.SimpleName, sn.Expression),
                NameReference n => (n.SimpleName, (KExpr)n),
                var other => throw new KqlNotSupportedException("mv-apply of an unnamed expression", other.TextStart)
            };
            var type = mv.ToTypeOf?.TypeOf.Types.Count > 0 && mv.ToTypeOf.TypeOf.Types[0].Element is PrimitiveTypeExpression pt
                ? ScalarTypes.GetSymbol(pt.Type.Text) as ScalarSymbol ?? ScalarTypes.Dynamic
                : ScalarTypes.Dynamic;
            var existing = contextCols.FindIndex(c => c.Name == name);
            if (existing >= 0) contextCols[existing] = (name, type);
            else contextCols.Add((name, type));
            expansions.Add((name, ExpressionHelpers.Coerce(compiler.Compile(value), typeof(object))));
        }
        if (indexColumn is not null) contextCols.Add((indexColumn, ScalarTypes.Long));
        var contextSchema = RowSchema.Emit(contextCols);

        // compile the sub-query against a node that yields the current record's expanded rows
        var holder = new ContextRows();
        var contextNode = new RowsNode(contextSchema, () => holder.Rows);
        _contextInputs.Push(contextNode);
        PlanNode sub;
        try
        {
            sub = CompileTabular(op.Subquery.Expression);
        }
        finally
        {
            _contextInputs.Pop();
        }

        // (record, items, index) => new Context(...)
        var items = LExpr.Parameter(typeof(object[]), "items");
        var index = LExpr.Parameter(typeof(long), "i");
        var targets = expansions.Select((e, i) => (e.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        var contextValues = contextSchema.Columns.Select(c =>
            targets.TryGetValue(c.Name, out var t) ? ExpressionHelpers.Coerce(LExpr.ArrayIndex(items, LExpr.Constant(t)), c.ClrType)
            : c.Name == indexColumn ? LExpr.Convert(index, typeof(long?))
            : compiler.Read(input.Schema.Get(c.Name), row)).ToList();
        var makeContext = LExpr.Lambda(typeof(Func<,,,>).MakeGenericType(rowType, typeof(object[]), typeof(long), contextSchema.RowType),
            contextSchema.New(contextValues), row, items, index).Compile();
        var valuesFn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(object[])),
            LExpr.NewArrayInit(typeof(object), expansions.Select(e => e.Value)), row).Compile();

        // (record, subRow) => new Out(...): sub-query columns first, then the record's other columns
        var outSchema = RowSchema.Emit(Columns(output));
        var subRow = LExpr.Parameter(sub.Schema.RowType, "s");
        var outValues = output.Columns.Select(c =>
            sub.Schema.Find(c.Name) is { } sc ? sc.Read(subRow)
            : input.Schema.Find(c.Name) is { } ic ? compiler.Read(ic, row)
            : LExpr.Constant(null, KqlTypes.ClrTypeFor(c.Type))).ToList();
        var makeOut = LExpr.Lambda(typeof(Func<,,>).MakeGenericType(rowType, sub.Schema.RowType, outSchema.RowType),
            outSchema.New(outValues), row, subRow).Compile();

        var runSub = typeof(QueryCompiler).GetMethod(nameof(SubQueryRunner), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(contextSchema.RowType, sub.Schema.RowType).Invoke(null, [holder, sub])!;
        var apply = typeof(RowOperators).GetMethod(nameof(RowOperators.MvApply))!
            .MakeGenericMethod(rowType, contextSchema.RowType, sub.Schema.RowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q => ExpressionHelpers.ToClientQuery(
            (IEnumerable)apply.Invoke(null, [q, valuesFn, makeContext, limit, runSub, makeOut])!, outSchema.RowType), "mv-apply");
    }

    private static Func<List<TContext>, IEnumerable<TSub>> SubQueryRunner<TContext, TSub>(ContextRows holder, PlanNode sub) => rows =>
    {
        holder.Rows = rows;
        return ((IEnumerable<TSub>)sub.Execute()).ToList();
    };

    // ------------------------------------------------------------------ parse-kv

    private PlanNode ParseKv(PlanNode input, ParseKvOperator op, TableSymbol output)
    {
        var keys = op.Keys.Columns.Select(c => c.Element).ToList();
        var keyNames = keys.Select(k => k.Name.SimpleName).ToArray();
        var keyTypes = keys.Select(k => KqlTypes.ClrTypeFor(output.GetColumn(k.Name.SimpleName).Type)).ToArray();

        string? pair = null, kv = null;
        Regex? regex = null;
        var greedy = false;
        var quotes = new List<string>();
        var escapes = new List<char>();
        foreach (var prop in op.WithClause?.Properties.Select(p => p.Element) ?? [])
        {
            var value = EvaluateScalar(prop.Expression);
            switch (prop.Name.SimpleName.ToLowerInvariant())
            {
                case "pair_delimiter": pair = value as string; break;
                case "kv_delimiter": kv = value as string; break;
                case "quote": if (value is string { Length: > 0 } q) quotes.Add(q); break;
                case "escape": if (value is string { Length: > 0 } e) escapes.Add(e[0]); break;
                case "greedy": greedy = KqlConvert.ToBool(value) == true; break;
                case "regex": regex = value is string r ? new Regex(r, RegexOptions.CultureInvariant) : null; break;
            }
        }
        var spec = new KeyValueSpec { PairDelimiter = pair, KvDelimiter = kv, Quotes = quotes, Escapes = escapes, Greedy = greedy, Regex = regex };

        var rowType = input.Schema.RowType;
        var row = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, row);
        var sourceFn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(string)),
            ExpressionHelpers.Coerce(compiler.Compile(op.Expression), typeof(string)), row).Compile();

        var outSchema = RowSchema.Emit(Columns(output));
        var parsed = LExpr.Parameter(typeof(object[]), "values");
        var targets = keyNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var outValues = output.Columns.Select(c => targets.TryGetValue(c.Name, out var t)
            ? ExpressionHelpers.Coerce(LExpr.ArrayIndex(parsed, LExpr.Constant(t)), KqlTypes.ClrTypeFor(c.Type))
            : compiler.Read(input.Schema.Get(c.Name), row)).ToList();
        var make = LExpr.Lambda(typeof(Func<,,>).MakeGenericType(rowType, typeof(object[]), outSchema.RowType),
            outSchema.New(outValues), row, parsed).Compile();

        var apply = typeof(RowOperators).GetMethod(nameof(RowOperators.ParseKv))!.MakeGenericMethod(rowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q => ExpressionHelpers.ToClientQuery(
            (IEnumerable)apply.Invoke(null, [q, sourceFn, keyNames, keyTypes, spec, make])!, outSchema.RowType), "parse-kv");
    }
}
