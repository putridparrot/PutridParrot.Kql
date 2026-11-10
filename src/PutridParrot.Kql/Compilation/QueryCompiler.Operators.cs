using System.Collections;
using System.Linq.Expressions;
using PutridParrot.Kql.Runtime;
using PutridParrot.Kql.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = PutridParrot.Kql.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace PutridParrot.Kql.Compilation;

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
            case ProjectAwayOperator away:
            {
                var removed = NamePatterns(away.Expressions.Select(x => x.Element));
                return Reshape(input, output!, new Dictionary<string, string>(), "project-away", name => !removed(name));
            }
            case ProjectKeepOperator keep:
                return Reshape(input, output!, new Dictionary<string, string>(), "project-keep", NamePatterns(keep.Expressions.Select(x => x.Element)));
            case ProjectReorderOperator:
                return Reshape(input, output!, new Dictionary<string, string>(), "project-reorder");
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
            case EvaluateOperator ev:
                return Evaluate(input, ev, output!);
            case SearchOperator search:
                return Search(input, search);
            case RenderOperator:
                return input; // visualisation hint: nothing to draw, the data passes through
            case MakeSeriesOperator ms:
                return MakeSeries(input, ms, output!);
            case TopNestedOperator tn:
                return TopNested(input, tn, output!);
            case MvApplyOperator ma:
                return MvApply(input, ma, output!);
            case ParseKvOperator pk:
                return ParseKv(input, pk, output!);
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

    /// <summary>
    /// The operator's output columns as Kusto declares them, made concrete against the input actually produced:
    /// columns Kusto types as unknown take the input's runtime type, and for open (data-dependent) schemas, e.g. after
    /// `evaluate bag_unpack`, input columns Kusto doesn't know about are carried through when <paramref name="keepExtra"/> allows.
    /// </summary>
    private static List<(string Name, ScalarSymbol Type)> Columns(TableSymbol table, PlanNode input, Func<string, bool>? keepExtra = null,
        IReadOnlyDictionary<string, string>? renames = null)
    {
        var cols = table.Columns.Select(c =>
        {
            var type = RowSchema.AsScalar(c.Type);
            var source = renames is not null && renames.TryGetValue(c.Name, out var old) ? old : c.Name;
            if ((type == ScalarTypes.Unknown || c.Type.IsError) && input.Schema.Find(source) is { } ic) type = ic.KqlType;
            return (c.Name, type);
        }).ToList();

        if (table.IsOpen && keepExtra is not null)
        {
            // follow the input's actual column order: kept/renamed input columns first, then new columns
            var declared = cols.ToDictionary(c => c.Name, c => c.Item2, StringComparer.Ordinal);
            var newName = (renames ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
            var ordered = new List<(string Name, ScalarSymbol Type)>();
            foreach (var c in input.Schema.Columns)
            {
                var name = newName.TryGetValue(c.Name, out var renamed) ? renamed : c.Name;
                if (declared.TryGetValue(name, out var type)) ordered.Add((name, type == ScalarTypes.Unknown ? c.KqlType : type));
                else if (keepExtra(c.Name)) ordered.Add((name, c.KqlType));
            }
            var present = ordered.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            ordered.AddRange(cols.Where(c => !present.Contains(c.Name) && input.Schema.Find(c.Name) is null));
            return ordered;
        }
        return cols;
    }

    /// <summary>Glob match for project-away/keep patterns (`*` wildcards).</summary>
    private static Func<string, bool> NamePatterns(IEnumerable<KExpr> patterns)
    {
        var regexes = patterns.Select(p => new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(p.ToString().Trim()).Replace(@"\*", ".*") + "$")).ToList();
        return name => regexes.Any(r => r.IsMatch(name));
    }

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

        // `a and b and c` where only some conjuncts need in-memory evaluation: filter by the others first (in the
        // provider), then by the rest. A row passes `a and b` only when both are true, so this is the same filter.
        var conjuncts = Conjuncts(condition);
        if (conjuncts.Count > 1)
        {
            var needsClient = conjuncts.Select(c => new ScalarCompiler(this, input.Schema, RowParam(input.Schema), translator: input.Translator,
                    approximationRoot: input.Translator is null ? null : c)
                .CompileAndReport(c)).ToList();
            if (needsClient.Any(x => x) && needsClient.Any(x => !x))
            {
                var node = input;
                foreach (var c in conjuncts.Where((_, i) => !needsClient[i]).Concat(conjuncts.Where((_, i) => needsClient[i])))
                    node = WhereCore(node, c);
                return node;
            }
        }
        return WhereCore(input, condition);
    }

    private static List<KExpr> Conjuncts(KExpr e)
    {
        while (e is ParenthesizedExpression p) e = p.Expression;
        return e is Kusto.Language.Syntax.BinaryExpression { Kind: SyntaxKind.AndExpression } and
            ? [.. Conjuncts(and.Left), .. Conjuncts(and.Right)]
            : [e];
    }

    private PlanNode WhereCore(PlanNode input, KExpr condition)
    {

        var plain = WhereNode(input, condition, null).Node;
        if (input.Translator is not { } translator) return plain;

        // Try a fully translated predicate; approximate (superset) translations such as `has` -> substring match are
        // allowed here and followed by the exact predicate in memory on the (already reduced) rows.
        var (translated, approximate) = WhereNode(input, condition, translator);
        if (translated.RequiresClient) return plain;
        return Adaptive(input, plain,
            approximate ? q => plain.ApplyTo(translated.ApplyTo(q)) : translated.ApplyTo,
            plain.ApplyTo, keepsProvider: !approximate);
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
        var (staged, tuples) = MaterializeTuples(input, expressions, name);

        // Output columns that aren't input columns, in the order the expressions introduce them.
        var fresh = new Queue<string>(output.Columns.Select(c => c.Name).Where(n => input.Schema.Find(n) is null));
        string Next(string? expected = null)
        {
            if (expected is not null && (fresh.Count == 0 || fresh.Peek() != expected)) return expected; // replaces an input column
            return fresh.Dequeue();
        }

        var assigned = new Dictionary<string, Func<ScalarCompiler, LExpr, Type, LExpr>>(StringComparer.Ordinal);
        foreach (var e in expressions)
        {
            switch (e)
            {
                case SimpleNamedExpression sn:
                {
                    var expr = sn.Expression;
                    assigned[Next(sn.Name.SimpleName)] = (c, _, _) => c.Compile(expr);
                    break;
                }
                case CompoundNamedExpression cn:
                {
                    var names = cn.Names.Names.Select(x => x.Element.SimpleName).ToList();
                    for (var k = 0; k < names.Count; k++) assigned[Next(names[k])] = TupleItem(staged, tuples[cn.Expression], k);
                    break;
                }
                case NameReference { ReferencedSymbol: ColumnSymbol }:
                    break;
                default:
                    if (tuples.TryGetValue(e, out var hidden))
                    {
                        var width = ((TupleSymbol)e.ResultType).Columns.Count;
                        for (var k = 0; k < width && fresh.Count > 0; k++) assigned[Next()] = TupleItem(staged, hidden, k);
                    }
                    else if (fresh.Count > 0)
                    {
                        var expr = e;
                        assigned[Next()] = (c, _, _) => c.Compile(expr);
                    }
                    break;
            }
        }

        var columns = Columns(output, staged, n => input.Schema.Find(n) is not null);
        return Select(staged, columns, (compiler, row) => columns.Select(c =>
            assigned.TryGetValue(c.Name, out var value)
                ? value(compiler, row, KqlTypes.ClrTypeFor(c.Type))
                : compiler.Read(staged.Schema.Get(c.Name), row)).ToList(),
            name, expressions);
    }

    private PlanNode Project(PlanNode input, IReadOnlyList<KExpr> expressions, TableSymbol output, string name)
    {
        var width = expressions.Sum(e => TupleSource(e)?.ResultType is TupleSymbol t ? t.Columns.Count : 1);
        if (width != output.Columns.Count)
        {
            // wildcards: fall back to name matching
            if (expressions.All(e => e is NameReference or SimpleNamedExpression { Expression: NameReference }))
                return Reshape(input, output, RenameMap(expressions), name);
            throw new KqlNotSupportedException($"{name} with wildcard expressions", expressions.FirstOrDefault()?.TextStart ?? 0);
        }

        var (staged, tuples) = MaterializeTuples(input, expressions, name);
        if (tuples.Count == 0)
            return Select(input, Columns(output, input), (compiler, _) => expressions.Select(compiler.Compile).ToList(), name, expressions);

        var values = new List<Func<ScalarCompiler, LExpr, Type, LExpr>>();
        foreach (var e in expressions)
        {
            if (TupleSource(e) is { } call)
            {
                for (var k = 0; k < ((TupleSymbol)call.ResultType).Columns.Count; k++) values.Add(TupleItem(staged, tuples[call], k));
            }
            else
            {
                var expr = e;
                values.Add((c, _, _) => c.Compile(expr));
            }
        }
        var columns = Columns(output, staged, _ => false);
        return Select(staged, columns, (compiler, row) =>
            columns.Select((c, i) => values[i](compiler, row, KqlTypes.ClrTypeFor(c.Type))).ToList(), name, expressions);
    }

    /// <summary>The call behind an expression that yields several columns (e.g. <c>series_decompose(y)</c>), if any.</summary>
    private static KExpr? TupleSource(KExpr e)
    {
        var inner = e switch
        {
            CompoundNamedExpression cn => cn.Expression,
            SimpleNamedExpression sn => sn.Expression,
            _ => e
        };
        return inner.ResultType is TupleSymbol ? inner : null;
    }

    /// <summary>
    /// Evaluates every multi-column call once into a hidden column (an object?[] with one element per column), so the
    /// columns unpacked from it share a single evaluation. Returns the staged input and each call's hidden column.
    /// </summary>
    private (PlanNode Input, Dictionary<KExpr, string> Hidden) MaterializeTuples(PlanNode input, IReadOnlyList<KExpr> expressions, string name)
    {
        var calls = expressions.Select(TupleSource).OfType<KExpr>().ToList();
        var hidden = new Dictionary<KExpr, string>();
        if (calls.Count == 0) return (input, hidden);

        var columns = input.Schema.Columns.Select(c => (c.Name, c.KqlType)).ToList();
        foreach (var call in calls)
        {
            hidden[call] = $"__tuple{hidden.Count}";
            columns.Add((hidden[call], ScalarTypes.Dynamic));
        }
        var staged = Select(input, columns, (compiler, row) =>
            input.Schema.Columns.Select(c => compiler.Read(c, row))
                .Concat(calls.Select(call => ExpressionHelpers.Coerce(compiler.Compile(call), typeof(object))))
                .ToList(), name, calls);
        return (staged, hidden);
    }

    private static readonly System.Reflection.MethodInfo TupleItemMethod = typeof(ExpressionHelpers).GetMethod(nameof(ExpressionHelpers.TupleItem))!;

    private static Func<ScalarCompiler, LExpr, Type, LExpr> TupleItem(PlanNode staged, string hidden, int index) =>
        (compiler, row, type) => ExpressionHelpers.Coerce(
            LExpr.Call(TupleItemMethod, compiler.Read(staged.Schema.Get(hidden), row), LExpr.Constant(index)), type);

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
    private PlanNode Reshape(PlanNode input, TableSymbol output, Dictionary<string, string> renames, string name, Func<string, bool>? keepExtra = null)
    {
        var columns = Columns(output, input, keepExtra ?? (_ => true), renames);
        return Select(input, columns, (compiler, row) => columns.Select(c =>
        {
            var source = renames.TryGetValue(c.Name, out var old) ? old : c.Name;
            return compiler.Read(input.Schema.Get(source), row);
        }).ToList(), name);
    }

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
            else if (widths[i] > 1 && PercentilesCall(aggExprs[i]) is { } percentiles)
            {
                // percentiles(x, 50, 90, ...): one column per percentile
                for (var k = 1; k < widths[i]; k++) values.Add(aggCompiler.PercentilesExtra(percentiles, k));
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

    private static FunctionCallExpression? PercentilesCall(KExpr aggregate) =>
        (aggregate is SimpleNamedExpression sn ? sn.Expression : aggregate) is FunctionCallExpression { Name.SimpleName: "percentiles" or "percentilesw" } fc
            ? fc
            : null;

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
            else if (PercentilesCall(aggregates[i]) is { } percentiles)
            {
                var count = percentiles.ArgumentList.Expressions.Count;
                widths[i] = percentiles.Name.SimpleName == "percentilesw" ? count - 2 : count - 1;
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
        var values = dt.Values.Select(v => ExpressionHelpers.Evaluate(compiler.Compile(v.Element))).ToList();
        var width = schema.Columns.Count;
        var create = RowFactory.For(schema);
        var rows = new List<object>();
        for (var i = 0; i + width <= values.Count; i += width)
        {
            var args = schema.Columns.Select((c, j) => KqlConvert.ChangeType(values[i + j], c.ClrType)).ToArray();
            rows.Add(create(args));
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
        var create = LExpr.Lambda<Func<object>>(LExpr.Convert(body, typeof(object))).Compile(preferInterpretation: true);
        return new RowsNode(schema, () => new[] { create() }.CastTo(schema.RowType));
    }

    private PlanNode Range(RangeOperator range)
    {
        var output = (TableSymbol)range.ResultType;
        var schema = RowSchema.Emit(Columns(output));
        var from = EvaluateScalar(range.From);
        var to = EvaluateScalar(range.To);
        var step = EvaluateScalar(range.Step);
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

        var create = RowFactory.For(schema);
        return new RowsNode(schema, () => Values().Select(v => create([KqlConvert.ChangeType(v, colType)])).CastTo(schema.RowType));
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
