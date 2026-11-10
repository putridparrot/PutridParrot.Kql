using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Kql.Net.Redis.Search;

/// <summary>
/// A RediSearch query fragment. <see cref="Exact"/> is true when it selects exactly the rows the predicate keeps;
/// otherwise it selects a superset (the predicate is always re-applied in memory, so a superset is safe to push down).
/// </summary>
internal sealed record SearchFilter(string Query, bool Exact)
{
    public static readonly SearchFilter All = new("*", true);

    public bool IsAll => Query == "*";

    public static SearchFilter And(SearchFilter a, SearchFilter b) =>
        a.IsAll ? b with { Exact = a.Exact && b.Exact }
        : b.IsAll ? a with { Exact = a.Exact && b.Exact }
        : new SearchFilter($"({a.Query}) ({b.Query})", a.Exact && b.Exact);
}

/// <summary>Translates LINQ predicates over the rows of a RediSearch table into FT.SEARCH query syntax (DIALECT 2).</summary>
internal sealed class SearchFilterTranslator(RedisSearchIndex index, bool pushDownText)
{
    /// <summary>Resolves an expression to the index attribute it reads (a column of the source row), or null.</summary>
    public Func<Expression, RedisSearchField?> Column { get; set; } = _ => null;

    /// <summary>The filter for a predicate: exact, a superset, or null when nothing can be pushed down.</summary>
    public SearchFilter? Translate(Expression e)
    {
        switch (e)
        {
            // `Convert(x, bool?) == true` / `x == true`: KQL truthiness (null is false)
            case BinaryExpression { NodeType: ExpressionType.Equal, Right: ConstantExpression { Value: true } } b:
                return Translate(StripConvert(b.Left));
            case UnaryExpression { NodeType: ExpressionType.Convert } u when u.Type == typeof(bool?) || u.Type == typeof(bool):
                return Translate(u.Operand);
            case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.And } and:
            {
                var l = Translate(and.Left);
                var r = Translate(and.Right);
                if (l is null || r is null) return (l ?? r) is { } one ? one with { Exact = false } : null;
                return SearchFilter.And(l, r);
            }
            case BinaryExpression { NodeType: ExpressionType.OrElse or ExpressionType.Or } or:
            {
                var l = Translate(or.Left);
                var r = Translate(or.Right);
                if (l is null || r is null) return null;
                if (l.IsAll || r.IsAll) return SearchFilter.All with { Exact = l.Exact && r.Exact };
                return new SearchFilter($"({l.Query})|({r.Query})", l.Exact && r.Exact);
            }
            case UnaryExpression { NodeType: ExpressionType.Not } not:
            {
                // the complement of a superset would be a subset: only exact filters can be negated
                var inner = Translate(not.Operand);
                return inner is { Exact: true, IsAll: false } ? new SearchFilter($"-({inner.Query})", true) : null;
            }
            case BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual:
                return Comparison(b);
            case MethodCallExpression call when call.Method == RedisSearchMarkers.MatchMethod:
                return Marker(call);
            case MethodCallExpression { Method.Name: "Contains" } call:
                return Contains(call);
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ comparisons

    private SearchFilter? Comparison(BinaryExpression b)
    {
        var op = b.NodeType;
        var field = Column(StripConvert(b.Left));
        Expression valueSide = b.Right;
        if (field is null)
        {
            field = Column(StripConvert(b.Right));
            valueSide = b.Left;
            op = Flip(op);
        }
        if (field is null || !TryEvaluate(valueSide, out var value)) return null;

        switch (field.Type)
        {
            case RedisSearchFieldType.Numeric:
            {
                var at = $"@{Name(field)}:";
                if (value is null)
                {
                    // `x == null` / `x != null`: documents without / with a (numeric) value
                    return op switch
                    {
                        ExpressionType.Equal => new SearchFilter($"-{at}[-inf +inf]", true),
                        ExpressionType.NotEqual => new SearchFilter($"{at}[-inf +inf]", true),
                        _ => null
                    };
                }
                if (ToDouble(value) is not { } d || double.IsNaN(d)) return null;
                var n = Number(d);
                return op switch
                {
                    ExpressionType.Equal => new SearchFilter($"{at}[{n} {n}]", true),
                    ExpressionType.NotEqual => new SearchFilter($"-{at}[{n} {n}]", true), // includes missing values, as `!=` does
                    ExpressionType.GreaterThan => new SearchFilter($"{at}[({n} +inf]", true),
                    ExpressionType.GreaterThanOrEqual => new SearchFilter($"{at}[{n} +inf]", true),
                    ExpressionType.LessThan => new SearchFilter($"{at}[-inf ({n}]", true),
                    ExpressionType.LessThanOrEqual => new SearchFilter($"{at}[-inf {n}]", true),
                    _ => null
                };
            }
            case RedisSearchFieldType.Tag when op == ExpressionType.Equal && value is string s:
                return Tag(field, [s]);
            case RedisSearchFieldType.Text when op == ExpressionType.Equal && value is string s:
                return Phrase(field, s);
            default:
                return null;
        }
    }

    /// <summary><c>array.Contains(column)</c> (KQL <c>in</c>).</summary>
    private SearchFilter? Contains(MethodCallExpression call)
    {
        var (list, item) = call.Object is null && call.Arguments.Count == 2 ? (call.Arguments[0], call.Arguments[1])
            : call.Object is not null && call.Arguments.Count == 1 ? (call.Object, call.Arguments[0])
            : (null, null);
        if (list is null || item is null || Column(StripConvert(item)) is not { } field || !TryEvaluate(list, out var values) || values is not IEnumerable items)
            return null;
        var all = items.Cast<object?>().ToList();
        if (all.Count == 0) return null;
        switch (field.Type)
        {
            case RedisSearchFieldType.Numeric:
                if (all.Any(v => v is null)) return null;
                var numbers = all.Select(ToDouble).ToList();
                if (numbers.Any(d => d is null || double.IsNaN(d.Value))) return null;
                if (numbers.Count == 0) return null;
                return new SearchFilter(string.Join("|", numbers.Select(d => $"@{Name(field)}:[{Number(d!.Value)} {Number(d.Value)}]")), true);
            case RedisSearchFieldType.Tag when all.All(v => v is string):
                return Tag(field, all.Cast<string>().ToList());
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ string operators (approximate markers)

    private SearchFilter? Marker(MethodCallExpression call)
    {
        if (call.Arguments[0] is not ConstantExpression { Value: KqlStringOperator op } || Column(StripConvert(call.Arguments[2])) is not { } field
            || !TryEvaluate(call.Arguments[3], out var right))
            return null;
        var terms = right switch
        {
            string s => [s],
            IEnumerable e => e.Cast<object?>().Select(x => x as string).ToList(),
            _ => null
        };
        if (terms is null || terms.Count == 0 || terms.Any(t => t is null)) return null;

        switch (op)
        {
            // case-insensitive: only ASCII values, where RediSearch and .NET agree on case folding
            case KqlStringOperator.EqualsIgnoreCase or KqlStringOperator.InIgnoreCase
                when field.Type == RedisSearchFieldType.Tag && !field.CaseSensitive && terms.All(t => t!.All(char.IsAscii)):
                return Tag(field, terms!);
            case KqlStringOperator.EqualsIgnoreCase when field.Type == RedisSearchFieldType.Text:
                return Phrase(field, terms[0]!);
            case KqlStringOperator.Has or KqlStringOperator.HasAny or KqlStringOperator.HasAll when field.Type == RedisSearchFieldType.Text:
                if (!pushDownText || !terms.All(t => IsSearchableTerm(t!))) return null;
                var joined = string.Join(op == KqlStringOperator.HasAll ? " " : "|", terms);
                return new SearchFilter($"@{Name(field)}:({joined})", false);
            default:
                return null;
        }
    }

    /// <summary>
    /// TAG match (a superset of exact equality: tags are case-insensitive unless CASESENSITIVE and are trimmed).
    /// Values containing the separator or surrounding whitespace can't be matched as one tag and aren't pushed down.
    /// </summary>
    private static SearchFilter? Tag(RedisSearchField field, IReadOnlyList<string> values)
    {
        if (values.Any(v => v.Length == 0 || v.Contains(field.Separator) || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1]))) return null;
        return new SearchFilter($"@{Name(field)}:{{{string.Join(" | ", values.Select(Escape))}}}", false);
    }

    /// <summary>TEXT phrase match: the value's terms in sequence (a superset of equality). Only with text pushdown on.</summary>
    private SearchFilter? Phrase(RedisSearchField field, string value)
    {
        if (!pushDownText) return null;
        var terms = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || terms.Length != value.Split(' ').Length || !terms.All(IsSearchableTerm)) return null;
        return new SearchFilter($"@{Name(field)}:\"{string.Join(" ", terms)}\"", false);
    }

    /// <summary>ASCII letters/digits only (RediSearch and KQL then agree on the term) and not a stop word.</summary>
    private bool IsSearchableTerm(string term) =>
        term.Length > 0 && term.All(char.IsAsciiLetterOrDigit) && !index.StopWords.Contains(term);

    // ------------------------------------------------------------------ helpers

    private static ExpressionType Flip(ExpressionType op) => op switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => op
    };

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u) e = u.Operand;
        return e;
    }

    /// <summary>Evaluates an expression that doesn't depend on the row (constants, captured values).</summary>
    private static bool TryEvaluate(Expression e, out object? value)
    {
        value = null;
        if (StripConvert(e) is ConstantExpression c)
        {
            value = c.Value;
            return true;
        }
        if (new ParameterFinder().Find(e)) return false;
        try
        {
            value = Expression.Lambda<Func<object?>>(Expression.Convert(e, typeof(object))).Compile(preferInterpretation: true)();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private bool _found;

        public bool Find(Expression e)
        {
            Visit(e);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found = true;
            return node;
        }
    }

    private static double? ToDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        long or int or short or byte or sbyte or ulong or uint or ushort => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => null
    };

    internal static string Number(double d) =>
        double.IsPositiveInfinity(d) ? "+inf" : double.IsNegativeInfinity(d) ? "-inf" : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>An attribute name for <c>@name</c>, with punctuation escaped.</summary>
    internal static string Name(RedisSearchField field) => Escape(field.Name);

    /// <summary>Backslash-escapes everything but letters, digits and underscores.</summary>
    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
