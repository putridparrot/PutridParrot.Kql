using System.Linq.Expressions;

namespace Kql.Net;

/// <summary>KQL string operators that a <see cref="IKqlQueryTranslator"/> may translate.</summary>
public enum KqlStringOperator
{
    /// <summary><c>=~</c> (case-insensitive equality). Right: string.</summary>
    EqualsIgnoreCase,
    Has,
    HasPrefix,
    HasSuffix,
    Contains,
    StartsWith,
    EndsWith,
    Like,
    MatchesRegex,
    /// <summary><c>has_any</c>. Right: <c>string[]</c> expression (usually a <see cref="NewArrayExpression"/>).</summary>
    HasAny,
    /// <summary><c>has_all</c>. Right: <c>string[]</c> expression.</summary>
    HasAll,
    /// <summary><c>in~</c>. Right: <c>string[]</c> expression.</summary>
    InIgnoreCase,
    /// <summary>Ordinal ordering comparison; the translation must return an <c>int</c> (&lt;0, 0, &gt;0).</summary>
    Compare
}

/// <summary>
/// A translated expression. Set <see cref="IsApproximate"/> when the expression is a superset filter (e.g. <c>has</c>
/// translated as a substring match): Kql.Net then only uses it in a <c>where</c> predicate where widening is safe, and
/// re-applies the exact predicate in memory to the rows that come back.
/// </summary>
public readonly record struct KqlTranslation(Expression Expression, bool IsApproximate = false);

/// <summary>
/// Supplies provider-translatable forms of KQL operators and functions for <see cref="IQueryable"/> sources
/// (e.g. Entity Framework Core). Return <c>null</c> from any method to fall back to in-memory evaluation, in which
/// case the query is executed by the provider up to that operator and the rest runs in .NET.
/// </summary>
public interface IKqlQueryTranslator
{
    /// <summary>Whether this translator applies to the given source query (typically by checking its provider).</summary>
    bool CanTranslate(IQueryable query);

    /// <summary>
    /// Translates a string operator. <paramref name="left"/> and <paramref name="right"/> are string expressions
    /// (or <c>string[]</c> for list operators). The result must be a <c>bool</c> (or <c>int</c> for
    /// <see cref="KqlStringOperator.Compare"/>).
    /// </summary>
    KqlTranslation? TranslateStringOperator(KqlStringOperator op, bool caseSensitive, Expression left, Expression right) => null;

    /// <summary>
    /// Translates a scalar function call. Arguments use Kql.Net's runtime types (nullable value types, string, object).
    /// The result is converted to <paramref name="resultType"/> by the caller.
    /// </summary>
    KqlTranslation? TranslateFunction(string name, IReadOnlyList<Expression> args, Type resultType) => null;
}
