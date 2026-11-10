using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using PutridParrot.Kql.Runtime;
using PutridParrot.Kql.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = PutridParrot.Kql.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace PutridParrot.Kql.Compilation;

// Row-level operators: mv-expand, parse, parse-where. These always run in memory.
internal sealed partial class QueryCompiler
{
    private PlanNode MvExpand(PlanNode input, MvExpandOperator op, TableSymbol output)
    {
        var bagAsArray = string.Equals(ParameterValue(op.Parameters, "bagexpansion"), "array", StringComparison.OrdinalIgnoreCase);
        var indexColumn = ParameterValue(op.Parameters, "with_itemindex");
        long? limit = op.RowLimitClause is { } lc ? KqlConvert.ToLong(EvaluateScalar(lc.RowLimit)) : null;

        var rowType = input.Schema.RowType;
        var row = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, row);

        // the values to expand, and which output column receives each
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        var values = new List<LExpr>();
        foreach (var mv in op.Expressions.Select(x => x.Element))
        {
            var (name, value) = mv.Expression switch
            {
                SimpleNamedExpression sn => (sn.Name.SimpleName, sn.Expression),
                NameReference n => (n.SimpleName, (KExpr)n),
                var other => throw new KqlNotSupportedException("mv-expand of an unnamed expression", other.TextStart)
            };
            targets[name] = values.Count;
            values.Add(ExpressionHelpers.Coerce(compiler.Compile(value), typeof(object)));
        }
        var valuesFn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(object[])),
            LExpr.NewArrayInit(typeof(object), values), row).Compile();

        // output row: (row, items, index) => new Out(...)
        var outSchema = RowSchema.Emit(Columns(output));
        var items = LExpr.Parameter(typeof(object[]), "items");
        var index = LExpr.Parameter(typeof(long), "i");
        var outValues = output.Columns.Select(c =>
            targets.TryGetValue(c.Name, out var t) ? ExpressionHelpers.Coerce(LExpr.ArrayIndex(items, LExpr.Constant(t)), KqlTypes.ClrTypeFor(c.Type))
            : c.Name == indexColumn ? LExpr.Convert(index, typeof(long?))
            : compiler.Read(input.Schema.Get(c.Name), row)).ToList();
        var make = LExpr.Lambda(typeof(Func<,,,>).MakeGenericType(rowType, typeof(object[]), typeof(long), outSchema.RowType),
            outSchema.New(outValues), row, items, index).Compile();

        var expand = typeof(RowOperators).GetMethod(nameof(RowOperators.MvExpand))!.MakeGenericMethod(rowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q =>
            ExpressionHelpers.ToClientQuery((IEnumerable)expand.Invoke(null, [q, valuesFn, make, bagAsArray, limit])!, outSchema.RowType), "mv-expand");
    }

    private PlanNode Parse(PlanNode input, KExpr source, SyntaxList<NamedParameter> parameters, SyntaxList<SyntaxNode> patterns,
        TableSymbol output, bool whereOnly)
    {
        var kind = (ParameterValue(parameters, "kind") ?? "simple").ToLowerInvariant();
        var flags = ParameterValue(parameters, "flags") ?? "";
        if (kind is not ("simple" or "relaxed" or "regex"))
            throw new KqlNotSupportedException($"parse kind={kind}", source.TextStart);

        // Build a regex with named groups k0..kN for the extracted columns.
        var regex = new StringBuilder("^");
        var columns = new List<(string Name, Type Type)>();
        var elements = patterns.ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            var last = i == elements.Count - 1;
            switch (elements[i])
            {
                case StarExpression:
                    regex.Append(".*?");
                    break;
                case LiteralExpression { LiteralValue: string text }:
                    regex.Append(kind == "regex" ? text : Regex.Escape(text));
                    break;
                case NameDeclaration nd:
                    regex.Append($"(?<k{columns.Count}>{(last ? ".*" : ".*?")})");
                    columns.Add((nd.SimpleName, typeof(string)));
                    break;
                case NameAndTypeDeclaration nt:
                    var type = KqlTypes.ClrTypeFor(output.GetColumn(nt.Name.SimpleName).Type);
                    regex.Append($"(?<k{columns.Count}>{(kind == "relaxed" ? (last ? ".*" : ".*?") : TypePattern(type, last))})");
                    columns.Add((nt.Name.SimpleName, type));
                    break;
                default:
                    throw new KqlNotSupportedException($"parse pattern element '{elements[i]}'", elements[i].TextStart);
            }
        }

        var options = RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        var compiledRegex = new Regex(regex.ToString(), options);

        var rowType = input.Schema.RowType;
        var row = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, row);
        var sourceFn = LExpr.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(string)),
            ExpressionHelpers.Coerce(compiler.Compile(source), typeof(string)), row).Compile();

        var outSchema = RowSchema.Emit(Columns(output));
        var parsed = LExpr.Parameter(typeof(object[]), "values");
        var targets = columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        var outValues = output.Columns.Select(c => targets.TryGetValue(c.Name, out var t)
            ? ExpressionHelpers.Coerce(LExpr.ArrayIndex(parsed, LExpr.Constant(t)), KqlTypes.ClrTypeFor(c.Type))
            : compiler.Read(input.Schema.Get(c.Name), row)).ToList();
        var make = LExpr.Lambda(typeof(Func<,,>).MakeGenericType(rowType, typeof(object[]), outSchema.RowType),
            outSchema.New(outValues), row, parsed).Compile();

        var types = columns.Select(c => c.Type).ToArray();
        var relaxed = kind == "relaxed";
        var apply = typeof(RowOperators).GetMethod(nameof(RowOperators.Parse))!.MakeGenericMethod(rowType, outSchema.RowType);
        return new OperatorNode(input, outSchema, true, q =>
            ExpressionHelpers.ToClientQuery((IEnumerable)apply.Invoke(null, [q, sourceFn, compiledRegex, types, relaxed, make, whereOnly])!,
                outSchema.RowType), whereOnly ? "parse-where" : "parse");
    }

    /// <summary>The capture pattern for a typed parse column (simple/regex modes).</summary>
    private static string TypePattern(Type type, bool last)
    {
        var core = Nullable.GetUnderlyingType(type) ?? type;
        if (core == typeof(long) || core == typeof(int)) return @"-?\d+";
        if (core == typeof(double) || core == typeof(decimal)) return @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?";
        if (core == typeof(bool)) return "(?i:true|false)";
        return last ? ".*" : ".*?";
    }
}
