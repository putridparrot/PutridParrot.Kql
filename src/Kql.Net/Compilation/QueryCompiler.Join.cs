using System.Linq.Expressions;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using BinaryExpression = Kusto.Language.Syntax.BinaryExpression;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

internal sealed partial class QueryCompiler
{
    private static string? ParameterValue(SyntaxList<NamedParameter> parameters, string name)
    {
        foreach (var p in parameters)
        {
            if (!string.Equals(p.Name.SimpleName, name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Expression switch
            {
                NameReference n => n.SimpleName,
                NameDeclaration d => d.SimpleName,
                LiteralExpression l => l.LiteralValue?.ToString(),
                _ => p.Expression.ToString().Trim()
            };
        }
        return null;
    }

    /// <summary>Extracts (leftKey, rightKey) pairs from a join/lookup condition.</summary>
    private static List<(KExpr Left, KExpr Right)> JoinKeys(JoinConditionClause? clause, int position)
    {
        var keys = new List<(KExpr, KExpr)>();
        if (clause is not JoinOnClause on) throw new KqlNotSupportedException("join without `on` clause", position);

        void Add(KExpr e)
        {
            switch (e)
            {
                case NameReference n:
                    keys.Add((n, n));
                    break;
                case BinaryExpression { Kind: SyntaxKind.AndExpression } and:
                    Add(and.Left);
                    Add(and.Right);
                    break;
                case BinaryExpression { Kind: SyntaxKind.EqualExpression } eq:
                    var (l, r) = IsSide(eq.Left, "$right") || IsSide(eq.Right, "$left") ? (eq.Right, eq.Left) : (eq.Left, eq.Right);
                    keys.Add((StripSide(l), StripSide(r)));
                    break;
                case ParenthesizedExpression p:
                    Add(p.Expression);
                    break;
                default:
                    throw new KqlNotSupportedException("join condition " + e.Kind, e.TextStart);
            }
        }

        foreach (var e in on.Expressions) Add(e.Element);
        return keys;
    }

    private static bool IsSide(KExpr e, string side) => e is PathExpression { Expression: NameReference n } && n.SimpleName == side;

    private static KExpr StripSide(KExpr e) => e is PathExpression { Expression: NameReference { SimpleName: "$left" or "$right" } } p ? p.Selector : e;

    /// <summary>Compiles a join key against one side; `$left.x` / `$right.x` resolve to the column x of that side.</summary>
    private LambdaExpression KeyLambda(PlanNode side, IReadOnlyList<KExpr> keys, RowSchema keySchema)
    {
        var p = RowParam(side.Schema, "k");
        var compiler = new ScalarCompiler(this, side.Schema, p);
        var values = keys.Select(k => KeyPart(compiler, side, k, p))
            // KQL strings are never null: null and "" are the same key
            .Select(v => v.Type == typeof(string) ? (LExpr)LExpr.Coalesce(v, LExpr.Constant("")) : v)
            .ToList();
        return LExpr.Lambda(keySchema.New(values), p);
    }

    private PlanNode Join(PlanNode left, JoinOperator op, TableSymbol output)
    {
        var right = CompileTabular(op.Expression);
        var kind = (ParameterValue(op.Parameters, "kind") ?? "innerunique").ToLowerInvariant();
        var keys = JoinKeys(op.ConditionClause, op.TextStart);
        return BuildJoin(left, right, kind, keys, output, lookup: false, op.TextStart);
    }

    private PlanNode Lookup(PlanNode left, LookupOperator op, TableSymbol output)
    {
        var right = CompileTabular(op.Expression);
        var kind = (ParameterValue(op.Parameters, "kind") ?? "leftouter").ToLowerInvariant();
        var keys = JoinKeys(op.LookupClause, op.TextStart);
        return BuildJoin(left, right, kind, keys, output, lookup: true, op.TextStart);
    }

    private PlanNode BuildJoin(PlanNode left, PlanNode right, string kind, List<(KExpr Left, KExpr Right)> keys, TableSymbol output, bool lookup, int position)
    {
        var joinKind = kind switch
        {
            "innerunique" => JoinKind.InnerUnique,
            "inner" => JoinKind.Inner,
            "leftouter" => JoinKind.LeftOuter,
            "rightouter" => JoinKind.RightOuter,
            "fullouter" => JoinKind.FullOuter,
            "leftanti" or "anti" or "leftantisemi" => JoinKind.LeftAnti,
            "rightanti" or "rightantisemi" => JoinKind.RightAnti,
            "leftsemi" => JoinKind.LeftSemi,
            "rightsemi" => JoinKind.RightSemi,
            _ => throw new KqlNotSupportedException($"join kind={kind}", position)
        };

        // key types: use the left key's type, coerce the right key to it
        var leftKeyTypes = keys.Select(k => RowSchema.AsScalar(k.Left.ResultType)).ToArray();
        var keySchema = RowSchema.Emit(leftKeyTypes.Select((t, i) => ("k" + i, t)).ToArray());
        var leftKey = KeyLambda(left, keys.Select(k => k.Left).ToList(), keySchema);
        var rightKey = KeyLambda(right, keys.Select(k => k.Right).ToList(), keySchema);

        if (!lookup && joinKind is not (JoinKind.LeftAnti or JoinKind.LeftSemi or JoinKind.RightAnti or JoinKind.RightSemi)
                    && output.Columns.Count != left.Schema.Columns.Count + right.Schema.Columns.Count)
            throw new KqlNotSupportedException("join output shape", position);

        var outSchema = RowSchema.Emit(Columns(output));

        // Builds the output row from (optional) left/right rows; `has` expressions mark rows that may be missing.
        NewExpression Body(LExpr? l, LExpr? hasL, LExpr? r, LExpr? hasR)
        {
            LExpr Read(RowColumn col, LExpr? row, LExpr? has) =>
                row is null ? LExpr.Constant(null, col.ClrType)
                : has is null ? col.Read(row)
                : LExpr.Condition(has, col.Read(row), LExpr.Constant(null, col.ClrType));

            var leftCount = left.Schema.Columns.Count;
            var values = joinKind switch
            {
                JoinKind.LeftAnti or JoinKind.LeftSemi => output.Columns.Select(c => Read(left.Schema.Get(c.Name), l, hasL)),
                JoinKind.RightAnti or JoinKind.RightSemi => output.Columns.Select(c => Read(right.Schema.Get(c.Name), r, hasR)),
                _ when lookup => output.Columns.Select((c, i) => i < leftCount
                    ? Read(left.Schema.Columns[i], l, hasL)
                    : Read(right.Schema.Get(c.Name), r, hasR)),
                _ => output.Columns.Select((_, i) => i < leftCount
                    ? Read(left.Schema.Columns[i], l, hasL)
                    : Read(right.Schema.Columns[i - leftCount], r, hasR))
            };
            return outSchema.New(values.ToList());
        }

        // in-memory implementation
        var lp = LExpr.Parameter(left.Schema.RowType, "l");
        var hasLp = LExpr.Parameter(typeof(bool), "hasL");
        var rp = LExpr.Parameter(right.Schema.RowType, "r");
        var hasRp = LExpr.Parameter(typeof(bool), "hasR");
        var resultSelector = LExpr.Lambda(Body(lp, hasLp, rp, hasRp), lp, hasLp, rp, hasRp).Compile();
        var method = typeof(JoinHelper).GetMethod(nameof(JoinHelper.Join))!
            .MakeGenericMethod(left.Schema.RowType, right.Schema.RowType, keySchema.RowType, outSchema.RowType);
        var lk = leftKey.Compile();
        var rk = rightKey.Compile();

        // provider (SQL) implementation, when both sides come from the same provider at run time
        var sql = SqlJoin(left, right, joinKind, keys, leftKey, Body, outSchema);
        var sameTranslator = left.Translator is not null && left.Translator == right.Translator ? left.Translator : null;

        return new CombineNode([left, right], outSchema, inputs =>
        {
            if (sql is not null && !ExpressionHelpers.IsInMemory(inputs[0]) && !ExpressionHelpers.IsInMemory(inputs[1])
                && inputs[0].Provider == inputs[1].Provider)
                return sql(inputs[0], inputs[1]);
            var result = (System.Collections.IEnumerable)method.Invoke(null, [inputs[0], inputs[1], lk, rk, resultSelector, joinKind])!;
            return ExpressionHelpers.ToClientQuery(result, outSchema.RowType);
        }, sql is null ? null : sameTranslator);
    }

    /// <summary>
    /// Builds the join as provider-translatable LINQ (SelectMany / Where / Any / DefaultIfEmpty, which EF Core turns
    /// into SQL joins and EXISTS). Returns null when the join can't be expressed that way.
    /// </summary>
    private Func<IQueryable, IQueryable, IQueryable>? SqlJoin(PlanNode left, PlanNode right, JoinKind kind,
        List<(KExpr Left, KExpr Right)> keys, LambdaExpression leftKey,
        Func<LExpr?, LExpr?, LExpr?, LExpr?, NewExpression> body, RowSchema outSchema)
    {
        if (kind == JoinKind.FullOuter) return null;
        if (left.Schema.Columns.Any(c => c.ClientOnly) || right.Schema.Columns.Any(c => c.ClientOnly)) return null;

        var lType = left.Schema.RowType;
        var rType = right.Schema.RowType;
        var l = LExpr.Parameter(lType, "l");
        var r = LExpr.Parameter(rType, "r");

        // key equality: l.k == r.k for each key, false when either side is null (KQL `==` semantics)
        var lCompiler = new ScalarCompiler(this, left.Schema, l, translator: left.Translator);
        var rCompiler = new ScalarCompiler(this, right.Schema, r, translator: right.Translator);
        LExpr? match = null;
        foreach (var (lk, rk) in keys)
        {
            var (a, b) = ScalarCompiler.Unify(KeyPart(lCompiler, left, lk, l), KeyPart(rCompiler, right, rk, r));
            LExpr eq = LExpr.Equal(a, b);
            if (a.Type != typeof(string))
            {
                if (a.Type.IsValueType && Nullable.GetUnderlyingType(a.Type) is null) { }
                else eq = LExpr.AndAlso(LExpr.AndAlso(LExpr.NotEqual(a, LExpr.Constant(null, a.Type)), LExpr.NotEqual(b, LExpr.Constant(null, b.Type))), eq);
            }
            match = match is null ? eq : LExpr.AndAlso(match, eq);
        }
        if (match is null || lCompiler.RequiresClient || rCompiler.RequiresClient) return null;

        var outType = outSchema.RowType;
        var matchForRight = LExpr.Lambda(match, r);   // r => match(l, r), closes over l
        var matchForLeft = LExpr.Lambda(match, l);    // l => match(l, r), closes over r

        LExpr WhereCall(LExpr source, Type t, LambdaExpression predicate) =>
            LExpr.Call(typeof(Queryable), nameof(Queryable.Where), [t], source, LExpr.Quote(predicate));
        LExpr AnyCall(LExpr source, Type t, LambdaExpression predicate) =>
            LExpr.Call(typeof(Queryable), nameof(Queryable.Any), [t], source, LExpr.Quote(predicate));
        LExpr NotNull(LExpr row) => LExpr.NotEqual(row, LExpr.Constant(null, row.Type));

        IQueryable Create(IQueryable source, LExpr call) => source.Provider.CreateQuery(call);

        // l => <right rows matching l>, then (l, r) => out
        IQueryable SelectMany(IQueryable outer, IQueryable inner, ParameterExpression o, ParameterExpression i, LambdaExpression innerMatch,
            bool outerJoin, NewExpression result)
        {
            LExpr matches = WhereCall(inner.Expression, i.Type, innerMatch);
            if (outerJoin) matches = LExpr.Call(typeof(Queryable), nameof(Queryable.DefaultIfEmpty), [i.Type], matches);
            var collection = LExpr.Lambda(typeof(Func<,>).MakeGenericType(o.Type, typeof(IEnumerable<>).MakeGenericType(i.Type)), matches, o);
            var selector = LExpr.Lambda(result, o, i);
            return Create(outer, LExpr.Call(typeof(Queryable), nameof(Queryable.SelectMany), [o.Type, i.Type, outType],
                outer.Expression, LExpr.Quote(collection), LExpr.Quote(selector)));
        }

        return kind switch
        {
            JoinKind.Inner => (lq, rq) => SelectMany(lq, rq, l, r, matchForRight, false, body(l, null, r, null)),
            JoinKind.InnerUnique => (lq, rq) =>
            {
                // keep one left row per key: distinct keys, then the first left row for each
                // (EF Core turns this into ROW_NUMBER() OVER (PARTITION BY key); GroupBy-based forms don't compose with joins)
                var keyType = leftKey.ReturnType;
                var distinctKeys = LExpr.Call(typeof(Queryable), nameof(Queryable.Distinct), [keyType],
                    LExpr.Call(typeof(Queryable), nameof(Queryable.Select), [lType, keyType], lq.Expression, LExpr.Quote(leftKey)));
                var k = LExpr.Parameter(keyType, "key");
                var l2 = LExpr.Parameter(lType, "l2");
                var keyCompiler = new ScalarCompiler(this, left.Schema, l2);
                var keyProps = RowTypeFactory.GetProperties(keyType);
                LExpr sameKey = LExpr.Constant(true);
                for (var i = 0; i < keys.Count; i++)
                {
                    var part = KeyPart(keyCompiler, left, keys[i].Left, l2);
                    if (part.Type == typeof(string)) part = LExpr.Coalesce(part, LExpr.Constant(""));
                    sameKey = LExpr.AndAlso(sameKey, LExpr.Equal(ExpressionHelpers.Coerce(part, keyProps[i].PropertyType), LExpr.Property(k, keyProps[i])));
                }
                var firstRow = LExpr.Call(typeof(Queryable), nameof(Queryable.Take), [lType],
                    LExpr.Call(typeof(Queryable), nameof(Queryable.Where), [lType], lq.Expression, LExpr.Quote(LExpr.Lambda(sameKey, l2))),
                    LExpr.Constant(1));
                var perKey = LExpr.Lambda(typeof(Func<,>).MakeGenericType(keyType, typeof(IEnumerable<>).MakeGenericType(lType)), firstRow, k);
                var deduped = Create(lq, LExpr.Call(typeof(Queryable), nameof(Queryable.SelectMany), [keyType, lType], distinctKeys, LExpr.Quote(perKey)));
                return SelectMany(deduped, rq, l, r, matchForRight, false, body(l, null, r, null));
            },
            JoinKind.LeftOuter => (lq, rq) => SelectMany(lq, rq, l, r, matchForRight, true, body(l, null, r, NotNull(r))),
            JoinKind.RightOuter => (lq, rq) => SelectMany(rq, lq, r, l, matchForLeft, true, body(l, NotNull(l), r, null)),
            JoinKind.LeftSemi or JoinKind.LeftAnti => (lq, rq) =>
            {
                LExpr any = AnyCall(rq.Expression, rType, matchForRight);
                if (kind == JoinKind.LeftAnti) any = LExpr.Not(any);
                var filtered = WhereCall(lq.Expression, lType, LExpr.Lambda(any, l));
                return Create(lq, LExpr.Call(typeof(Queryable), nameof(Queryable.Select), [lType, outType], filtered,
                    LExpr.Quote(LExpr.Lambda(body(l, null, null, null), l))));
            },
            JoinKind.RightSemi or JoinKind.RightAnti => (lq, rq) =>
            {
                LExpr any = AnyCall(lq.Expression, lType, matchForLeft);
                if (kind == JoinKind.RightAnti) any = LExpr.Not(any);
                var filtered = WhereCall(rq.Expression, rType, LExpr.Lambda(any, r));
                return Create(rq, LExpr.Call(typeof(Queryable), nameof(Queryable.Select), [rType, outType], filtered,
                    LExpr.Quote(LExpr.Lambda(body(null, null, r, null), r))));
            },
            _ => null
        };
    }

    private static LExpr KeyPart(ScalarCompiler compiler, PlanNode side, KExpr key, LExpr row) =>
        key is NameReference n && side.Schema.Find(n.SimpleName) is { } col ? compiler.Read(col, row) : compiler.Compile(key);

    // ------------------------------------------------------------------ union

    private PlanNode Union(PlanNode? first, UnionOperator op)
    {
        var output = (TableSymbol)op.ResultType;
        var inputs = new List<(PlanNode Plan, string Name)>();
        if (first is not null) inputs.Add((first, "union_arg0"));
        foreach (var e in op.Expressions.Select(x => x.Element))
        {
            var name = e is NameReference n ? n.SimpleName : "union_arg" + inputs.Count;
            inputs.Add((CompileTabular(e), name));
        }

        var withSource = ParameterValue(op.Parameters, "withsource") ?? ParameterValue(op.Parameters, "with_source");
        var outCols = Columns(output);
        var outSchema = RowSchema.Emit(outCols);

        var projected = inputs.Select(input =>
            (PlanNode)Select(input.Plan, outCols, (compiler, row) => output.Columns.Select(c =>
                c.Name == withSource ? LExpr.Constant(input.Name, typeof(string))
                : input.Plan.Schema.Find(c.Name) is { } col ? ExpressionHelpers.Coerce(compiler.Read(col, row), KqlTypes.ClrTypeFor(c.Type))
                : (LExpr)LExpr.Constant(null, KqlTypes.ClrTypeFor(c.Type))).ToList(), "union")).ToList();

        return new CombineNode(projected, outSchema, queries =>
        {
            var sameProvider = queries.All(q => q.Provider == queries[0].Provider) && !ExpressionHelpers.IsInMemory(queries[0]);
            if (sameProvider)
            {
                var acc = queries[0];
                foreach (var q in queries.Skip(1))
                    acc = ExpressionHelpers.CallQueryable(acc, "Concat", [outSchema.RowType], q.Expression);
                return acc;
            }
            var all = queries.SelectMany(q => q.Cast<object>());
            return ExpressionHelpers.ToClientQuery(all.CastTo(outSchema.RowType), outSchema.RowType);
        });
    }
}
