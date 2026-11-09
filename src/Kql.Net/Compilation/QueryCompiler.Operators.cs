using System.Collections;
using System.Linq.Expressions;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

internal sealed partial class QueryCompiler
{
    private PlanNode ApplyOperator(PlanNode input, QueryOperator op)
    {
        var output = op.ResultType as TableSymbol;
        switch (op)
        {
            case FilterOperator f:
                return Where(input, f.Condition);
            case ExtendOperator e:
                return Extend(input, e.Expressions.Select(x => x.Element).ToList(), output!);
            case ProjectOperator p:
                return Project(input, p.Expressions.Select(x => x.Element).ToList(), output!, "project");
            case ProjectAwayOperator:
            case ProjectKeepOperator:
            case ProjectReorderOperator:
                return Reshape(input, output!, new Dictionary<string, string>(), op.Kind.ToString());
            case ProjectRenameOperator pr:
                return Reshape(input, output!, RenameMap(pr.Expressions.Select(x => x.Element)), "project-rename");
            case TakeOperator t:
                return Take(input, t.Expression);
            case SortOperator s:
                return Sort(input, s.Expressions.Select(x => x.Element).ToList(), "sort");
            case TopOperator top:
                return Take(Sort(input, [top.ByExpression], "top"), top.Expression);
            case CountOperator:
                return Count(input, output!);
            case DistinctOperator d:
                return Distinct(input, d, output!);
            case SummarizeOperator s:
                return Summarize(input, s, output!);
            case JoinOperator j:
                return Join(input, j, output!);
            case LookupOperator l:
                return Lookup(input, l, output!);
            case UnionOperator u:
                return Union(input, u);
            case MvExpandOperator mv:
                return MvExpand(input, mv, output!);
            case ParseOperator parse:
                return Parse(input, parse.Expression, parse.Parameters, parse.Patterns, output!, whereOnly: false);
            case ParseWhereOperator parseWhere:
                return Parse(input, parseWhere.Expression, parseWhere.Parameters, parseWhere.Patterns, output!, whereOnly: true);
            case SerializeOperator ser:
                return ser.Expressions.Count == 0 ? input : Extend(input, ser.Expressions.Select(x => x.Element).ToList(), output!, "serialize");
            case AsOperator:
            case ConsumeOperator:
                return input;
            case GetSchemaOperator:
                return GetSchema(input);
            case SampleOperator sample:
                return Take(input, sample.Expression);
            default:
                throw new KqlNotSupportedException(op.Kind.ToString().Replace("Operator", "").ToLowerInvariant(), op.TextStart);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ParameterExpression RowParam(RowSchema schema, string name = "row") => LExpr.Parameter(schema.RowType, name);

    private static LambdaExpression Lambda(LExpr body, ParameterExpression p) => LExpr.Lambda(body, p);

    /// <summary>
    /// Projects the input into a new emitted row type. <paramref name="values"/> receives the input row parameter
    /// and returns the value expression for each output column.
    /// </summary>
    private OperatorNode Select(PlanNode input, IReadOnlyList<(string Name, ScalarSymbol Type)> columns,
        Func<ScalarCompiler, LExpr, IReadOnlyList<LExpr>> values, string name, IEnumerable<SyntaxNode>? syntax = null)
    {
        var schema = RowSchema.Emit(columns);
        if (UsesWindowFunctions(syntax))
        {
            // window functions: evaluate row by row over the materialised, ordered input
            var (ctx, ctxType) = WindowParam(input);
            var row = LExpr.Property(ctx, "Current");
            var windowCompiler = new ScalarCompiler(this, input.Schema, row, window: ctx);
            var fn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(ctxType, schema.RowType), schema.New(values(windowCompiler, row)), ctx).Compile();
            var run = ctxType.GetMethod("Select")!.MakeGenericMethod(schema.RowType);
            return new OperatorNode(input, schema, true,
                q => ExpressionHelpers.ToClientQuery((IEnumerable)run.Invoke(null, [q, fn])!, schema.RowType), name);
        }

        return Translated(input, translator =>
        {
            var p = RowParam(input.Schema);
            var compiler = new ScalarCompiler(this, input.Schema, p, translator: translator);
            var body = schema.New(values(compiler, p));
            var lambda = Lambda(body, p);
            return new OperatorNode(input, schema, compiler.RequiresClient,
                q => ExpressionHelpers.CallQueryable(q, "Select", [input.Schema.RowType, schema.RowType], LExpr.Quote(lambda)), name);
        });
    }

    private static IReadOnlyList<(string, ScalarSymbol)> Columns(TableSymbol table) =>
        table.Columns.Select(c => (c.Name, RowSchema.AsScalar(c.Type))).ToArray();

    private static bool UsesWindowFunctions(IEnumerable<SyntaxNode>? nodes)
    {
        if (nodes is null) return false;
        var found = false;
        foreach (var node in nodes)
            node.WalkElements(e => found |= e is FunctionCallExpression fc && ScalarCompiler.WindowFunctions.Contains(fc.Name.SimpleName));
        return found;
    }

    private static (ParameterExpression Ctx, Type CtxType) WindowParam(PlanNode input)
    {
        var ctxType = typeof(WindowContext<>).MakeGenericType(input.Schema.RowType);
        return (LExpr.Parameter(ctxType, "w"), ctxType);
    }

    // ------------------------------------------------------------------ where / extend / project

    private PlanNode Where(PlanNode input, KExpr condition)
    {
        if (UsesWindowFunctions([condition]))
        {
            var (ctx, ctxType) = WindowParam(input);
            var windowCompiler = new ScalarCompiler(this, input.Schema, LExpr.Property(ctx, "Current"), window: ctx);
            var fn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(ctxType, typeof(bool)), windowCompiler.CompilePredicate(condition), ctx).Compile();
            var run = ctxType.GetMethod("Where")!;
            return new OperatorNode(input, input.Schema, true,
                q => ExpressionHelpers.ToClientQuery((IEnumerable)run.Invoke(null, [q, fn])!, input.Schema.RowType), "where");
        }

        if (input.Translator is { } translator)
        {
            // Try a fully translated predicate; approximate (superset) translations such as `has` -> LIKE are allowed
            // here and followed by the exact predicate in memory on the (already reduced) rows.
            var translated = WhereNode(input, condition, translator);
            if (!translated.Node.RequiresClient)
                return translated.Approximate ? WhereNode(translated.Node, condition, null).Node : translated.Node;
        }
        return WhereNode(input, condition, null).Node;
    }

    private (OperatorNode Node, bool Approximate) WhereNode(PlanNode input, KExpr condition, IKqlQueryTranslator? translator)
    {
        var p = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, p, translator: translator, approximationRoot: translator is null ? null : condition);
        var lambda = Lambda(compiler.CompilePredicate(condition), p);
        // with no translator the exact predicate may still be provider-translatable (plain comparisons): keep it pushed down
        var node = new OperatorNode(input, input.Schema, compiler.RequiresClient,
            q => ExpressionHelpers.CallQueryable(q, "Where", [input.Schema.RowType], LExpr.Quote(lambda)), "where");
        return (node, compiler.UsedApproximation);
    }

    private PlanNode Extend(PlanNode input, IReadOnlyList<KExpr> expressions, TableSymbol output, string name = "extend")
    {
        var assigned = new Dictionary<string, KExpr>(StringComparer.Ordinal);
        var unnamed = new Queue<KExpr>();
        foreach (var e in expressions)
        {
            if (e is SimpleNamedExpression sn) assigned[sn.Name.SimpleName] = sn.Expression;
            else if (e is CompoundNamedExpression) throw new KqlNotSupportedException("tuple assignment in extend", e.TextStart);
            else if (e is NameReference { ReferencedSymbol: ColumnSymbol }) continue;
            else unnamed.Enqueue(e);
        }
        foreach (var c in output.Columns)
        {
            if (unnamed.Count == 0) break;
            if (!assigned.ContainsKey(c.Name) && input.Schema.Find(c.Name) is null) assigned[c.Name] = unnamed.Dequeue();
        }

        return Select(input, Columns(output), (compiler, row) => output.Columns.Select(c =>
            assigned.TryGetValue(c.Name, out var expr) ? compiler.Compile(expr) : compiler.Read(input.Schema.Get(c.Name), row)).ToList(),
            name, expressions);
    }

    private PlanNode Project(PlanNode input, IReadOnlyList<KExpr> expressions, TableSymbol output, string name)
    {
        if (expressions.Count != output.Columns.Count || expressions.Any(e => e is CompoundNamedExpression))
        {
            // wildcards / tuple results: fall back to name matching
            if (expressions.All(e => e is NameReference or SimpleNamedExpression { Expression: NameReference }))
                return Reshape(input, output, RenameMap(expressions), name);
            throw new KqlNotSupportedException($"{name} with wildcard or tuple expressions", expressions.FirstOrDefault()?.TextStart ?? 0);
        }
        return Select(input, Columns(output), (compiler, _) => expressions.Select(compiler.Compile).ToList(), name, expressions);
    }

    /// <summary>new name -> old name for `new = old` pairs.</summary>
    private static Dictionary<string, string> RenameMap(IEnumerable<KExpr> expressions)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in expressions)
        {
            if (e is SimpleNamedExpression { Expression: NameReference old } sn) map[sn.Name.SimpleName] = old.SimpleName;
        }
        return map;
    }

    /// <summary>Re-shapes rows to the output schema, taking each column from the input by (possibly renamed) name.</summary>
    private PlanNode Reshape(PlanNode input, TableSymbol output, Dictionary<string, string> renames, string name) =>
        Select(input, Columns(output), (compiler, row) => output.Columns.Select(c =>
        {
            var source = renames.TryGetValue(c.Name, out var old) ? old : c.Name;
            return compiler.Read(input.Schema.Get(source), row);
        }).ToList(), name);

    // ------------------------------------------------------------------ take / sort / count / distinct

    private PlanNode Take(PlanNode input, KExpr count)
    {
        var n = KqlConvert.ToLong(EvaluateScalar(count)) ?? 0;
        var take = (int)Math.Clamp(n, 0, int.MaxValue);
        return new OperatorNode(input, input.Schema, false,
            q => ExpressionHelpers.CallQueryable(q, "Take", [input.Schema.RowType], LExpr.Constant(take)), "take");
    }

    private sealed record SortKey(LambdaExpression Key, bool Descending);

    private PlanNode Sort(PlanNode input, IReadOnlyList<KExpr> expressions, string name) =>
        Translated(input, translator => Sort(input, expressions, name, translator));

    private OperatorNode Sort(PlanNode input, IReadOnlyList<KExpr> expressions, string name, IKqlQueryTranslator? translator)
    {
        var p = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, p, translator: translator);
        var keys = new List<SortKey>();
        foreach (var e in expressions)
        {
            var (expr, ordering) = e is OrderedExpression oe ? (oe.Expression, oe.Ordering) : (e, null);
            var descending = ordering?.AscOrDescKeyword?.Kind != SyntaxKind.AscKeyword;
            var nullsKeyword = ordering?.NullsClause?.FirstOrLastKeyword?.Kind;
            var nullsFirst = nullsKeyword is null ? !descending : nullsKeyword == SyntaxKind.FirstKeyword;

            var key = compiler.Compile(expr);
            // default comparer orders null lowest: asc => nulls first, desc => nulls last
            if (nullsFirst == descending) keys.Add(new SortKey(Lambda(ExpressionHelpers.IsNull(key), p), descending));
            keys.Add(new SortKey(Lambda(key, p), descending));
        }

        var rowType = input.Schema.RowType;
        return new OperatorNode(input, input.Schema, compiler.RequiresClient, q =>
        {
            var inMemory = ExpressionHelpers.IsInMemory(q);
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                var method = (i == 0 ? "OrderBy" : "ThenBy") + (k.Descending ? "Descending" : "");
                var typeArgs = new[] { rowType, k.Key.ReturnType };
                q = inMemory
                    ? ExpressionHelpers.CallQueryable(q, method, typeArgs, LExpr.Quote(k.Key), ComparerFor(k.Key.ReturnType))
                    : ExpressionHelpers.CallQueryable(q, method, typeArgs, LExpr.Quote(k.Key));
            }
            return q;
        }, name);
    }

    private static LExpr ComparerFor(Type t)
    {
        var comparer = typeof(KqlComparer).GetMethod(nameof(KqlComparer.For))!.MakeGenericMethod(t).Invoke(null, null);
        return LExpr.Constant(comparer, typeof(IComparer<>).MakeGenericType(t));
    }

    private PlanNode Count(PlanNode input, TableSymbol output)
    {
        var schema = RowSchema.Emit(Columns(output));
        return new OperatorNode(input, schema, false, q =>
        {
            var count = (long)ExpressionHelpers.ExecuteQueryable(q, "LongCount", [input.Schema.RowType])!;
            return ExpressionHelpers.ToClientQuery(new[] { schema.Constructor!.Invoke([(long?)count]) }.CastTo(schema.RowType), schema.RowType);
        }, "count", keepsProvider: false);
    }

    private PlanNode Distinct(PlanNode input, DistinctOperator op, TableSymbol output)
    {
        var exprs = op.Expressions.Select(x => x.Element).ToList();
        var projected = exprs.Count == 1 && exprs[0] is StarExpression
            ? Reshape(input, output, new Dictionary<string, string>(), "distinct")
            : Project(input, exprs, output, "distinct");
        return new OperatorNode(projected, projected.Schema, false,
            q => ExpressionHelpers.CallQueryable(q, "Distinct", [projected.Schema.RowType]), "distinct");
    }

    // ------------------------------------------------------------------ summarize

    private PlanNode Summarize(PlanNode input, SummarizeOperator op, TableSymbol output)
    {
        var byExprs = op.ByClause?.Expressions.Select(x => x.Element).ToList() ?? [];
        var aggExprs = op.Aggregates.Select(x => x.Element).ToList();
        var widths = AggregateWidths(aggExprs, output.Columns.Count - byExprs.Count, op.TextStart);

        return Translated(input, translator => Summarize(input, byExprs, aggExprs, widths, output, translator));
    }

    private OperatorNode Summarize(PlanNode input, List<KExpr> byExprs, List<KExpr> aggExprs, int[] widths, TableSymbol output,
        IKqlQueryTranslator? translator)
    {
        var outCols = Columns(output);
        var keyCols = outCols.Take(byExprs.Count).ToArray();
        var outSchema = RowSchema.Emit(outCols);
        var rowType = input.Schema.RowType;

        // key selector: row => new Key(by...)
        var p = RowParam(input.Schema);
        var keyCompiler = new ScalarCompiler(this, input.Schema, p, translator: translator);
        var keyType = byExprs.Count == 0 ? typeof(int) : RowSchema.Emit(keyCols).RowType;
        var keySchema = byExprs.Count == 0 ? null : RowSchema.Emit(keyCols);
        LExpr keyBody = keySchema is null ? LExpr.Constant(1) : keySchema.New(byExprs.Select(keyCompiler.Compile).ToList());
        var keySelector = Lambda(keyBody, p);

        // result selector: g => new Out(g.Key.k..., aggregates...)
        var groupingType = typeof(IGrouping<,>).MakeGenericType(keyType, rowType);
        var g = LExpr.Parameter(groupingType, "g");
        var aggCompiler = new ScalarCompiler(this, null, null, new GroupContext(g, input.Schema), translator: translator);
        var values = new List<LExpr>();
        if (keySchema is not null)
        {
            var key = LExpr.Property(g, "Key");
            values.AddRange(keySchema.Columns.Select(c => c.Read(key)));
        }
        var column = byExprs.Count;
        for (var i = 0; i < aggExprs.Count; i++)
        {
            values.Add(aggCompiler.Compile(aggExprs[i]));
            if (widths[i] > 1 && ArgCall(aggExprs[i]) is { } arg)
            {
                // arg_max/arg_min extra columns: values from the same (max/min) row
                var args = arg.ArgumentList.Expressions.Select(x => x.Element).ToList();
                var max = arg.Name.SimpleName == "arg_max";
                var star = args.Skip(1).Any(a => a is StarExpression);
                for (var k = 1; k < widths[i]; k++)
                {
                    var name = outCols[column + k].Item1;
                    var expr = star ? null : args[k];
                    var col = star ? input.Schema.Find(name)
                                     ?? throw new KqlNotSupportedException($"arg_max/arg_min column '{name}'", arg.TextStart)
                                   : null;
                    values.Add(aggCompiler.ArgValue(args[0], max, (c, r) => col is not null ? c.Read(col, r) : c.Compile(expr!)));
                }
            }
            column += widths[i];
        }
        var resultSelector = LExpr.Lambda(outSchema.New(values), g);

        var requiresClient = keyCompiler.RequiresClient || aggCompiler.RequiresClient;
        return new OperatorNode(input, outSchema, requiresClient, q =>
        {
            var grouped = ExpressionHelpers.CallQueryable(q, "GroupBy", [rowType, keyType], LExpr.Quote(keySelector));
            var result = ExpressionHelpers.CallQueryable(grouped, "Select", [groupingType, outSchema.RowType], LExpr.Quote(resultSelector));
            if (byExprs.Count > 0) return result;

            // summarize without `by` always yields exactly one row, even for empty input
            var rows = result.Cast<object>().ToList();
            if (rows.Count == 0)
            {
                var empty = Activator.CreateInstance(typeof(EmptyGrouping<,>).MakeGenericType(keyType, rowType), 1)!;
                rows.Add(resultSelector.Compile().DynamicInvoke(empty)!);
            }
            return ExpressionHelpers.ToClientQuery(rows.CastTo(outSchema.RowType), outSchema.RowType);
        }, "summarize", keepsProvider: byExprs.Count > 0 ? null : false);
    }

    private static FunctionCallExpression? ArgCall(KExpr aggregate) =>
        (aggregate is SimpleNamedExpression sn ? sn.Expression : aggregate) is FunctionCallExpression { Name.SimpleName: "arg_max" or "arg_min" } fc
            ? fc
            : null;

    /// <summary>
    /// Number of output columns each aggregate produces: 1, or for arg_max/arg_min 1 + the selected columns
    /// (`*` takes whatever output columns remain).
    /// </summary>
    private static int[] AggregateWidths(IReadOnlyList<KExpr> aggregates, int available, int position)
    {
        var widths = new int[aggregates.Count];
        var starIndex = -1;
        for (var i = 0; i < aggregates.Count; i++)
        {
            if (ArgCall(aggregates[i]) is { } arg)
            {
                var args = arg.ArgumentList.Expressions.Select(x => x.Element).ToList();
                if (args.Skip(1).Any(a => a is StarExpression))
                {
                    if (starIndex >= 0) throw new KqlNotSupportedException("several arg_max/arg_min(…, *) in one summarize", position);
                    starIndex = i;
                    continue;
                }
                widths[i] = args.Count;
            }
            else
            {
                widths[i] = 1;
            }
        }
        if (starIndex >= 0) widths[starIndex] = available - widths.Sum();
        if (widths.Sum() != available || widths.Any(w => w < 1))
            throw new KqlNotSupportedException("summarize output shape", position);
        return widths;
    }

    // ------------------------------------------------------------------ getschema

    private PlanNode GetSchema(PlanNode input)
    {
        var schema = RowSchema.Emit([
            ("ColumnName", ScalarTypes.String), ("ColumnOrdinal", ScalarTypes.Int),
            ("DataType", ScalarTypes.String), ("ColumnType", ScalarTypes.String)
        ]);
        return new RowsNode(schema, () => input.Schema.Columns.Select((c, i) =>
            schema.Constructor!.Invoke([c.Name, (int?)i, "System." + (Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType).Name, c.KqlType.Name])).ToList().CastTo(schema.RowType));
    }

    // ------------------------------------------------------------------ sources: datatable / print / range

    private PlanNode DataTable(DataTableExpression dt)
    {
        var output = (TableSymbol)dt.ResultType;
        var schema = RowSchema.Emit(Columns(output));
        var compiler = new ScalarCompiler(this, null, null);
        var values = dt.Values.Select(v => LExpr.Lambda(LExpr.Convert(compiler.Compile(v.Element), typeof(object))).Compile().DynamicInvoke()).ToList();
        var width = schema.Columns.Count;
        var rows = new List<object>();
        for (var i = 0; i + width <= values.Count; i += width)
        {
            var args = schema.Columns.Select((c, j) => KqlConvert.ChangeType(values[i + j], c.ClrType)).ToArray();
            rows.Add(schema.Constructor!.Invoke(args));
        }
        return new RowsNode(schema, () => rows.CastTo(schema.RowType));
    }

    private PlanNode Print(PrintOperator print)
    {
        var output = (TableSymbol)print.ResultType;
        var schema = RowSchema.Emit(Columns(output));
        var compiler = new ScalarCompiler(this, null, null);
        var exprs = print.Expressions.Select(x => x.Element).ToList();
        var body = schema.New(exprs.Select(compiler.Compile).ToList());
        var create = LExpr.Lambda(LExpr.Convert(body, typeof(object))).Compile();
        return new RowsNode(schema, () => new[] { create.DynamicInvoke()! }.CastTo(schema.RowType));
    }

    private PlanNode Range(RangeOperator range)
    {
        var output = (TableSymbol)range.ResultType;
        var schema = RowSchema.Emit(Columns(output));
        var from = EvaluateScalar(range.From);
        var to = EvaluateScalar(range.To);
        var step = EvaluateScalar(range.Step);
        var ctor = schema.Constructor!;
        var colType = schema.Columns[0].ClrType;

        IEnumerable<object?> Values()
        {
            switch (from)
            {
                case DateTime df when to is DateTime dt && step is TimeSpan ts && ts > TimeSpan.Zero:
                    for (var d = df; d <= dt; d += ts) yield return d;
                    break;
                case TimeSpan tf when to is TimeSpan tt && step is TimeSpan ts2 && ts2 > TimeSpan.Zero:
                    for (var t = tf; t <= tt; t += ts2) yield return t;
                    break;
                case double or decimal when KqlConvert.ToDouble(step) is > 0:
                    for (var x = KqlConvert.ToDouble(from)!.Value; x <= KqlConvert.ToDouble(to); x += KqlConvert.ToDouble(step)!.Value) yield return x;
                    break;
                default:
                    var s = KqlConvert.ToLong(step) ?? 1;
                    if (s == 0) yield break;
                    var a = KqlConvert.ToLong(from)!.Value;
                    var b = KqlConvert.ToLong(to)!.Value;
                    for (var x = a; s > 0 ? x <= b : x >= b; x += s) yield return x;
                    break;
            }
        }

        return new RowsNode(schema, () => Values().Select(v => ctor.Invoke([KqlConvert.ChangeType(v, colType)])).CastTo(schema.RowType));
    }
}

/// <summary>An empty <see cref="IGrouping{TKey,TElement}"/> used to evaluate aggregates over no rows.</summary>
internal sealed class EmptyGrouping<TKey, T>(TKey key) : IGrouping<TKey, T>
{
    public TKey Key { get; } = key;
    public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EnumerableCastExtensions
{
    /// <summary>Casts a sequence of objects to <c>IEnumerable&lt;rowType&gt;</c>.</summary>
    public static IEnumerable CastTo(this IEnumerable source, Type rowType) =>
        (IEnumerable)typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(rowType).Invoke(null, [source])!;
}
