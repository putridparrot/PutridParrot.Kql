using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;

namespace Kql.Net.Redis.Search;

/// <summary>A query over a RediSearch table. Enumerating it runs FT.SEARCH (see <see cref="RedisSearchProvider"/>).</summary>
internal sealed class RedisSearchQueryable<T> : IOrderedQueryable<T>
{
    public RedisSearchQueryable(RedisSearchProvider provider)
    {
        Provider = provider;
        Expression = Expression.Constant(this);
    }

    public RedisSearchQueryable(RedisSearchProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    internal RedisSearchProvider SearchProvider => (RedisSearchProvider)Provider;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)SearchProvider.Run(Expression)!).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Runs LINQ queries over a RediSearch index. The leading operators are pushed into one FT.SEARCH command:
/// <list type="bullet">
/// <item><c>where</c> predicates (NUMERIC ranges, TAG matches, and TEXT terms when enabled), through <c>project</c>/<c>extend</c>;</item>
/// <item>when the filter is exact: <c>take</c> (LIMIT), <c>sort by numeric | take</c> (SORTBY + LIMIT) and <c>count</c>.</item>
/// </list>
/// The whole query (pushed operators included) then runs in memory over the documents that come back, so a pushed
/// filter only has to be a superset of the result: KQL semantics are always those of the in-memory evaluation.
/// </summary>
internal sealed class RedisSearchProvider(RedisSearchTableSource source) : IQueryProvider
{
    public RedisSearchTableSource Source { get; } = source;

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = ElementTypeOf(expression.Type);
        return (IQueryable)Activator.CreateInstance(typeof(RedisSearchQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new RedisSearchQueryable<TElement>(this, expression);

    public object? Execute(Expression expression) => Run(expression);

    public TResult Execute<TResult>(Expression expression) => (TResult)Run(expression)!;

    /// <summary>Plans, fetches and evaluates a query (a sequence or a scalar such as <c>LongCount</c>).</summary>
    internal object? Run(Expression expression)
    {
        var plan = SearchPlan.Create(expression, Source);
        if (plan.CountOnly) return Convert.ChangeType(Source.Count(plan.Filter), expression.Type);

        var rows = Source.Fetch(plan);
        var rowsQuery = (IQueryable)Activator.CreateInstance(typeof(EnumerableQuery<>).MakeGenericType(Source.ElementType), rows)!;
        var inMemory = new InMemoryRewriter(rowsQuery).Visit(expression);
        var provider = (IQueryProvider)rowsQuery;
        return typeof(IQueryable).IsAssignableFrom(expression.Type)
            ? provider.CreateQuery(inMemory)
            : provider.Execute(inMemory);
    }

    private static Type ElementTypeOf(Type sequenceType) =>
        sequenceType.GetInterfaces().Append(sequenceType)
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];

    /// <summary>
    /// Points the query at the fetched rows and restores KQL ordering: sorts sent to a provider carry no comparer, so
    /// in memory they get Kql.Net's (ordinal strings, nulls lowest).
    /// </summary>
    private sealed class InMemoryRewriter(IQueryable rows) : ExpressionVisitor
    {
        private static readonly MethodInfo ComparerFor = typeof(KqlComparer).GetMethod(nameof(KqlComparer.For))!;

        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Value is IQueryable { Provider: RedisSearchProvider } ? Expression.Constant(rows) : node;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType == typeof(Queryable) && visited.Arguments.Count == 2
                && visited.Method.Name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                var keyType = visited.Method.GetGenericArguments()[1];
                var withComparer = typeof(Queryable).GetMethods()
                    .Single(m => m.Name == visited.Method.Name && m.GetParameters().Length == 3)
                    .MakeGenericMethod(visited.Method.GetGenericArguments());
                var comparer = Expression.Constant(ComparerFor.MakeGenericMethod(keyType).Invoke(null, null),
                    typeof(IComparer<>).MakeGenericType(keyType));
                return Expression.Call(withComparer, visited.Arguments[0], visited.Arguments[1], comparer);
            }
            return visited;
        }
    }
}

/// <summary>What a query pushes into FT.SEARCH.</summary>
internal sealed class SearchPlan
{
    public SearchFilter Filter { get; private set; } = SearchFilter.All;

    /// <summary>LIMIT for the fetch (only when the filter is exact).</summary>
    public int? Limit { get; private set; }

    /// <summary>SORTBY attribute (NUMERIC) and direction, together with <see cref="Limit"/>.</summary>
    public (RedisSearchField Field, bool Descending)? Sort { get; private set; }

    /// <summary>The query is <c>Count</c>/<c>LongCount</c> of an exactly-filtered table: FT.SEARCH's total answers it.</summary>
    public bool CountOnly { get; private set; }

    public static SearchPlan Create(Expression expression, RedisSearchTableSource source)
    {
        var plan = new SearchPlan();

        // the operator chain, innermost (applied to the table) first
        var chain = new List<MethodCallExpression>();
        for (var e = expression; e is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable); e = call.Arguments[0])
            chain.Insert(0, call);
        if (chain.Count == 0 || chain[0].Arguments[0] is not ConstantExpression { Value: IQueryable { Provider: RedisSearchProvider } })
            return plan;

        // columns visible at the current stage: member name -> index attribute (followed through projections)
        var columns = source.FieldsByMember.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var translator = new SearchFilterTranslator(source.Index, source.Options.PushDownText);

        for (var i = 0; i < chain.Count; i++)
        {
            var call = chain[i];
            switch (call.Method.Name)
            {
                case "Where" when Lambda(call) is { Parameters.Count: 1 } where:
                {
                    var p = where.Parameters[0];
                    var visible = columns;
                    translator.Column = e => e is MemberExpression m && m.Expression == p && visible.TryGetValue(m.Member.Name, out var f) ? f : null;
                    var filter = translator.Translate(where.Body) ?? SearchFilter.All with { Exact = false };
                    plan.Filter = SearchFilter.And(plan.Filter, filter);
                    continue;
                }
                case "Select" when Lambda(call) is { Parameters.Count: 1 } select:
                {
                    // follow columns through row-building projections (project / extend)
                    var p = select.Parameters[0];
                    var next = new Dictionary<string, RedisSearchField>(StringComparer.Ordinal);
                    if (select.Body is NewExpression { Members: { } members } ne)
                    {
                        for (var a = 0; a < ne.Arguments.Count; a++)
                            if (Source(ne.Arguments[a], p, columns) is { } f) next[members[a].Name] = f;
                    }
                    else if (select.Body is MemberInitExpression mi)
                    {
                        foreach (var b in mi.Bindings.OfType<MemberAssignment>())
                            if (Source(b.Expression, p, columns) is { } f) next[b.Member.Name] = f;
                    }
                    else break;
                    columns = next;
                    continue;
                }
                case "Take" when plan.Filter.Exact && call.Arguments[1] is ConstantExpression { Value: int n }:
                    plan.Limit = Math.Max(n, 0);
                    break;
                case "OrderBy" or "OrderByDescending" when plan.Filter.Exact && call.Arguments.Count == 2
                    && i + 1 < chain.Count && chain[i + 1] is { Method.Name: "Take", Arguments: [_, ConstantExpression { Value: int top }] }
                    && Lambda(call) is { Parameters.Count: 1 } key:
                {
                    // top-N on a NUMERIC attribute (a single key: ties on further keys can't be resolved by Redis)
                    var p = key.Parameters[0];
                    if (key.Body is MemberExpression m && m.Expression == p && columns.TryGetValue(m.Member.Name, out var f)
                        && f.Type == RedisSearchFieldType.Numeric)
                    {
                        plan.Sort = (f, call.Method.Name == "OrderByDescending");
                        plan.Limit = Math.Max(top, 0);
                    }
                    break;
                }
                case "Count" or "LongCount" when call.Arguments.Count == 1 && i == chain.Count - 1 && plan.Filter.Exact:
                    plan.CountOnly = true;
                    break;
            }
            break; // any other operator: the rest runs in memory only
        }
        return plan;
    }

    private static LambdaExpression? Lambda(MethodCallExpression call) =>
        call.Arguments.Count > 1 && StripQuote(call.Arguments[1]) is LambdaExpression l ? l : null;

    private static Expression StripQuote(Expression e) => e is UnaryExpression { NodeType: ExpressionType.Quote } u ? u.Operand : e;

    private static RedisSearchField? Source(Expression e, ParameterExpression p, Dictionary<string, RedisSearchField> columns)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert } u && Nullable.GetUnderlyingType(u.Type) == u.Operand.Type) e = u.Operand;
        return e is MemberExpression m && m.Expression == p && columns.TryGetValue(m.Member.Name, out var f) ? f : null;
    }
}
