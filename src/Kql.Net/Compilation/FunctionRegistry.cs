using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace Kql.Net;

/// <summary>
/// The scalar functions available to a <see cref="KqlContext"/>: the built-in KQL functions plus any custom ones
/// registered on that context. Custom functions are declared to the KQL parser too, so new names type-check like
/// built-ins; registering a built-in name replaces its implementation for this context only.
/// </summary>
public sealed class FunctionRegistry
{
    private sealed record Implementation(MethodInfo Method, object? Target);

    private static readonly Lazy<Dictionary<string, List<Implementation>>> BuiltIns = new(() =>
    {
        var map = new Dictionary<string, List<Implementation>>(StringComparer.Ordinal);
        Add(map, typeof(KqlFunctions));
        return map;
    });

    private readonly Dictionary<string, List<Implementation>> _custom = new(StringComparer.Ordinal);

    /// <summary>Incremented on every registration (used to refresh the parser's view of the functions).</summary>
    internal int Version { get; private set; }

    /// <summary>Names of the custom functions registered on this registry.</summary>
    public IReadOnlyCollection<string> CustomFunctionNames => _custom.Keys;

    /// <summary>Registers every <see cref="KqlFunctionAttribute"/>-marked public static method of <paramref name="type"/>.</summary>
    public FunctionRegistry Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Add(_custom, type);
        Version++;
        return this;
    }

    /// <summary>
    /// Registers a delegate as a KQL function, e.g. <c>Register("double_it", (long? x) =&gt; x * 2)</c>.
    /// Use nullable value types and <c>string</c>/<c>object</c> parameters (KQL values can be null).
    /// </summary>
    public FunctionRegistry Register(string name, Delegate implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(implementation);
        AddOne(_custom, name, new Implementation(implementation.Method, implementation.Target));
        Version++;
        return this;
    }

    private static void Add(Dictionary<string, List<Implementation>> map, Type type)
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            foreach (var attr in m.GetCustomAttributes<KqlFunctionAttribute>())
                AddOne(map, attr.Name, new Implementation(m, null));
    }

    private static void AddOne(Dictionary<string, List<Implementation>> map, string name, Implementation impl)
    {
        if (!map.TryGetValue(name, out var list)) map[name] = list = [];
        list.Insert(0, impl); // later registrations win ties
    }

    /// <summary>KQL symbols for custom functions whose names the parser doesn't already know.</summary>
    internal IReadOnlyList<FunctionSymbol> CustomSymbols(GlobalState globals)
    {
        var symbols = new List<FunctionSymbol>();
        foreach (var (name, impls) in _custom)
        {
            if (globals.GetFunction(name) is not null || globals.GetAggregate(name) is not null) continue;
            var signatures = impls.Select(i => ToSignature(i.Method)).ToArray();
            symbols.Add(new FunctionSymbol(name, signatures));
        }
        return symbols;
    }

    private static Signature ToSignature(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p =>
            {
                var isParams = p.IsDefined(typeof(ParamArrayAttribute));
                var type = isParams ? p.ParameterType.GetElementType()! : p.ParameterType;
                var kql = type == typeof(object) ? null : KqlTypes.FromClr(type).KqlType;
                return kql is null
                    ? new Parameter(p.Name ?? "arg", ParameterTypeKind.Scalar, minOccurring: isParams ? 0 : 1, maxOccurring: isParams ? 64 : 1)
                    : new Parameter(p.Name ?? "arg", kql, minOccurring: isParams ? 0 : 1, maxOccurring: isParams ? 64 : 1);
            }).ToArray();
        return new Signature(KqlTypes.FromClr(method.ReturnType).KqlType, parameters);
    }

    /// <summary>Binds a call to the best-matching implementation (custom implementations first), or null if none.</summary>
    internal Expression? TryBind(string name, IReadOnlyList<Expression> args)
    {
        var candidates = (_custom.TryGetValue(name, out var custom) ? custom : [])
            .Concat(BuiltIns.Value.TryGetValue(name, out var builtIn) ? builtIn : []);

        Implementation? best = null;
        var bestScore = int.MaxValue;
        foreach (var impl in candidates)
        {
            var score = Score(impl.Method, args);
            if (score < bestScore)
            {
                best = impl;
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
        if (at == param || KqlTypes.MakeNullable(at) == param || KqlTypes.MakeNullable(param) == at) return 0;
        if (arg is ConstantExpression { Value: null }) return 1;
        if (KqlTypes.IsNumeric(at) && KqlTypes.IsNumeric(param))
            return KqlTypes.WidestNumeric(at, param) == KqlTypes.MakeNullable(param) ? 2 : 20;
        if (param == typeof(object)) return 5;
        if (at == typeof(object)) return 8;
        return 50;
    }

    private static Expression BuildCall(Implementation impl, IReadOnlyList<Expression> args)
    {
        var m = impl.Method;
        var ps = m.GetParameters();
        var isParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute));
        var fixedCount = isParams ? ps.Length - 1 : ps.Length;
        var callArgs = new List<Expression>();
        for (var i = 0; i < fixedCount; i++) callArgs.Add(Coerce(args[i], ps[i].ParameterType));
        if (isParams)
        {
            var et = ps[^1].ParameterType.GetElementType()!;
            callArgs.Add(Expression.NewArrayInit(et, args.Skip(fixedCount).Select(a => Coerce(a, et))));
        }
        return m.IsStatic
            ? Expression.Call(m, callArgs)
            : Expression.Call(Expression.Constant(impl.Target, m.DeclaringType!), m, callArgs);
    }

    private static Expression Coerce(Expression e, Type target)
    {
        // non-nullable parameters (e.g. a lambda taking `long`) receive the value or default
        if (target.IsValueType && Nullable.GetUnderlyingType(target) is null && e.Type == KqlTypes.MakeNullable(target))
            return Expression.Call(e, e.Type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!);
        return Compilation.ExpressionHelpers.Coerce(e, target);
    }
}
