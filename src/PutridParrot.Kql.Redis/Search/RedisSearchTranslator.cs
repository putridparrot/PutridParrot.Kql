using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace PutridParrot.Kql.Redis.Search;

/// <summary>
/// Lets <c>where</c> predicates that use KQL string operators (<c>=~</c>, <c>in~</c>, <c>has</c>, <c>has_any</c>,
/// <c>has_all</c>) stay on a RediSearch table so they can be pushed into FT.SEARCH. The operators become
/// <see cref="RedisSearchMarkers.Match"/> calls, which are approximate (superset) translations: PutridParrot.Kql re-applies the
/// exact KQL predicate to the rows that come back.
/// </summary>
public sealed class RedisSearchTranslator : IKqlQueryTranslator
{
    public static RedisSearchTranslator Instance { get; } = new();

    private static readonly HashSet<KqlStringOperator> Supported =
    [
        KqlStringOperator.EqualsIgnoreCase, KqlStringOperator.InIgnoreCase,
        KqlStringOperator.Has, KqlStringOperator.HasAny, KqlStringOperator.HasAll
    ];

    public bool CanTranslate(IQueryable query) => query.Provider is RedisSearchProvider;

    public KqlTranslation? TranslateStringOperator(KqlStringOperator op, bool caseSensitive, Expression left, Expression right) =>
        Supported.Contains(op)
            ? new KqlTranslation(Expression.Call(RedisSearchMarkers.MatchMethod, Expression.Constant(op), Expression.Constant(caseSensitive),
                left, Expression.Convert(right, typeof(object))), IsApproximate: true)
            : null;
}

/// <summary>Placeholder calls recognised by the RediSearch query provider.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RedisSearchMarkers
{
    internal static readonly MethodInfo MatchMethod = typeof(RedisSearchMarkers).GetMethod(nameof(Match))!;

    /// <summary>
    /// A string operator to push into FT.SEARCH where possible. Evaluated in memory it is always true: it only ever
    /// appears as a superset filter that is followed by the exact predicate.
    /// </summary>
    public static bool Match(KqlStringOperator op, bool caseSensitive, string? left, object? right) => true;
}
