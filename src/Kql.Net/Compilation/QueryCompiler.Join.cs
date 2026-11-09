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
        var values = keys.Select(k => k is NameReference n && side.Schema.Find(n.SimpleName) is { } col ? col.Read(p) : compiler.Compile(k)).ToList();
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

        // output column sources
        var outSchema = RowSchema.Emit(Columns(output));
        var l = LExpr.Parameter(left.Schema.RowType, "l");
        var hasL = LExpr.Parameter(typeof(bool), "hasL");
        var r = LExpr.Parameter(right.Schema.RowType, "r");
        var hasR = LExpr.Parameter(typeof(bool), "hasR");

        LExpr Read(RowColumn col, ParameterExpression row, ParameterExpression has) =>
            LExpr.Condition(has, col.Read(row), LExpr.Constant(null, col.ClrType));

        var values = new List<LExpr>();
        var leftCount = left.Schema.Columns.Count;
        switch (joinKind)
        {
            case JoinKind.LeftAnti or JoinKind.LeftSemi:
                values.AddRange(output.Columns.Select(c => Read(left.Schema.Get(c.Name), l, hasL)));
                break;
            case JoinKind.RightAnti or JoinKind.RightSemi:
                values.AddRange(output.Columns.Select(c => Read(right.Schema.Get(c.Name), r, hasR)));
                break;
            default:
                if (lookup)
                {
                    values.AddRange(output.Columns.Select((c, i) => i < leftCount
                        ? Read(left.Schema.Columns[i], l, hasL)
                        : Read(right.Schema.Get(c.Name), r, hasR)));
                }
                else
                {
                    if (output.Columns.Count != leftCount + right.Schema.Columns.Count)
                        throw new KqlNotSupportedException("join output shape", position);
                    values.AddRange(output.Columns.Select((_, i) => i < leftCount
                        ? Read(left.Schema.Columns[i], l, hasL)
                        : Read(right.Schema.Columns[i - leftCount], r, hasR)));
                }
                break;
        }
        var resultSelector = LExpr.Lambda(outSchema.New(values), l, hasL, r, hasR).Compile();

        var method = typeof(JoinHelper).GetMethod(nameof(JoinHelper.Join))!
            .MakeGenericMethod(left.Schema.RowType, right.Schema.RowType, keySchema.RowType, outSchema.RowType);
        var lk = leftKey.Compile();
        var rk = rightKey.Compile();

        return new CombineNode([left, right], outSchema, inputs =>
        {
            var result = (System.Collections.IEnumerable)method.Invoke(null, [inputs[0], inputs[1], lk, rk, resultSelector, joinKind])!;
            return ExpressionHelpers.ToClientQuery(result, outSchema.RowType);
        });
    }

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
