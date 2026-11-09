using System.Collections;
using System.Text.RegularExpressions;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

// search and find: run a predicate over one or more tables, tagging rows with their source table. In memory.
internal sealed partial class QueryCompiler
{
    /// <summary>The tables named by `in (...)` (wildcards allowed), or every registered table.</summary>
    private List<(PlanNode Plan, string Name)> SearchTables(FindInClause? inClause)
    {
        if (inClause is null)
            return _tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => (Table(t.Name, inClause!), t.Name)).ToList();

        var result = new List<(PlanNode, string)>();
        foreach (var e in inClause.Expressions.Select(x => x.Element))
        {
            var text = e.ToString().Trim();
            if (text.Contains('*'))
            {
                var regex = new Regex("^" + Regex.Escape(text).Replace(@"\*", ".*") + "$");
                result.AddRange(_tables.Keys.Where(k => regex.IsMatch(k)).Order(StringComparer.Ordinal).Select(k => (Table(k, e), k)));
            }
            else
            {
                result.Add((CompileTabular(e), e is NameReference n ? n.SimpleName : text));
            }
        }
        return result;
    }

    /// <summary>Projects each input onto the union of all columns (plus a source-name column) and concatenates them.</summary>
    private PlanNode Concatenate(List<(PlanNode Plan, string Name)> inputs, string sourceColumn)
    {
        var columns = new List<(string Name, ScalarSymbol Type)> { (sourceColumn, ScalarTypes.String) };
        foreach (var (plan, _) in inputs)
        {
            foreach (var c in plan.Schema.Columns)
            {
                var i = columns.FindIndex(x => x.Name == c.Name);
                if (i < 0) columns.Add((c.Name, c.KqlType));
                else if (columns[i].Type != c.KqlType) columns[i] = (c.Name, ScalarTypes.Dynamic);
            }
        }

        var schema = RowSchema.Emit(columns);
        var projected = inputs.Select(input => (PlanNode)Select(input.Plan, columns, (compiler, row) => columns.Select(c =>
            c.Name == sourceColumn ? LExpr.Constant(input.Name, typeof(string))
            : input.Plan.Schema.Find(c.Name) is { } col ? ExpressionHelpers.Coerce(compiler.Read(col, row), KqlTypes.ClrTypeFor(c.Type))
            : (LExpr)LExpr.Constant(null, KqlTypes.ClrTypeFor(c.Type))).ToList(), "search")).ToList();

        return new CombineNode(projected, schema, queries =>
            ExpressionHelpers.ToClientQuery(queries.SelectMany(q => q.Cast<object>()).CastTo(schema.RowType), schema.RowType));
    }

    private PlanNode SearchWhere(PlanNode input, KExpr condition, bool caseSensitive, string excluded)
    {
        var p = RowParam(input.Schema);
        var compiler = new ScalarCompiler(this, input.Schema, p)
        {
            SearchCaseSensitive = caseSensitive,
            SearchExcludedColumns = new HashSet<string> { excluded }
        };
        var lambda = Lambda(compiler.CompilePredicate(condition), p);
        return new OperatorNode(input, input.Schema, true,
            q => ExpressionHelpers.CallQueryable(q, "Where", [input.Schema.RowType], LExpr.Quote(lambda)), "search");
    }

    private PlanNode Search(PlanNode? input, SearchOperator op)
    {
        var caseSensitive = string.Equals(ParameterValue(op.Parameters, "kind"), "case_sensitive", StringComparison.OrdinalIgnoreCase);
        var tables = input is not null ? [(input, "")] : SearchTables(op.InClause);
        var all = Concatenate(tables, "$table");
        var filtered = SearchWhere(all, op.Condition, caseSensitive, "$table");
        return Reshape(filtered, (TableSymbol)op.ResultType, new Dictionary<string, string>(), "search");
    }

    private PlanNode Find(FindOperator op)
    {
        var tables = SearchTables(op.InClause);
        var all = Concatenate(tables, "source_");
        var filtered = SearchWhere(all, op.Condition, false, "source_");

        // output: source_, the projected (or common) columns, and pack_ holding the remaining non-null columns
        var output = (TableSymbol)op.ResultType;
        var listed = output.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var packed = filtered.Schema.Columns.Where(c => !listed.Contains(c.Name)).ToList();
        return Select(filtered, Columns(output), (compiler, row) => output.Columns.Select(c =>
        {
            if (c.Name == "pack_" && filtered.Schema.Find("pack_") is null)
            {
                var pairs = packed.SelectMany(pc => new[] { (LExpr)LExpr.Constant(pc.Name, typeof(object)), LExpr.Convert(compiler.Read(pc, row), typeof(object)) });
                return (LExpr)LExpr.Call(typeof(KqlFunctions).GetMethod(nameof(KqlFunctions.PackNonNull))!, LExpr.NewArrayInit(typeof(object), pairs));
            }
            return filtered.Schema.Find(c.Name) is { } col ? compiler.Read(col, row) : LExpr.Constant(null, KqlTypes.ClrTypeFor(c.Type));
        }).ToList(), "find");
    }
}
