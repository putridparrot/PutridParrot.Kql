using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

internal sealed partial class ScalarCompiler
{
    private static readonly MethodInfo[] EnumerableMethods = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static);

    private LExpr Aggregate(FunctionCallExpression fc, string name)
    {
        if (_group is null)
            throw new KqlException($"Aggregate function {name}() can only be used in summarize (position {fc.TextStart}).");

        var args = fc.ArgumentList.Expressions.Select(e => e.Element).ToList();
        var rowType = _group.RowSchema.RowType;
        var g = _group.Group;
        var resultType = KqlTypes.ClrTypeFor(fc.ResultType);

        switch (name)
        {
            case "count":
                return args.Count == 0 ? Enum("LongCount", [rowType], g) : Enum("LongCount", [rowType], g, Predicate(args[0]));
            case "countif":
                return Enum("LongCount", [rowType], g, Predicate(args[0]));
            case "sum":
                return Sum(g, args[0], resultType);
            case "sumif":
                return Sum(Where(g, args[1]), args[0], resultType);
            case "avg":
                return Avg(g, args[0]);
            case "avgif":
                return Avg(Where(g, args[1]), args[0]);
            case "min":
                return MinMax("Min", g, args[0]);
            case "max":
                return MinMax("Max", g, args[0]);
            case "minif":
                return MinMax("Min", Where(g, args[1]), args[0]);
            case "maxif":
                return MinMax("Max", Where(g, args[1]), args[0]);
            case "dcount":
            case "count_distinct":
                return DistinctCount(g, args[0]);
            case "dcountif":
            case "count_distinctif":
                return DistinctCount(Where(g, args[1]), args[0]);
            case "take_any":
            case "any":
                return TakeAny(g, args[0]);
            case "take_anyif":
                return TakeAny(Where(g, args[1]), args[0]);
            case "make_list":
            case "makelist":
                return Helper(nameof(KqlAggregates.MakeList), g, args[0], MaxSize(args, 1));
            case "make_list_if":
                return Helper(nameof(KqlAggregates.MakeList), Where(g, args[1]), args[0], MaxSize(args, 2));
            case "make_set":
            case "makeset":
                return Helper(nameof(KqlAggregates.MakeSet), g, args[0], MaxSize(args, 1));
            case "make_set_if":
                return Helper(nameof(KqlAggregates.MakeSet), Where(g, args[1]), args[0], MaxSize(args, 2));
            case "make_list_with_nulls":
                return Helper(nameof(KqlAggregates.MakeListWithNulls), g, args[0], MaxSize(args, 1));
            case "stdev":
                return Helper(nameof(KqlAggregates.Stdev), g, args[0]);
            case "stdevp":
                return Helper(nameof(KqlAggregates.StdevP), g, args[0]);
            case "variance":
                return Helper(nameof(KqlAggregates.Variance), g, args[0]);
            case "variancep":
                return Helper(nameof(KqlAggregates.VarianceP), g, args[0]);
            case "percentile":
                return Helper(nameof(KqlAggregates.Percentile), g, args[0], Compile(args[1]));
            case "arg_max":
            case "arg_min":
                // the first output column is the extreme value itself; Summarize adds the other columns
                return ArgValue(args[0], name == "arg_max", (c, _) => c.Compile(args[0]));
            case "make_bag":
                return Helper(nameof(KqlAggregates.MakeBag), g, args[0], MaxSize(args, 1));
            case "make_bag_if":
                return Helper(nameof(KqlAggregates.MakeBag), Where(g, args[1]), args[0], MaxSize(args, 2));
            case "stdevif":
                return Helper(nameof(KqlAggregates.Stdev), Where(g, args[1]), args[0]);
            case "varianceif":
                return Helper(nameof(KqlAggregates.Variance), Where(g, args[1]), args[0]);
            case "variancepif":
                return Helper(nameof(KqlAggregates.VarianceP), Where(g, args[1]), args[0]);
            case "percentiles":
                // one column per percentile; Summarize adds the others (PercentilesExtra)
                return Helper(nameof(KqlAggregates.Percentile), g, args[0], Compile(args[1]));
            case "percentiles_array":
                return Helper(nameof(KqlAggregates.PercentilesArray), g, args[0], Percents(args.Skip(1)));
            case "percentilew":
            case "percentilesw":
                return Rows(nameof(KqlAggregates.WeightedPercentile), g, [args[0], args[1]], Compile(args[2]));
            case "percentilesw_array":
                return Rows(nameof(KqlAggregates.WeightedPercentilesArray), g, [args[0], args[1]], Percents(args.Skip(2)));
            case "covariance":
                return Rows(nameof(KqlAggregates.Covariance), g, [args[0], args[1]], LExpr.Constant(true));
            case "covariancep":
                return Rows(nameof(KqlAggregates.Covariance), g, [args[0], args[1]], LExpr.Constant(false));
            case "covarianceif":
                return Rows(nameof(KqlAggregates.Covariance), Where(g, args[2]), [args[0], args[1]], LExpr.Constant(true));
            case "covariancepif":
                return Rows(nameof(KqlAggregates.Covariance), Where(g, args[2]), [args[0], args[1]], LExpr.Constant(false));
            case "binary_all_and":
                return Helper(nameof(KqlAggregates.BinaryAllAnd), g, args[0]);
            case "binary_all_or":
                return Helper(nameof(KqlAggregates.BinaryAllOr), g, args[0]);
            case "binary_all_xor":
                return Helper(nameof(KqlAggregates.BinaryAllXor), g, args[0]);
            case "hll":
                return Helper(nameof(KqlAggregates.Hll), g, args[0]);
            case "hll_if":
                return Helper(nameof(KqlAggregates.Hll), Where(g, args[1]), args[0]);
            case "hll_merge":
                return Helper(nameof(KqlAggregates.HllMerge), g, args[0]);
            case "tdigest":
                return args.Count > 1
                    ? Rows(nameof(KqlAggregates.TdigestWeighted), g, [args[0], args[1]])
                    : Helper(nameof(KqlAggregates.Tdigest), g, args[0]);
            case "tdigest_merge":
            case "merge_tdigest":
                return Helper(nameof(KqlAggregates.TdigestMerge), g, args[0]);
            default:
                throw new KqlNotSupportedException($"aggregate {name}()", fc.TextStart);
        }
    }

    /// <summary>Compiles <paramref name="body"/> as a lambda over a single row of the group.</summary>
    private LambdaExpression RowLambda(KExpr body, Func<ScalarCompiler, KExpr, LExpr> compile)
    {
        var p = LExpr.Parameter(_group!.RowSchema.RowType, "r");
        var inner = new ScalarCompiler(_query, _group!.RowSchema, p, translator: _translator);
        var lambda = LExpr.Lambda(compile(inner, body), p);
        RequiresClient |= inner.RequiresClient;
        return lambda;
    }

    private LambdaExpression Selector(KExpr body, Type? target = null) =>
        RowLambda(body, (c, e) => target is null ? c.Compile(e) : ExpressionHelpers.Coerce(c.Compile(e), target));

    private LambdaExpression Predicate(KExpr body) => RowLambda(body, (c, e) => c.CompilePredicate(e));

    private LExpr Where(LExpr g, KExpr predicate) =>
        Enum("Where", [_group!.RowSchema.RowType], g, Predicate(predicate));

    private static LExpr Enum(string name, Type[] typeArgs, params LExpr[] args)
    {
        var argTypes = args.Select(a => a.Type).ToArray();
        foreach (var m in EnumerableMethods.Where(m => m.Name == name && m.GetGenericArguments().Length == typeArgs.Length && m.GetParameters().Length == args.Length))
        {
            var mi = m.MakeGenericMethod(typeArgs);
            var ps = mi.GetParameters();
            var ok = true;
            for (var i = 0; i < ps.Length && ok; i++)
            {
                ok = args[i] is LambdaExpression le
                    ? ps[i].ParameterType == le.Type
                    : ps[i].ParameterType.IsAssignableFrom(argTypes[i]);
            }
            if (ok) return LExpr.Call(mi, args);
        }
        throw new InvalidOperationException($"No Enumerable.{name} overload for ({string.Join(", ", argTypes.Select(t => t.Name))}).");
    }

    private LExpr Sum(LExpr g, KExpr value, Type resultType)
    {
        // Enumerable.Sum has overloads for long?, double?, decimal?; widen int to long.
        if (resultType == typeof(object))
        {
            // the type isn't known to Kusto (e.g. a column from bag_unpack): use the value's runtime type
            var natural = Selector(value).ReturnType;
            resultType = natural == typeof(int?) || natural == typeof(long?) ? typeof(long?)
                : natural == typeof(decimal?) ? typeof(decimal?) : typeof(double?);
        }
        var target = resultType == typeof(int?) ? typeof(long?) : resultType;
        if (target != typeof(long?) && target != typeof(double?) && target != typeof(decimal?)) target = typeof(double?);
        var selector = Selector(value, target);
        var rowType = _group!.RowSchema.RowType;
        var funcType = typeof(Func<,>).MakeGenericType(rowType, target);
        var sum = EnumerableMethods.Single(m => m.Name == "Sum" && m.GetParameters().Length == 2
                                               && m.GetParameters()[1].ParameterType.IsGenericType
                                               && m.GetParameters()[1].ParameterType.GetGenericArguments()[1] == target)
            .MakeGenericMethod(rowType);
        _ = funcType;
        return ExpressionHelpers.Coerce(LExpr.Call(sum, g, selector), resultType);
    }

    private LExpr Avg(LExpr g, KExpr value)
    {
        var selector = Selector(value, typeof(double?));
        var rowType = _group!.RowSchema.RowType;
        var avg = EnumerableMethods.Single(m => m.Name == "Average" && m.GetParameters().Length == 2
                                               && m.GetParameters()[1].ParameterType.IsGenericType
                                               && m.GetParameters()[1].ParameterType.GetGenericArguments()[1] == typeof(double?))
            .MakeGenericMethod(rowType);
        return LExpr.Call(avg, g, selector);
    }

    private LExpr MinMax(string name, LExpr g, KExpr value)
    {
        var selector = Selector(value);
        var resultType = selector.ReturnType;
        if (resultType == typeof(object))
        {
            RequiresClient = true;
            return LExpr.Call(typeof(KqlAggregates).GetMethod(name == "Min" ? nameof(KqlAggregates.MinObject) : nameof(KqlAggregates.MaxObject))!
                .MakeGenericMethod(_group!.RowSchema.RowType), g, selector);
        }
        return Enum(name, [_group!.RowSchema.RowType, resultType], g, selector);
    }

    private LExpr DistinctCount(LExpr g, KExpr value)
    {
        var selector = Selector(value);
        var rowType = _group!.RowSchema.RowType;
        var selected = Enum("Select", [rowType, selector.ReturnType], g, selector);
        var nonNull = selector.ReturnType.IsValueType && System.Nullable.GetUnderlyingType(selector.ReturnType) is null
            ? selected
            : Enum("Where", [selector.ReturnType], selected, NotNullLambda(selector.ReturnType));
        var distinct = Enum("Distinct", [selector.ReturnType], nonNull);
        return Enum("LongCount", [selector.ReturnType], distinct);
    }

    private static LambdaExpression NotNullLambda(Type t)
    {
        var p = LExpr.Parameter(t, "v");
        return LExpr.Lambda(LExpr.NotEqual(p, LExpr.Constant(null, t)), p);
    }

    private LExpr TakeAny(LExpr g, KExpr value)
    {
        var selector = Selector(value);
        var rowType = _group!.RowSchema.RowType;
        var selected = Enum("Select", [rowType, selector.ReturnType], g, selector);
        return Enum("FirstOrDefault", [selector.ReturnType], selected);
    }

    /// <summary>
    /// Value of <paramref name="select"/> on the row of the group with the max (or min) of <paramref name="by"/>,
    /// ignoring rows where <paramref name="by"/> is null:
    /// <c>g.Where(r =&gt; by(r) != null).OrderByDescending(by).Select(select).FirstOrDefault()</c>.
    /// </summary>
    internal LExpr ArgValue(KExpr by, bool max, Func<ScalarCompiler, ParameterExpression, LExpr> select)
    {
        if (_group is null) throw new KqlException("arg_max/arg_min can only be used in summarize.");
        var rowType = _group.RowSchema.RowType;
        var p = LExpr.Parameter(rowType, "r");
        var inner = new ScalarCompiler(_query, _group!.RowSchema, p, translator: _translator);
        var key = inner.Compile(by);
        var value = Nullable(select(inner, p));
        RequiresClient |= inner.RequiresClient;
        if (key.Type == typeof(object))
            throw new KqlNotSupportedException("arg_max/arg_min over a dynamic value", by.TextStart);

        var filtered = Enum("Where", [rowType], _group.Group, LExpr.Lambda(LExpr.Not(ExpressionHelpers.IsNull(key)), p));
        var ordered = Enum(max ? "OrderByDescending" : "OrderBy", [rowType, key.Type], filtered, LExpr.Lambda(key, p));
        var selected = Enum("Select", [rowType, value.Type], ordered, LExpr.Lambda(value, p));
        return Enum("FirstOrDefault", [value.Type], selected);
    }

    /// <summary>The value of the <paramref name="index"/>-th percentile column of a percentiles()/percentilesw() call.</summary>
    internal LExpr PercentilesExtra(FunctionCallExpression fc, int index)
    {
        var args = fc.ArgumentList.Expressions.Select(e => e.Element).ToList();
        return fc.Name.SimpleName == "percentilesw"
            ? Rows(nameof(KqlAggregates.WeightedPercentile), _group!.Group, [args[0], args[1]], Compile(args[2 + index]))
            : Helper(nameof(KqlAggregates.Percentile), _group!.Group, args[0], Compile(args[1 + index]));
    }

    /// <summary>Percentile arguments (literals or a dynamic array) as an object[] expression.</summary>
    private LExpr Percents(IEnumerable<KExpr> args) =>
        LExpr.NewArrayInit(typeof(object), args.Select(a => ExpressionHelpers.Coerce(Compile(a), typeof(object))));

    /// <summary>Calls a <see cref="KqlAggregates"/> helper over per-row tuples (object[] of several expressions).</summary>
    private LExpr Rows(string method, LExpr g, KExpr[] values, params LExpr[] extra)
    {
        RequiresClient = true;
        var rowType = _group!.RowSchema.RowType;
        var p = LExpr.Parameter(rowType, "r");
        var inner = new ScalarCompiler(_query, _group.RowSchema, p, translator: _translator);
        var tuple = LExpr.Lambda(LExpr.NewArrayInit(typeof(object), values.Select(v => ExpressionHelpers.Coerce(inner.Compile(v), typeof(object)))), p);
        var selected = Enum("Select", [rowType, typeof(object[])], g, tuple);
        var mi = typeof(KqlAggregates).GetMethod(method)!;
        var ps = mi.GetParameters();
        var callArgs = new List<LExpr> { selected };
        for (var i = 0; i < extra.Length; i++) callArgs.Add(ExpressionHelpers.Coerce(extra[i], ps[i + 1].ParameterType));
        return LExpr.Call(mi, callArgs);
    }

    private LExpr MaxSize(List<KExpr> args, int index) =>
        args.Count > index ? ExpressionHelpers.Coerce(Compile(args[index]), typeof(long?)) : LExpr.Constant(null, typeof(long?));

    /// <summary>Calls a <see cref="KqlAggregates"/> helper taking (IEnumerable&lt;object?&gt; values, extra...).</summary>
    private LExpr Helper(string method, LExpr g, KExpr value, params LExpr[] extra)
    {
        RequiresClient = true;
        var selector = Selector(value, typeof(object));
        var rowType = _group!.RowSchema.RowType;
        var selected = Enum("Select", [rowType, typeof(object)], g, selector);
        var mi = typeof(KqlAggregates).GetMethod(method)!;
        var ps = mi.GetParameters();
        var callArgs = new List<LExpr> { selected };
        for (var i = 0; i < extra.Length; i++) callArgs.Add(ExpressionHelpers.Coerce(extra[i], ps[i + 1].ParameterType));
        return LExpr.Call(mi, callArgs);
    }
}
