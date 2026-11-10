using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace PutridParrot.Kql.Compilation;

/// <summary>
/// Runs in-memory (<see cref="EnumerableQuery"/>) queries through a compiled delegate that is cached between executions.
/// <para>
/// <see cref="EnumerableQuery{T}"/> compiles its expression tree every time a new query is enumerated, which costs
/// milliseconds and dominates queries over small tables. Here the tree is rewritten from <see cref="Queryable"/> to
/// <see cref="Enumerable"/> calls with its object constants (the data sources, compiled delegates...) turned into
/// parameters, so a plan executed again with fresh sources reuses the delegate compiled the first time.
/// </para>
/// </summary>
internal sealed class ExecutorSlot
{
    private sealed record Entry(string Shape, (int Index, object Value)[] Inlined, Func<object?[], IEnumerable> Run);

    private Entry? _entry;

    /// <summary>
    /// The rows of <paramref name="query"/>, enumerated through the cached delegate when the query is an in-memory
    /// expression tree (otherwise the query itself).
    /// </summary>
    public IEnumerable Bind(IQueryable query)
    {
        if (query is not EnumerableQuery || query.Expression is ConstantExpression || !typeof(IEnumerable).IsAssignableFrom(query.Expression.Type))
            return query;

        ClientExecution.Prepared prepared;
        var entry = Volatile.Read(ref _entry);
        try
        {
            prepared = ClientExecution.Prepare(query.Expression);
            if (entry is null || entry.Shape != prepared.Shape || !entry.Inlined.All(x => Equals(prepared.Values[x.Index], x.Value)))
            {
                entry = new Entry(prepared.Shape, prepared.Inlinable.Select(i => (i, prepared.Values[i]!)).ToArray(), prepared.Compile());
                Volatile.Write(ref _entry, entry);
            }
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return query; // a shape the rewrite doesn't handle: let EnumerableQuery run it
        }
        return entry.Run(prepared.Values);
    }
}

internal static class ClientExecution
{
    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo> EnumerableEquivalents = new();

    internal sealed class Prepared(Expression body, ParameterExpression values, object?[] constants, List<int> inlinable, string shape)
    {
        public string Shape { get; } = shape;
        public object?[] Values { get; } = constants;
        public List<int> Inlinable { get; } = inlinable;

        /// <summary>Compiles the body, with scalar constants (numbers, strings, dates...) put back inline.</summary>
        public Func<object?[], IEnumerable> Compile()
        {
            var inline = new Inliner(values, Values, Inlinable.ToHashSet()).Visit(body);
            return Expression.Lambda<Func<object?[], IEnumerable>>(Expression.Convert(inline, typeof(IEnumerable)), values).Compile();
        }
    }

    /// <summary>Rewrites a Queryable tree into a parameterized Enumerable tree, collecting its constants.</summary>
    public static Prepared Prepare(Expression expression)
    {
        var values = Expression.Parameter(typeof(object[]), "values");
        var rewriter = new Rewriter(values);
        var body = rewriter.Visit(expression);
        if (!typeof(IEnumerable).IsAssignableFrom(body.Type)) throw new NotSupportedException();
        return new Prepared(body, values, rewriter.Constants.ToArray(), rewriter.Inlinable, body.ToString());
    }

    private static bool IsInlinable(object value)
    {
        var t = value.GetType();
        return t.IsPrimitive || t.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid;
    }

    private sealed class Rewriter(ParameterExpression values) : ExpressionVisitor
    {
        public List<object?> Constants { get; } = [];
        public List<int> Inlinable { get; } = [];

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is null) return node;
            var value = node.Value;
            var type = node.Type;
            // a nested in-memory query over a plain sequence: pass the sequence itself
            if (value is EnumerableQuery && value is IQueryable q)
            {
                if (q.Expression is not ConstantExpression c || !ReferenceEquals(c.Value, q)) throw new NotSupportedException();
                type = typeof(IEnumerable<>).MakeGenericType(q.ElementType);
            }
            else if (typeof(IQueryable).IsAssignableFrom(type))
            {
                throw new NotSupportedException(); // a provider-backed query: leave it to EnumerableQuery
            }
            if (IsInlinable(value)) Inlinable.Add(Constants.Count);
            Constants.Add(value);
            return Expression.Convert(Expression.ArrayIndex(values, Expression.Constant(Constants.Count - 1)), type);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var obj = Visit(node.Object);
            var args = node.Arguments.Select(a => Visit(StripQuote(a))).ToArray();
            var method = node.Method;
            if (method.DeclaringType == typeof(Queryable)) method = ToEnumerable(method);
            return Expression.Call(obj, method, args);
        }

        private static Expression StripQuote(Expression e) => e is UnaryExpression { NodeType: ExpressionType.Quote } u ? u.Operand : e;

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            // parameter types may change (IQueryable -> IEnumerable) only for Queryable arguments, which aren't lambdas
            var body = Visit(node.Body);
            return body == node.Body ? node : Expression.Lambda(node.Type, body, node.Name, node.TailCall, node.Parameters);
        }
    }

    private sealed class Inliner(ParameterExpression values, object?[] constants, HashSet<int> inline) : ExpressionVisitor
    {
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node is { NodeType: ExpressionType.Convert, Operand: BinaryExpression { NodeType: ExpressionType.ArrayIndex } index }
                && index.Left == values && index.Right is ConstantExpression { Value: int i } && inline.Contains(i))
                return Expression.Constant(constants[i], node.Type);
            return base.VisitUnary(node);
        }
    }

    // ------------------------------------------------------------------ Queryable -> Enumerable

    private static MethodInfo ToEnumerable(MethodInfo queryable)
    {
        var definition = queryable.IsGenericMethod ? queryable.GetGenericMethodDefinition() : queryable;
        var match = EnumerableEquivalents.GetOrAdd(definition, FindEquivalent);
        return queryable.IsGenericMethod ? match.MakeGenericMethod(queryable.GetGenericArguments()) : match;
    }

    private static MethodInfo FindEquivalent(MethodInfo queryable)
    {
        var wanted = queryable.GetParameters().Select(p => MapType(p.ParameterType)).ToArray();
        var arity = queryable.IsGenericMethod ? queryable.GetGenericArguments().Length : 0;
        foreach (var m in typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != queryable.Name || (m.IsGenericMethod ? m.GetGenericArguments().Length : 0) != arity) continue;
            var ps = m.GetParameters();
            if (ps.Length == wanted.Length && ps.Select((p, i) => Same(p.ParameterType, wanted[i])).All(x => x)) return m;
        }
        throw new NotSupportedException($"No Enumerable equivalent of Queryable.{queryable.Name}.");
    }

    /// <summary>The Enumerable form of a Queryable parameter type (IQueryable -> IEnumerable, Expression&lt;F&gt; -> F).</summary>
    private static Type MapType(Type t)
    {
        if (t == typeof(IQueryable)) return typeof(IEnumerable);
        if (!t.IsGenericType) return t;
        var def = t.GetGenericTypeDefinition();
        var args = t.GetGenericArguments();
        if (def == typeof(IQueryable<>)) return typeof(IEnumerable<>).MakeGenericType(args);
        if (def == typeof(IOrderedQueryable<>)) return typeof(IOrderedEnumerable<>).MakeGenericType(args);
        if (def == typeof(Expression<>)) return args[0];
        return t;
    }

    private static bool Same(Type a, Type b)
    {
        if (a.IsGenericParameter || b.IsGenericParameter)
            return a.IsGenericParameter && b.IsGenericParameter && a.GenericParameterPosition == b.GenericParameterPosition;
        if (a.IsArray || b.IsArray) return a.IsArray && b.IsArray && Same(a.GetElementType()!, b.GetElementType()!);
        if (a.IsGenericType != b.IsGenericType) return false;
        if (!a.IsGenericType) return a == b;
        if (a.GetGenericTypeDefinition() != b.GetGenericTypeDefinition()) return false;
        var aa = a.GetGenericArguments();
        var ba = b.GetGenericArguments();
        return aa.Length == ba.Length && aa.Zip(ba).All(p => Same(p.First, p.Second));
    }
}
