using System.Linq.Expressions;
using System.Reflection;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using BinaryExpression = Kusto.Language.Syntax.BinaryExpression;
using RowSchema = Kql.Net.Runtime.RowSchema;
using DynamicExpression = Kusto.Language.Syntax.DynamicExpression;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

/// <summary>Context for compiling an aggregate: the group (an <c>IEnumerable&lt;TRow&gt;</c>) and its row schema.</summary>
internal sealed record GroupContext(LExpr Group, RowSchema RowSchema);

/// <summary>
/// Compiles bound Kusto scalar expressions into LINQ expressions over a row parameter.
/// </summary>
internal sealed partial class ScalarCompiler
{
    private static readonly MethodInfo GetMemberMethod = typeof(KqlConvert).GetMethod(nameof(KqlConvert.GetMember))!;
    private static readonly MethodInfo GetElementMethod = typeof(KqlConvert).GetMethod(nameof(KqlConvert.GetElement))!;

    private static readonly HashSet<string> NonDeterministic = new(StringComparer.Ordinal) { "rand", "new_guid", "newguid" };

    /// <summary>Functions whose (folded) value depends on when the query is compiled.</summary>
    private static readonly HashSet<string> ClockFunctions = new(StringComparer.Ordinal) { "now", "ago" };

    /// <summary>Functions implemented by the Kql.Net.Geo package (named in the error when it isn't in use).</summary>
    private static readonly HashSet<string> GeoPackageFunctions = new(StringComparer.Ordinal)
    {
        "geo_point_to_h3cell", "geo_h3cell_to_central_point", "geo_h3cell_to_polygon", "geo_h3cell_level", "geo_h3cell_parent",
        "geo_h3cell_children", "geo_h3cell_neighbors", "geo_h3cell_rings", "geo_polygon_to_h3cells", "geo_point_buffer",
        "geo_line_buffer", "geo_polygon_buffer", "geo_intersection_2polygons", "geo_intersection_line_with_polygon",
        "geo_union_polygons_array", "geo_union_lines_array", "geo_simplify_polygons_array", "geo_info_from_ip_address"
    };

    private readonly QueryCompiler _query;
    private readonly RowSchema? _schema;
    private readonly LExpr? _row;
    private readonly GroupContext? _group;
    private readonly LExpr? _window;
    private readonly IKqlQueryTranslator? _translator;
    private readonly KExpr? _approximationRoot;

    /// <param name="window">A <see cref="WindowContext{T}"/> expression when compiling over serialized rows.</param>
    /// <param name="translator">Provider translator to try before falling back to in-memory helpers.</param>
    /// <param name="approximationRoot">
    /// The root of a where predicate: approximate (superset) translations are allowed for sub-expressions reached
    /// from it only through and / or / parentheses.
    /// </param>
    public ScalarCompiler(QueryCompiler query, RowSchema? schema, LExpr? row, GroupContext? group = null, LExpr? window = null,
        IKqlQueryTranslator? translator = null, KExpr? approximationRoot = null)
    {
        _query = query;
        _schema = schema;
        _row = row;
        _group = group;
        _window = window;
        _translator = window is null ? translator : null;
        _approximationRoot = approximationRoot;
    }

    internal IKqlQueryTranslator? Translator => _translator;

    /// <summary>For `search`: term matching is case-sensitive (kind=case_sensitive).</summary>
    internal bool SearchCaseSensitive { get; init; }

    /// <summary>Columns excluded from "any column" searches (e.g. search's $table).</summary>
    internal IReadOnlySet<string> SearchExcludedColumns { get; init; } = new HashSet<string>();

    /// <summary>A search term against one string value: `abc` = has, `abc*` = hasprefix, `*abc` = hassuffix, `*abc*` = contains.</summary>
    private LExpr SearchTerm(LExpr value, string term, bool cs)
    {
        RequiresClient = true;
        var s = ExpressionHelpers.Coerce(value, typeof(string));
        string method;
        var core = term;
        if (term.Length > 1 && term.StartsWith('*') && term.EndsWith('*')) { method = nameof(KqlFunctions.Contains); core = term[1..^1]; }
        else if (term.EndsWith('*')) { method = nameof(KqlFunctions.HasPrefix); core = term[..^1]; }
        else if (term.StartsWith('*')) { method = nameof(KqlFunctions.HasSuffix); core = term[1..]; }
        else if (term.Contains('*'))
            return LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.Like))!, s, LExpr.Constant(term.Replace('*', '%')), LExpr.Constant(cs));
        else method = nameof(KqlFunctions.Has);
        return LExpr.Call(typeof(KqlFunctions).GetMethod(method)!, s, LExpr.Constant(core), LExpr.Constant(cs));
    }

    /// <summary>True when any searchable column of the row satisfies <paramref name="test"/>.</summary>
    private LExpr AnyColumn(Func<LExpr, LExpr> test)
    {
        if (_schema is null || _row is null) throw new KqlException("'*' can only be used in a row context.");
        LExpr acc = LExpr.Constant(false);
        foreach (var c in _schema.Columns.Where(c => !SearchExcludedColumns.Contains(c.Name)))
            acc = LExpr.OrElse(acc, test(Read(c, _row)));
        return acc;
    }

    /// <summary>True when an approximate translation was used, so the exact predicate must be re-applied in memory.</summary>
    public bool UsedApproximation { get; private set; }

    /// <summary>Asks the translator for a provider form of a string operator; null means "evaluate in memory".</summary>
    private LExpr? TryTranslate(KqlStringOperator op, bool caseSensitive, LExpr left, LExpr right, SyntaxNode at, bool negate)
    {
        if (_translator?.TranslateStringOperator(op, caseSensitive, left, right) is not { } t) return null;
        if (t.IsApproximate)
        {
            if (negate || !ApproximationAllowed(at)) return null;
            UsedApproximation = true;
        }
        return t.Expression;
    }

    private bool ApproximationAllowed(SyntaxNode node)
    {
        if (_approximationRoot is null) return false;
        for (var n = node; n != _approximationRoot; n = n.Parent)
        {
            if (n.Parent is null) return false;
            if (n.Parent is not (ParenthesizedExpression or BinaryExpression { Kind: SyntaxKind.AndExpression or SyntaxKind.OrExpression }))
                return false;
        }
        return true;
    }

    /// <summary>Names of KQL window functions, which need serialized input.</summary>
    internal static readonly HashSet<string> WindowFunctions = new(StringComparer.Ordinal)
    {
        "row_number", "prev", "next", "row_cumsum", "row_rank_dense", "row_rank_min"
    };

    private static int _windowSite;

    /// <summary>True when the compiled expression needs in-memory (client) evaluation.</summary>
    public bool RequiresClient { get; private set; }

    public LExpr Compile(KExpr node)
    {
        var result = CompileCore(node);
        if (node.ResultType is ScalarSymbol s && s != ScalarTypes.Unknown && s != ScalarTypes.Null)
        {
            var target = KqlTypes.ClrTypeFor(s);
            if (result.Type != target && !(result.Type == typeof(bool) && target == typeof(bool?)))
                result = ExpressionHelpers.Coerce(result, target);
        }
        return result;
    }

    /// <summary>Compiles a predicate (null => false).</summary>
    public LExpr CompilePredicate(KExpr node) => ExpressionHelpers.ToPredicate(Compile(node));

    /// <summary>Compiles a predicate only to find out whether it needs in-memory evaluation.</summary>
    public bool CompileAndReport(KExpr node)
    {
        CompilePredicate(node);
        return RequiresClient;
    }

    private LExpr CompileCore(KExpr node)
    {
        switch (node)
        {
            case LiteralExpression { LiteralValue: string term } searchTerm when searchTerm.ResultType == ScalarTypes.Bool:
                // a bare string in a search predicate: does any column have the term?
                return LExpr.Convert(AnyColumn(v => SearchTerm(v, term, SearchCaseSensitive)), typeof(bool?));
            case LiteralExpression lit:
                return Literal(lit.LiteralValue, lit.ResultType);
            case CompoundStringLiteralExpression cs:
                return LExpr.Constant(cs.LiteralValue as string, typeof(string));
            case DynamicExpression dyn:
                return LExpr.Constant(JsonLiteral(dyn.Expression), typeof(object));
            case ParenthesizedExpression p:
                return Compile(p.Expression);
            case NameReference nr:
                return NameRef(nr);
            case BracketedExpression b:
                return Compile(b.Expression);
            case PrefixUnaryExpression u:
                return Unary(u);
            case BinaryExpression b:
                return Binary(b);
            case InExpression ie:
                return In(ie);
            case BetweenExpression be:
                return Between(be);
            case HasAnyExpression ha:
                return HasAnyAll(ha, ha.Left, ha.Right, all: false);
            case HasAllExpression hl:
                return HasAnyAll(hl, hl.Left, hl.Right, all: true);
            case PathExpression path:
                return Path(path);
            case ElementExpression el:
                return Element(el);
            case FunctionCallExpression fc:
                return Call(fc);
            case ToScalarExpression ts:
                return LExpr.Constant(_query.EvaluateScalar(ts.Expression), KqlTypes.ClrTypeFor(ts.ResultType));
            case SimpleNamedExpression sn:
                return Compile(sn.Expression);
            case OrderedExpression oe:
                return Compile(oe.Expression);
            default:
                throw new KqlNotSupportedException(node.Kind.ToString(), node.TextStart);
        }
    }

    /// <summary>Builds a dynamic value from KQL JSON literal syntax (which allows single quotes, datetime(...), etc.).</summary>
    private object? JsonLiteral(KExpr e) => e switch
    {
        JsonObjectExpression o => o.Pairs.Aggregate(new Dictionary<string, object?>(), (d, p) =>
        {
            d[p.Element.Name.ValueText] = JsonLiteral(p.Element.Value);
            return d;
        }),
        JsonArrayExpression a => a.Values.Select(v => JsonLiteral(v.Element)).ToList(),
        LiteralExpression { Kind: SyntaxKind.NullLiteralExpression } => null,
        LiteralExpression { Token.Kind: SyntaxKind.NullKeyword } => null,
        LiteralExpression l => Dynamic.Normalize(l.LiteralValue),
        _ => Dynamic.Normalize(_query.EvaluateScalar(e))
    };

    private static LExpr Literal(object? value, TypeSymbol type)
    {
        var target = KqlTypes.ClrTypeFor(type);
        if (value is null) return LExpr.Constant(null, KqlTypes.MakeNullable(target));
        var converted = KqlConvert.ChangeType(value, target);
        return LExpr.Constant(converted, target);
    }

    private LExpr NameRef(NameReference nr)
    {
        switch (nr.ReferencedSymbol)
        {
            case ColumnSymbol col:
                return ReadColumn(col.Name, nr);
            case VariableSymbol v when _query.TryGetScalarLet(v, out var value):
                return value;
            case VariableSymbol { IsConstant: true } v:
                return Literal(v.ConstantValue, v.Type);
            case ParameterSymbol or VariableSymbol when _query.TryGetParameter(nr.SimpleName, out var pv):
                return pv;
            case FunctionSymbol fs:
                // a function referenced without parens, e.g. `now`
                return BindFunction(fs.Name, [], nr);
            default:
                if (_query.TryGetParameter(nr.SimpleName, out var param)) return param;
                if (_schema?.Find(nr.SimpleName) is not null) return ReadColumn(nr.SimpleName, nr);
                throw new KqlNotSupportedException($"reference to '{nr.SimpleName}' ({nr.ReferencedSymbol?.Kind})", nr.TextStart);
        }
    }

    private LExpr ReadColumn(string name, SyntaxNode at)
    {
        if (_schema is null || _row is null)
            throw new KqlException($"Column '{name}' cannot be referenced here (position {at.TextStart}).");
        return Read(_schema.Get(name), _row);
    }

    /// <summary>Reads a column, noting whether the read needs client-side evaluation.</summary>
    internal LExpr Read(RowColumn column, LExpr row)
    {
        RequiresClient |= column.ClientOnly;
        return column.Read(row);
    }

    // ------------------------------------------------------------------ operators

    private LExpr Unary(PrefixUnaryExpression u)
    {
        var operand = Compile(u.Expression);
        return u.Kind switch
        {
            SyntaxKind.UnaryMinusExpression => LExpr.Negate(operand.Type == typeof(object) ? ExpressionHelpers.Coerce(operand, typeof(double?)) : operand),
            SyntaxKind.UnaryPlusExpression => operand,
            _ => throw new KqlNotSupportedException(u.Kind.ToString(), u.TextStart)
        };
    }

    private LExpr Binary(BinaryExpression b)
    {
        switch (b.Kind)
        {
            case SyntaxKind.AddExpression: return Arithmetic(ExpressionType.Add, b);
            case SyntaxKind.SubtractExpression: return Arithmetic(ExpressionType.Subtract, b);
            case SyntaxKind.MultiplyExpression: return Arithmetic(ExpressionType.Multiply, b);
            case SyntaxKind.DivideExpression: return Arithmetic(ExpressionType.Divide, b);
            case SyntaxKind.ModuloExpression: return Arithmetic(ExpressionType.Modulo, b);

            case SyntaxKind.LessThanExpression: return Comparison(ExpressionType.LessThan, b);
            case SyntaxKind.LessThanOrEqualExpression: return Comparison(ExpressionType.LessThanOrEqual, b);
            case SyntaxKind.GreaterThanExpression: return Comparison(ExpressionType.GreaterThan, b);
            case SyntaxKind.GreaterThanOrEqualExpression: return Comparison(ExpressionType.GreaterThanOrEqual, b);
            case SyntaxKind.EqualExpression: return Comparison(ExpressionType.Equal, b);
            case SyntaxKind.NotEqualExpression: return Comparison(ExpressionType.NotEqual, b);

            case SyntaxKind.AndExpression:
                return LExpr.Convert(LExpr.AndAlso(CompilePredicate(b.Left), CompilePredicate(b.Right)), typeof(bool?));
            case SyntaxKind.OrExpression:
                return LExpr.Convert(LExpr.OrElse(CompilePredicate(b.Left), CompilePredicate(b.Right)), typeof(bool?));

            case SyntaxKind.EqualTildeExpression: return StringOp(nameof(KqlFunctions.EqualsIgnoreCase), b, null, false);
            case SyntaxKind.BangTildeExpression: return StringOp(nameof(KqlFunctions.EqualsIgnoreCase), b, null, true);

            case SyntaxKind.HasExpression: return StringOp(nameof(KqlFunctions.Has), b, false, false);
            case SyntaxKind.HasCsExpression: return StringOp(nameof(KqlFunctions.Has), b, true, false);
            case SyntaxKind.NotHasExpression: return StringOp(nameof(KqlFunctions.Has), b, false, true);
            case SyntaxKind.NotHasCsExpression: return StringOp(nameof(KqlFunctions.Has), b, true, true);
            case SyntaxKind.HasPrefixExpression: return StringOp(nameof(KqlFunctions.HasPrefix), b, false, false);
            case SyntaxKind.HasPrefixCsExpression: return StringOp(nameof(KqlFunctions.HasPrefix), b, true, false);
            case SyntaxKind.NotHasPrefixExpression: return StringOp(nameof(KqlFunctions.HasPrefix), b, false, true);
            case SyntaxKind.NotHasPrefixCsExpression: return StringOp(nameof(KqlFunctions.HasPrefix), b, true, true);
            case SyntaxKind.HasSuffixExpression: return StringOp(nameof(KqlFunctions.HasSuffix), b, false, false);
            case SyntaxKind.HasSuffixCsExpression: return StringOp(nameof(KqlFunctions.HasSuffix), b, true, false);
            case SyntaxKind.NotHasSuffixExpression: return StringOp(nameof(KqlFunctions.HasSuffix), b, false, true);
            case SyntaxKind.NotHasSuffixCsExpression: return StringOp(nameof(KqlFunctions.HasSuffix), b, true, true);
            case SyntaxKind.ContainsExpression: return StringOp(nameof(KqlFunctions.Contains), b, false, false);
            case SyntaxKind.ContainsCsExpression: return StringOp(nameof(KqlFunctions.Contains), b, true, false);
            case SyntaxKind.NotContainsExpression: return StringOp(nameof(KqlFunctions.Contains), b, false, true);
            case SyntaxKind.NotContainsCsExpression: return StringOp(nameof(KqlFunctions.Contains), b, true, true);
            case SyntaxKind.StartsWithExpression: return StringOp(nameof(KqlFunctions.StartsWith), b, false, false);
            case SyntaxKind.StartsWithCsExpression: return StringOp(nameof(KqlFunctions.StartsWith), b, true, false);
            case SyntaxKind.NotStartsWithExpression: return StringOp(nameof(KqlFunctions.StartsWith), b, false, true);
            case SyntaxKind.NotStartsWithCsExpression: return StringOp(nameof(KqlFunctions.StartsWith), b, true, true);
            case SyntaxKind.EndsWithExpression: return StringOp(nameof(KqlFunctions.EndsWith), b, false, false);
            case SyntaxKind.EndsWithCsExpression: return StringOp(nameof(KqlFunctions.EndsWith), b, true, false);
            case SyntaxKind.NotEndsWithExpression: return StringOp(nameof(KqlFunctions.EndsWith), b, false, true);
            case SyntaxKind.NotEndsWithCsExpression: return StringOp(nameof(KqlFunctions.EndsWith), b, true, true);
            case SyntaxKind.LikeExpression: return StringOp(nameof(KqlFunctions.Like), b, false, false);
            case SyntaxKind.LikeCsExpression: return StringOp(nameof(KqlFunctions.Like), b, true, false);
            case SyntaxKind.NotLikeExpression: return StringOp(nameof(KqlFunctions.Like), b, false, true);
            case SyntaxKind.NotLikeCsExpression: return StringOp(nameof(KqlFunctions.Like), b, true, true);
            case SyntaxKind.MatchesRegexExpression: return StringOp(nameof(KqlFunctions.MatchesRegex), b, null, false);

            case SyntaxKind.SearchExpression when b.Right is LiteralExpression { LiteralValue: string columnTerm }:
                // search's `Column:term`
                return LExpr.Convert(SearchTerm(Compile(b.Left), columnTerm, SearchCaseSensitive), typeof(bool?));

            default:
                throw new KqlNotSupportedException(b.Kind.ToString(), b.TextStart);
        }
    }

    private LExpr StringOp(string method, BinaryExpression b, bool? caseSensitive, bool negate)
    {
        if (b.Left is StarExpression)
        {
            // `* has 'x'`: any column satisfies the operator
            RequiresClient = true;
            var term = ExpressionHelpers.Coerce(Compile(b.Right), typeof(string));
            var any = AnyColumn(v =>
            {
                var callArgs = new List<LExpr> { ExpressionHelpers.Coerce(v, typeof(string)), term };
                if (caseSensitive is { } c) callArgs.Add(LExpr.Constant(c));
                return LExpr.Call(typeof(KqlFunctions).GetMethod(method, callArgs.Select(a => a.Type).ToArray())!, callArgs);
            });
            return LExpr.Convert(negate ? LExpr.Not(any) : any, typeof(bool?));
        }

        var args = new List<LExpr>
        {
            ExpressionHelpers.Coerce(Compile(b.Left), typeof(string)),
            ExpressionHelpers.Coerce(Compile(b.Right), typeof(string))
        };
        if (TryTranslate(System.Enum.Parse<KqlStringOperator>(method), caseSensitive ?? false, args[0], args[1], b, negate) is { } translated)
            return LExpr.Convert(negate ? LExpr.Not(translated) : translated, typeof(bool?));

        if (caseSensitive is { } cs) args.Add(LExpr.Constant(cs));
        var mi = typeof(KqlFunctions).GetMethod(method, args.Select(a => a.Type).ToArray())!;
        RequiresClient = true;
        LExpr call = LExpr.Call(mi, args);
        if (negate) call = LExpr.Not(call);
        return LExpr.Convert(call, typeof(bool?));
    }

    private LExpr Arithmetic(ExpressionType op, BinaryExpression b)
    {
        var left = Compile(b.Left);
        var right = Compile(b.Right);
        var resultType = KqlTypes.ClrTypeFor(b.ResultType);

        if (KqlTypes.IsNumeric(resultType) && (KqlTypes.IsNumeric(left.Type) || left.Type == typeof(object))
                                           && (KqlTypes.IsNumeric(right.Type) || right.Type == typeof(object)))
        {
            left = ExpressionHelpers.Coerce(left, resultType);
            right = ExpressionHelpers.Coerce(right, resultType);
            if (op is ExpressionType.Divide or ExpressionType.Modulo && resultType != typeof(double?))
            {
                // integer/decimal division by zero yields null in KQL
                var zero = LExpr.Constant(Activator.CreateInstance(System.Nullable.GetUnderlyingType(resultType)!), resultType);
                return LExpr.Condition(LExpr.Equal(right, zero), LExpr.Constant(null, resultType), LExpr.MakeBinary(op, left, right));
            }
            return LExpr.MakeBinary(op, left, right);
        }

        // datetime / timespan arithmetic
        left = Nullable(left);
        right = Nullable(right);
        if (left.Type == typeof(TimeSpan?) && KqlTypes.IsNumeric(right.Type)) right = ExpressionHelpers.Coerce(right, typeof(double?));
        if (right.Type == typeof(TimeSpan?) && KqlTypes.IsNumeric(left.Type))
        {
            (left, right) = (right, ExpressionHelpers.Coerce(left, typeof(double?)));
        }
        if (left.Type == typeof(object)) left = ExpressionHelpers.Coerce(left, right.Type);
        if (right.Type == typeof(object)) right = ExpressionHelpers.Coerce(right, left.Type);

        try
        {
            var r = LExpr.MakeBinary(op, left, right);
            return ExpressionHelpers.Coerce(r, resultType);
        }
        catch (InvalidOperationException)
        {
            throw new KqlNotSupportedException($"operator {op} on {b.Left.ResultType.Name} and {b.Right.ResultType.Name}", b.TextStart);
        }
    }

    private static LExpr Nullable(LExpr e) => e.Type.IsValueType && System.Nullable.GetUnderlyingType(e.Type) is null
        ? LExpr.Convert(e, KqlTypes.MakeNullable(e.Type))
        : e;

    private LExpr Comparison(ExpressionType op, BinaryExpression b)
    {
        var (left, right) = Unify(Compile(b.Left), Compile(b.Right));

        LExpr result;
        if (left.Type == typeof(string) && op is not (ExpressionType.Equal or ExpressionType.NotEqual))
        {
            var cmp = TryTranslate(KqlStringOperator.Compare, true, left, right, b, false);
            if (cmp is null)
            {
                RequiresClient = true;
                cmp = LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.CompareOrdinal))!, left, right);
            }
            result = LExpr.MakeBinary(op, cmp, LExpr.Constant(0));
        }
        else if (left.Type == typeof(object))
        {
            RequiresClient = true;
            LExpr eq = LExpr.Call(typeof(KqlComparer).GetMethod(nameof(KqlComparer.ValueEquals))!, left, right);
            result = op switch
            {
                ExpressionType.Equal => eq,
                ExpressionType.NotEqual => LExpr.Not(eq),
                _ => throw new KqlNotSupportedException("ordering comparison of dynamic values", b.TextStart)
            };
            result = NullGuard(result, left, right);
        }
        else if (left.Type == typeof(string))
        {
            result = StringEquality(op == ExpressionType.Equal, left, right);
        }
        else if (op is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            // KQL: comparing with null is never true (null == null is not true either); use isnull() to find nulls
            result = IsNullConstant(left) || IsNullConstant(right)
                ? LExpr.Constant(false)
                : NullGuard(LExpr.MakeBinary(op, left, right), left, right);
        }
        else
        {
            result = LExpr.MakeBinary(op, left, right); // lifted <, <=, >, >= are already false for null
        }
        return LExpr.Convert(result, typeof(bool?));
    }

    private static bool IsNullConstant(LExpr e) => e is ConstantExpression { Value: null };

    private static bool IsNonNullConstant(LExpr e) => e is ConstantExpression { Value: not null };

    /// <summary>
    /// Wraps a comparison so it's false when either operand is null. Non-null constants need no check, which keeps
    /// the common `column == literal` case a plain, index-friendly comparison in SQL.
    /// </summary>
    private static LExpr NullGuard(LExpr comparison, LExpr left, LExpr right)
    {
        var result = comparison;
        if (!IsNonNullConstant(right) && CanBeNull(right)) result = LExpr.AndAlso(LExpr.NotEqual(right, LExpr.Constant(null, right.Type)), result);
        if (!IsNonNullConstant(left) && CanBeNull(left)) result = LExpr.AndAlso(LExpr.NotEqual(left, LExpr.Constant(null, left.Type)), result);
        return result;
    }

    private static bool CanBeNull(LExpr e) => !e.Type.IsValueType || System.Nullable.GetUnderlyingType(e.Type) is not null;

    /// <summary>
    /// KQL strings are never null: a null CLR string is treated as "" (so "" == null is true).
    /// </summary>
    private static LExpr StringEquality(bool equal, LExpr left, LExpr right)
    {
        var isNullOrEmpty = typeof(string).GetMethod(nameof(string.IsNullOrEmpty))!;
        LExpr result;
        if (left is ConstantExpression { Value: null or "" }) result = LExpr.Call(isNullOrEmpty, right);
        else if (right is ConstantExpression { Value: null or "" }) result = LExpr.Call(isNullOrEmpty, left);
        else if (IsNonNullConstant(left) || IsNonNullConstant(right)) result = LExpr.Equal(left, right); // null column never equals a non-empty literal
        else result = LExpr.Equal(LExpr.Coalesce(left, LExpr.Constant("")), LExpr.Coalesce(right, LExpr.Constant("")));
        return equal ? result : LExpr.Not(result);
    }

    /// <summary>Brings two operands to a common runtime type for comparison.</summary>
    internal static (LExpr, LExpr) Unify(LExpr left, LExpr right)
    {
        if (left.Type == right.Type) return (left, right);
        if (left is ConstantExpression { Value: null }) return (LExpr.Constant(null, KqlTypes.MakeNullable(right.Type)), Nullable(right));
        if (right is ConstantExpression { Value: null }) return (Nullable(left), LExpr.Constant(null, KqlTypes.MakeNullable(left.Type)));
        if (KqlTypes.IsNumeric(left.Type) && KqlTypes.IsNumeric(right.Type))
        {
            var t = KqlTypes.WidestNumeric(left.Type, right.Type);
            return (ExpressionHelpers.Coerce(left, t), ExpressionHelpers.Coerce(right, t));
        }
        if (left.Type == typeof(object)) return (ExpressionHelpers.Coerce(left, KqlTypes.MakeNullable(right.Type)), Nullable(right));
        if (right.Type == typeof(object)) return (Nullable(left), ExpressionHelpers.Coerce(right, KqlTypes.MakeNullable(left.Type)));
        var l = Nullable(left);
        var r = Nullable(right);
        if (l.Type == r.Type) return (l, r);
        return (l, ExpressionHelpers.Coerce(r, l.Type));
    }

    private LExpr In(InExpression ie)
    {
        var left = Compile(ie.Left);
        var negate = ie.Kind is SyntaxKind.NotInExpression or SyntaxKind.NotInCsExpression;
        var caseInsensitive = ie.Kind is SyntaxKind.InCsExpression or SyntaxKind.NotInCsExpression; // `in~` / `!in~`

        var items = new List<object?>();
        var exprs = new List<LExpr>();
        foreach (var se in ie.Right.Expressions)
        {
            var e = se.Element;
            if (e.ResultType is TableSymbol)
            {
                items.AddRange(_query.EvaluateColumn(e));
                continue;
            }
            var compiled = Compile(e);
            if (compiled.Type == typeof(object) && compiled is ConstantExpression { Value: List<object?> list })
            {
                items.AddRange(list);
                continue;
            }
            if (compiled is ConstantExpression c)
            {
                items.Add(c.Value);
                continue;
            }
            exprs.Add(compiled);
        }

        LExpr result;
        if (caseInsensitive)
        {
            var all = items.Select(i => (LExpr)LExpr.Constant(KqlConvert.ToStringValue(i), typeof(string)))
                .Concat(exprs.Select(e => ExpressionHelpers.Coerce(e, typeof(string))));
            var stringLeft = ExpressionHelpers.Coerce(left, typeof(string));
            var array = LExpr.NewArrayInit(typeof(string), all);
            var translated = TryTranslate(KqlStringOperator.InIgnoreCase, false, stringLeft, array, ie, negate);
            if (translated is null) RequiresClient = true;
            result = translated ?? LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.InIgnoreCase))!, stringLeft, array);
        }
        else
        {
            var elementType = left.Type == typeof(object) ? typeof(object) : Nullable(left).Type;
            var typedLeft = left.Type == typeof(object) ? left : Nullable(left);
            LExpr? acc = null;
            if (items.Count > 0)
            {
                var arr = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++) arr.SetValue(KqlConvert.ChangeType(Dynamic.Normalize(items[i]), elementType), i);
                acc = LExpr.Call(typeof(Enumerable).GetMethods().First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
                    .MakeGenericMethod(elementType), LExpr.Constant(arr), typedLeft);
            }
            foreach (var e in exprs)
            {
                var (l, r) = Unify(typedLeft, e);
                LExpr eq = LExpr.Equal(l, r);
                acc = acc is null ? eq : LExpr.OrElse(acc, eq);
            }
            result = acc ?? LExpr.Constant(false);
        }

        if (negate)
        {
            // like ==, a null (non-string) value is neither in nor not in the list
            result = left.Type == typeof(string) ? LExpr.Not(result) : NullGuard(LExpr.Not(result), left, LExpr.Constant(0));
        }
        return LExpr.Convert(result, typeof(bool?));
    }

    private LExpr Between(BetweenExpression be)
    {
        var value = Compile(be.Left);
        var lo = Compile(be.Right.First);
        var hi = Compile(be.Right.Second);
        if (Nullable(value).Type == typeof(DateTime?) && Nullable(hi).Type == typeof(TimeSpan?))
            hi = LExpr.Add(Nullable(lo), Nullable(hi));

        var (v1, l) = Unify(value, lo);
        var (v2, h) = Unify(value, hi);
        LExpr result = LExpr.AndAlso(LExpr.GreaterThanOrEqual(v1, l), LExpr.LessThanOrEqual(v2, h));
        if (be.Kind == SyntaxKind.NotBetweenExpression) result = LExpr.Not(result);
        return LExpr.Convert(result, typeof(bool?));
    }

    private LExpr HasAnyAll(KExpr node, KExpr left, ExpressionList right, bool all)
    {
        var terms = new List<LExpr>();
        foreach (var se in right.Expressions)
        {
            var c = Compile(se.Element);
            if (c is ConstantExpression { Value: List<object?> list })
                terms.AddRange(list.Select(x => (LExpr)LExpr.Constant(KqlConvert.ToStringValue(x), typeof(string))));
            else
                terms.Add(ExpressionHelpers.Coerce(c, typeof(string)));
        }
        var stringLeft = ExpressionHelpers.Coerce(Compile(left), typeof(string));
        var array = LExpr.NewArrayInit(typeof(string), terms);
        if (TryTranslate(all ? KqlStringOperator.HasAll : KqlStringOperator.HasAny, false, stringLeft, array, node, false) is { } translated)
            return LExpr.Convert(translated, typeof(bool?));

        RequiresClient = true;
        var mi = typeof(KqlFunctions).GetMethod(all ? nameof(KqlFunctions.HasAll) : nameof(KqlFunctions.HasAny))!;
        return LExpr.Convert(LExpr.Call(mi, stringLeft, array), typeof(bool?));
    }

    private LExpr Path(PathExpression path)
    {
        var target = Compile(path.Expression);
        var name = path.Selector switch
        {
            NameReference n => n.SimpleName,
            BracketedExpression { Expression: LiteralExpression l } => l.LiteralValue?.ToString(),
            _ => null
        } ?? throw new KqlNotSupportedException("path selector", path.Selector.TextStart);
        RequiresClient = true;
        return LExpr.Call(GetMemberMethod, ExpressionHelpers.Coerce(target, typeof(object)), LExpr.Constant(name));
    }

    private LExpr Element(ElementExpression el)
    {
        var target = Compile(el.Expression);
        var selector = el.Selector is BracketedExpression b ? b.Expression : el.Selector;
        var index = Compile(selector);
        RequiresClient = true;
        return LExpr.Call(GetElementMethod, ExpressionHelpers.Coerce(target, typeof(object)), ExpressionHelpers.Coerce(index, typeof(object)));
    }

    // ------------------------------------------------------------------ functions

    private LExpr Call(FunctionCallExpression fc)
    {
        var name = fc.Name.SimpleName;
        var symbol = fc.ReferencedSymbol as FunctionSymbol;

        if (_query.IsAggregate(symbol))
            return Aggregate(fc, name);

        if (symbol is not null && !_query.IsBuiltIn(symbol))
            return _query.InlineUserFunction(fc, this);

        var args = fc.ArgumentList.Expressions.Select(e => e.Element).ToList();

        if (WindowFunctions.Contains(name))
            return Window(name, args, fc);

        switch (name)
        {
            case "iff":
            case "iif":
            {
                var rt = KqlTypes.ClrTypeFor(fc.ResultType);
                return LExpr.Condition(CompilePredicate(args[0]),
                    ExpressionHelpers.Coerce(Compile(args[1]), rt), ExpressionHelpers.Coerce(Compile(args[2]), rt));
            }
            case "case":
            {
                var rt = KqlTypes.ClrTypeFor(fc.ResultType);
                LExpr acc = ExpressionHelpers.Coerce(Compile(args[^1]), rt);
                for (var i = args.Count - 3; i >= 0; i -= 2)
                    acc = LExpr.Condition(CompilePredicate(args[i]), ExpressionHelpers.Coerce(Compile(args[i + 1]), rt), acc);
                return acc;
            }
            case "coalesce":
            {
                var rt = KqlTypes.ClrTypeFor(fc.ResultType);
                LExpr acc = ExpressionHelpers.Coerce(Compile(args[^1]), rt);
                for (var i = args.Count - 2; i >= 0; i--)
                {
                    var v = ExpressionHelpers.Coerce(Compile(args[i]), rt);
                    LExpr present = rt == typeof(string)
                        ? LExpr.Not(LExpr.Call(typeof(string).GetMethod(nameof(string.IsNullOrEmpty))!, v))
                        : LExpr.Not(ExpressionHelpers.IsNull(v));
                    acc = LExpr.Condition(present, v, acc);
                }
                return acc;
            }
            case "isnull":
                return LExpr.Convert(ExpressionHelpers.IsNull(Compile(args[0])), typeof(bool?));
            case "isnotnull":
                return LExpr.Convert(LExpr.Not(ExpressionHelpers.IsNull(Compile(args[0]))), typeof(bool?));
            case "isempty":
            case "isnotempty":
            {
                var v = Compile(args[0]);
                LExpr empty = v.Type == typeof(string)
                    ? LExpr.Call(typeof(string).GetMethod(nameof(string.IsNullOrEmpty))!, v)
                    : v.Type == typeof(object)
                        ? LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.IsEmpty))!, v)
                        : ExpressionHelpers.IsNull(v);
                if (name == "isnotempty") empty = LExpr.Not(empty);
                return LExpr.Convert(empty, typeof(bool?));
            }
            case "not":
                return LExpr.Convert(LExpr.Not(CompilePredicate(args[0])), typeof(bool?));
            case "pack_all":
            case "bag_pack_all":
                return PackAll();
            case "column_ifexists":
            {
                var col = (args[0] as LiteralExpression)?.LiteralValue as string;
                return col is not null && _schema?.Find(col) is { } c && _row is not null
                    ? ExpressionHelpers.Coerce(c.Read(_row), KqlTypes.ClrTypeFor(fc.ResultType))
                    : ExpressionHelpers.Coerce(Compile(args[1]), KqlTypes.ClrTypeFor(fc.ResultType));
            }
        }

        return BindFunction(name, args.Select(Compile).ToList(), fc);
    }

    private LExpr BindFunction(string name, IReadOnlyList<LExpr> args, SyntaxNode at)
    {
        var call = _query.Functions.TryBind(name, args);

        // Constant-fold deterministic calls with constant arguments (e.g. ago(1d)) so providers see a constant.
        if (call is not null && !NonDeterministic.Contains(name) && args.All(a => a is ConstantExpression))
        {
            // the clock (and custom functions, which may read anything) make the folded value execution-specific
            if (ClockFunctions.Contains(name) || _query.Functions.CustomFunctionNames.Contains(name)) _query.MarkNotCacheable();
            var value = ExpressionHelpers.Evaluate(call);
            return LExpr.Constant(value, call.Type);
        }

        var resultType = at is KExpr { ResultType: ScalarSymbol rt } ? KqlTypes.ClrTypeFor(rt) : call?.Type ?? typeof(object);
        if (_translator?.TranslateFunction(name, args, resultType) is { IsApproximate: false } translated)
            return ExpressionHelpers.Coerce(translated.Expression, resultType);

        RequiresClient = true;
        return call ?? throw new KqlNotSupportedException($"function {name}()", at.TextStart,
            GeoPackageFunctions.Contains(name) ? "Add the PutridParrot.Kql.Net.Geo package and call KqlContext.UseGeo()." : null);
    }

    private LExpr Window(string name, List<KExpr> args, SyntaxNode at)
    {
        if (_window is null)
            throw new KqlException($"{name}() requires serialized input: use sort/order/serialize first (position {at.TextStart}).");
        RequiresClient = true;
        var site = LExpr.Constant(Interlocked.Increment(ref _windowSite));

        LExpr Restart(int index) => args.Count > index ? CompilePredicate(args[index]) : LExpr.Constant(false);

        switch (name)
        {
            case "prev":
            case "next":
            {
                var offset = args.Count > 1 ? KqlConvert.ToLong(_query.EvaluateScalar(args[1])) ?? 1 : 1;
                var off = LExpr.Constant((int)(name == "prev" ? -offset : offset));
                var other = new ScalarCompiler(_query, _schema, LExpr.Call(_window, "At", null, off), _group, _window);
                var value = Nullable(other.Compile(args[0]));
                RequiresClient |= other.RequiresClient;
                var fallback = args.Count > 2 ? ExpressionHelpers.Coerce(Compile(args[2]), value.Type) : LExpr.Constant(null, value.Type);
                return LExpr.Condition(LExpr.Call(_window, "Has", null, off), value, fallback);
            }
            case "row_number":
            {
                var start = args.Count > 0 ? ExpressionHelpers.Coerce(Compile(args[0]), typeof(long?)) : LExpr.Constant(1L, typeof(long?));
                return LExpr.Call(_window, "RowNumber", null, site, start, Restart(1));
            }
            case "row_cumsum":
            {
                var value = Compile(args[0]);
                var (method, type) = value.Type == typeof(decimal?) ? ("CumSumDecimal", typeof(decimal?))
                    : value.Type == typeof(double?) ? ("CumSumDouble", typeof(double?))
                    : ("CumSumLong", typeof(long?));
                return LExpr.Call(_window, method, null, site, ExpressionHelpers.Coerce(value, type), Restart(1));
            }
            default: // row_rank_dense / row_rank_min
            {
                var value = ExpressionHelpers.Coerce(Compile(args[0]), typeof(object));
                return LExpr.Call(_window, name == "row_rank_dense" ? "RankDense" : "RankMin", null, site, value, Restart(1));
            }
        }
    }

    private LExpr PackAll()
    {
        if (_schema is null || _row is null) throw new KqlException("pack_all() requires a row context.");
        RequiresClient = true;
        var args = _schema.Columns.SelectMany(c => new[]
        {
            (LExpr)LExpr.Constant(c.Name, typeof(object)),
            LExpr.Convert(c.Read(_row), typeof(object))
        });
        return LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.Pack))!, LExpr.NewArrayInit(typeof(object), args));
    }
}
