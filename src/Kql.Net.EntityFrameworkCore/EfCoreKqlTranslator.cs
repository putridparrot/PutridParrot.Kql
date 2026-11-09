using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Kql.Net.EntityFrameworkCore;

/// <summary>
/// Translates KQL operators and functions into forms Entity Framework Core can turn into SQL, using only
/// provider-neutral building blocks (string methods, <c>ToLower</c>, <c>Math</c>, <c>DateTime</c> members,
/// <c>EF.Functions.Like</c>).
/// </summary>
/// <remarks>
/// Case-sensitive operators (<c>contains_cs</c>, <c>==</c>...) map to plain string methods, so the database
/// collation decides case sensitivity (e.g. SQL Server's default collation is case-insensitive).
/// <c>has</c> is translated as a substring match: a superset of KQL's whole-term semantics, which is why
/// it's marked approximate and re-checked in memory.
/// </remarks>
public sealed class EfCoreKqlTranslator : IKqlQueryTranslator
{
    public static readonly EfCoreKqlTranslator Instance = new();

    private static readonly MethodInfo ToLowerMethod = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo ToUpperMethod = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;
    private static readonly MethodInfo ContainsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StartsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo EndsWithMethod = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo CompareMethod = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo ConcatMethod = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string)])!;
    private static readonly MethodInfo FloorMethod = typeof(Math).GetMethod(nameof(Math.Floor), [typeof(double)])!;
    private static readonly MethodInfo RoundMethod = typeof(Math).GetMethod(nameof(Math.Round), [typeof(double), typeof(int)])!;
    private static readonly MethodInfo AbsDouble = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!;
    private static readonly MethodInfo AbsLong = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(long)])!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(typeof(string));

    public bool CanTranslate(IQueryable query) => query.Provider is IAsyncQueryProvider;

    public KqlTranslation? TranslateStringOperator(KqlStringOperator op, bool caseSensitive, Expression left, Expression right)
    {
        Expression Fold(Expression e) => caseSensitive ? e : Lower(e);

        switch (op)
        {
            case KqlStringOperator.EqualsIgnoreCase:
                return new(Expression.Equal(Lower(left), Lower(right)));
            case KqlStringOperator.Contains:
                return new(Expression.Call(Fold(left), ContainsMethod, Fold(right)));
            case KqlStringOperator.StartsWith:
                return new(Expression.Call(Fold(left), StartsWithMethod, Fold(right)));
            case KqlStringOperator.EndsWith:
                return new(Expression.Call(Fold(left), EndsWithMethod, Fold(right)));
            case KqlStringOperator.Has:
            case KqlStringOperator.HasPrefix:
            case KqlStringOperator.HasSuffix:
                return new(Expression.Call(Fold(left), ContainsMethod, Fold(right)), IsApproximate: true);
            case KqlStringOperator.HasAny when right is NewArrayExpression any:
                return new(any.Expressions.Select(t => (Expression)Expression.Call(Lower(left), ContainsMethod, Lower(t)))
                    .Aggregate((Expression)Expression.Constant(false), Expression.OrElse), IsApproximate: true);
            case KqlStringOperator.HasAll when right is NewArrayExpression all:
                return new(all.Expressions.Select(t => (Expression)Expression.Call(Lower(left), ContainsMethod, Lower(t)))
                    .Aggregate((Expression)Expression.Constant(true), Expression.AndAlso), IsApproximate: true);
            case KqlStringOperator.InIgnoreCase when right is NewArrayExpression list && list.Expressions.All(e => e is ConstantExpression):
                var values = list.Expressions.Select(e => (((ConstantExpression)e).Value as string)?.ToLowerInvariant()).ToArray();
                return new(Expression.Call(EnumerableContains, Expression.Constant(values), Lower(left)));
            case KqlStringOperator.Like when !caseSensitive:
                return new(Expression.Call(LikeMethod, Expression.Constant(EF.Functions), Lower(left), Lower(right)));
            case KqlStringOperator.Compare:
                return new(Expression.Call(CompareMethod, left, right));
            default:
                return null; // matches regex, like_cs, ...: evaluate in memory
        }
    }

    public KqlTranslation? TranslateFunction(string name, IReadOnlyList<Expression> args, Type resultType)
    {
        switch (name)
        {
            case "tolower" when IsString(args, 1):
                return new(Expression.Call(args[0], ToLowerMethod));
            case "toupper" when IsString(args, 1):
                return new(Expression.Call(args[0], ToUpperMethod));
            case "strlen" when IsString(args, 1):
                return new(Expression.Condition(Expression.Equal(args[0], Expression.Constant(null, typeof(string))),
                    Expression.Constant(0L, typeof(long?)),
                    Expression.Convert(Expression.Property(args[0], nameof(string.Length)), typeof(long?))));
            case "substring" when args.Count is 2 or 3 && args[0].Type == typeof(string) && args.Skip(1).All(IsConstant):
            {
                var ints = args.Skip(1).Select(a => (Expression)Expression.Constant(Convert.ToInt32(((ConstantExpression)a).Value))).ToArray();
                var method = typeof(string).GetMethod(nameof(string.Substring), ints.Select(_ => typeof(int)).ToArray())!;
                return new(Expression.Call(args[0], method, ints));
            }
            case "strcat" when args.Count > 0 && args.All(a => a.Type == typeof(string)):
                return new(args.Select(a => (Expression)Expression.Coalesce(a, Expression.Constant("")))
                    .Aggregate((a, b) => Expression.Call(ConcatMethod, a, b)));
            case "abs" when args.Count == 1 && IsType(args[0], typeof(long)):
                return new(Expression.Call(AbsLong, Expression.Convert(args[0], typeof(long))));
            case "abs" when args.Count == 1 && IsType(args[0], typeof(double)):
                return new(Expression.Call(AbsDouble, Expression.Convert(args[0], typeof(double))));
            case "round" when args.Count is 1 or 2 && IsType(args[0], typeof(double)) && args.Skip(1).All(IsConstant):
            {
                var digits = args.Count == 2 ? Convert.ToInt32(((ConstantExpression)args[1]).Value) : 0;
                return new(Expression.Call(RoundMethod, Expression.Convert(args[0], typeof(double)), Expression.Constant(digits)));
            }
            case "floor" when args.Count == 1 && IsType(args[0], typeof(double)):
                return new(Expression.Call(FloorMethod, Expression.Convert(args[0], typeof(double))));
            case "bin" or "floor" when args.Count == 2:
                return Bin(args[0], args[1]);
            case "startofday" when args.Count == 1 && IsType(args[0], typeof(DateTime)):
                return new(Expression.Property(Expression.Convert(args[0], typeof(DateTime)), nameof(DateTime.Date)));
            case "getyear" when IsDate(args):
                return DatePart(args[0], nameof(DateTime.Year));
            case "getmonth" or "monthofyear" when IsDate(args):
                return DatePart(args[0], nameof(DateTime.Month));
            case "dayofmonth" when IsDate(args):
                return DatePart(args[0], nameof(DateTime.Day));
            case "dayofyear" when IsDate(args):
                return DatePart(args[0], nameof(DateTime.DayOfYear));
            case "hourofday" when IsDate(args):
                return DatePart(args[0], nameof(DateTime.Hour));
            default:
                return null;
        }
    }

    private static KqlTranslation? Bin(Expression value, Expression size)
    {
        if (size is not ConstantExpression { Value: { } sizeValue }) return null;

        if (IsType(value, typeof(DateTime)) && sizeValue is TimeSpan span)
        {
            // only whole days have a portable translation
            return span == TimeSpan.FromDays(1)
                ? new(Expression.Property(Expression.Convert(value, typeof(DateTime)), nameof(DateTime.Date)))
                : null;
        }

        if (IsType(value, typeof(long)) || IsType(value, typeof(int)))
        {
            var step = Convert.ToInt64(sizeValue);
            if (step == 0) return null;
            var floor = Expression.Call(FloorMethod, Expression.Divide(Expression.Convert(value, typeof(double)), Expression.Constant((double)step)));
            return new(Expression.Multiply(Expression.Convert(floor, typeof(long)), Expression.Constant(step)));
        }

        if (IsType(value, typeof(double)))
        {
            var step = Convert.ToDouble(sizeValue);
            if (step == 0) return null;
            var floor = Expression.Call(FloorMethod, Expression.Divide(Expression.Convert(value, typeof(double)), Expression.Constant(step)));
            return new(Expression.Multiply(floor, Expression.Constant(step)));
        }

        return null;
    }

    private static KqlTranslation DatePart(Expression value, string member) =>
        new(Expression.Property(Expression.Convert(value, typeof(DateTime)), member));

    private static Expression Lower(Expression e) => e is ConstantExpression { Value: string s }
        ? Expression.Constant(s.ToLowerInvariant())
        : Expression.Call(e, ToLowerMethod);

    private static bool IsConstant(Expression e) => e is ConstantExpression { Value: not null };

    private static bool IsString(IReadOnlyList<Expression> args, int count) => args.Count == count && args.All(a => a.Type == typeof(string));

    private static bool IsDate(IReadOnlyList<Expression> args) => args.Count == 1 && IsType(args[0], typeof(DateTime));

    private static bool IsType(Expression e, Type t) => (Nullable.GetUnderlyingType(e.Type) ?? e.Type) == t;
}
