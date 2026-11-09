using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;
using Kql.Net.Schema;

namespace Kql.Net.Compilation;

/// <summary>
/// Resolves KQL scalar function names to CLR implementations (static methods marked with
/// <see cref="KqlFunctionAttribute"/>), choosing the overload that best fits the argument types.
/// Additional function libraries can be registered with <see cref="Register(Type)"/>.
/// </summary>
public static class FunctionRegistry
{
    private static readonly Dictionary<string, List<MethodInfo>> Functions = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    static FunctionRegistry() => Register(typeof(KqlFunctions));

    /// <summary>Registers every <see cref="KqlFunctionAttribute"/>-marked static method of a type.</summary>
    public static void Register(Type type)
    {
        lock (Gate)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (var attr in m.GetCustomAttributes<KqlFunctionAttribute>())
                {
                    if (!Functions.TryGetValue(attr.Name, out var list)) Functions[attr.Name] = list = [];
                    list.Insert(0, m); // later registrations win ties
                }
            }
        }
    }

    public static bool IsDefined(string name)
    {
        lock (Gate) return Functions.ContainsKey(name);
    }

    internal static Expression? TryBind(string name, IReadOnlyList<Expression> args)
    {
        List<MethodInfo> candidates;
        lock (Gate)
        {
            if (!Functions.TryGetValue(name, out var list)) return null;
            candidates = list.ToList();
        }

        MethodInfo? best = null;
        var bestScore = int.MaxValue;
        foreach (var m in candidates)
        {
            var score = Score(m, args);
            if (score < bestScore)
            {
                best = m;
                bestScore = score;
            }
        }
        return best is null ? null : BuildCall(best, args);
    }

    private static int Score(MethodInfo m, IReadOnlyList<Expression> args)
    {
        var ps = m.GetParameters();
        var isParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute));
        var fixedCount = isParams ? ps.Length - 1 : ps.Length;
        if (isParams ? args.Count < fixedCount : args.Count != ps.Length) return int.MaxValue;

        var score = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var pt = i < fixedCount ? ps[i].ParameterType : ps[^1].ParameterType.GetElementType()!;
            score += ArgScore(args[i], pt);
        }
        return score;
    }

    private static int ArgScore(Expression arg, Type param)
    {
        var at = arg.Type;
        if (at == param || KqlTypes.MakeNullable(at) == param) return 0;
        if (arg is ConstantExpression { Value: null }) return 1;
        if (KqlTypes.IsNumeric(at) && KqlTypes.IsNumeric(param))
            return KqlTypes.WidestNumeric(at, param) == KqlTypes.MakeNullable(param) ? 2 : 20;
        if (param == typeof(object)) return 5;
        if (at == typeof(object)) return 8;
        return 50;
    }

    private static Expression BuildCall(MethodInfo m, IReadOnlyList<Expression> args)
    {
        var ps = m.GetParameters();
        var isParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute));
        var fixedCount = isParams ? ps.Length - 1 : ps.Length;
        var callArgs = new List<Expression>();
        for (var i = 0; i < fixedCount; i++) callArgs.Add(ExpressionHelpers.Coerce(args[i], ps[i].ParameterType));
        if (isParams)
        {
            var et = ps[^1].ParameterType.GetElementType()!;
            callArgs.Add(Expression.NewArrayInit(et, args.Skip(fixedCount).Select(a => ExpressionHelpers.Coerce(a, et))));
        }
        return Expression.Call(m, callArgs);
    }
}
