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
            case "make_bag":
                return Helper(nameof(KqlAggregates.MakeBag), g, args[0], MaxSize(args, 1));
            default:
                throw new KqlNotSupportedException($"aggregate {name}()", fc.TextStart);
        }
    }

    /// <summary>Compiles <paramref name="body"/> as a lambda over a single row of the group.</summary>
    private LambdaExpression RowLambda(KExpr body, Func<ScalarCompiler, KExpr, LExpr> compile)
    {
        var p = LExpr.Parameter(_group!.RowSchema.RowType, "r");
        var inner = new ScalarCompiler(_query, _group.RowSchema, p);
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
