using System.Collections;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

/// <summary>A registered table: its source and row schema.</summary>
internal sealed record TableBinding(string Name, IKqlTableSource Source, RowSchema Schema);

/// <summary>
/// Compiles a bound <see cref="KustoCode"/> into a tree of <see cref="PlanNode"/>s.
/// </summary>
internal sealed partial class QueryCompiler
{
    private readonly KustoCode _code;
    private readonly IReadOnlyDictionary<string, TableBinding> _tables;
    private readonly IReadOnlyList<IKqlQueryTranslator> _translators;
    private readonly Dictionary<Symbol, PlanNode> _tabularLets = new();
    private readonly Dictionary<Symbol, LExpr> _scalarLets = new();
    private readonly Stack<Dictionary<string, object>> _parameters = new();

    public QueryCompiler(KustoCode code, IReadOnlyDictionary<string, TableBinding> tables, IReadOnlyList<IKqlQueryTranslator>? translators = null,
        FunctionRegistry? functions = null)
    {
        _code = code;
        _tables = tables;
        _translators = translators ?? [];
        Functions = functions ?? new FunctionRegistry();
    }

    private GlobalState Globals => _code.Globals;

    internal FunctionRegistry Functions { get; }

    /// <summary>Compiles every tabular expression statement (KQL can return several result sets).</summary>
    public IReadOnlyList<PlanNode> Compile()
    {
        if (_code.Syntax is not QueryBlock block)
            throw new KqlNotSupportedException(_code.Kind, 0);

        var results = new List<PlanNode>();
        foreach (var se in block.Statements)
        {
            switch (se.Element)
            {
                case LetStatement let:
                    CompileLet(let);
                    break;
                case ExpressionStatement { Expression: var e } when e.ResultType is TableSymbol:
                    results.Add(CompileTabular(e));
                    break;
                case ExpressionStatement es:
                    results.Add(ScalarAsTable(es.Expression));
                    break;
                case SetOptionStatement:
                case QueryParametersStatement:
                    break;
                default:
                    throw new KqlNotSupportedException(se.Element.Kind.ToString(), se.Element.TextStart);
            }
        }
        if (results.Count == 0) throw new KqlException("The query has no tabular expression to evaluate.");
        return results;
    }

    private void CompileLet(LetStatement let)
    {
        var symbol = let.Name.ReferencedSymbol;
        var expr = let.Expression;
        if (symbol is null) return;
        if (expr is FunctionDeclaration) return; // user functions are inlined at the call site
        if (expr.ResultType is TableSymbol)
        {
            _tabularLets[symbol] = CompileTabular(expr);
        }
        else if (expr.ResultType is ScalarSymbol)
        {
            var compiled = new ScalarCompiler(this, null, null).Compile(expr);
            _scalarLets[symbol] = compiled is System.Linq.Expressions.ConstantExpression
                ? compiled
                : LExpr.Constant(LExpr.Lambda(LExpr.Convert(compiled, typeof(object))).Compile().DynamicInvoke(), compiled.Type);
        }
    }

    internal bool TryGetScalarLet(Symbol symbol, out LExpr value) => _scalarLets.TryGetValue(symbol, out value!);

    internal bool TryGetParameter(string name, out LExpr value)
    {
        value = null!;
        if (_parameters.Count == 0 || !_parameters.Peek().TryGetValue(name, out var v) || v is not LExpr e) return false;
        value = e;
        return true;
    }

    internal bool IsAggregate(FunctionSymbol? symbol) => symbol is not null && Globals.IsAggregateFunction(symbol);

    internal bool IsBuiltIn(FunctionSymbol symbol) => Globals.IsBuiltInFunction(symbol) || Globals.IsAggregateFunction(symbol);

    /// <summary>Inlines a call to a user-defined (let) scalar function.</summary>
    internal LExpr InlineUserFunction(FunctionCallExpression call, ScalarCompiler caller)
    {
        var body = call.GetCalledFunctionBody() as FunctionBody
                   ?? throw new KqlNotSupportedException($"function {call.Name.SimpleName}()", call.TextStart);
        var signature = call.ReferencedSignature
                        ?? throw new KqlNotSupportedException($"function {call.Name.SimpleName}()", call.TextStart);

        var frame = new Dictionary<string, object>(StringComparer.Ordinal);
        var args = call.ArgumentList.Expressions.Select(e => e.Element).ToList();
        for (var i = 0; i < signature.Parameters.Count && i < args.Count; i++)
        {
            var p = signature.Parameters[i];
            frame[p.Name] = args[i].ResultType is TableSymbol ? CompileTabular(args[i]) : caller.Compile(args[i]);
        }

        _parameters.Push(frame);
        try
        {
            foreach (var st in body.Statements)
                if (st.Element is LetStatement let) CompileLet(let);
            return caller.Compile(body.Expression);
        }
        finally
        {
            _parameters.Pop();
        }
    }

    // ------------------------------------------------------------------ tabular

    internal PlanNode CompileTabular(KExpr node)
    {
        switch (node)
        {
            case ParenthesizedExpression p:
                return CompileTabular(p.Expression);
            case NameReference nr:
                return ResolveTable(nr);
            case PipeExpression pipe:
                return ApplyOperator(CompileTabular(pipe.Expression), pipe.Operator);
            case DataTableExpression dt:
                return DataTable(dt);
            case PrintOperator print:
                return Print(print);
            case RangeOperator range:
                return Range(range);
            case UnionOperator union:
                return Union(null, union);
            case FunctionCallExpression fc when fc.Name.SimpleName is "table" && fc.ArgumentList.Expressions.Count > 0
                                                && fc.ArgumentList.Expressions[0].Element is LiteralExpression { LiteralValue: string tableName }:
                return Table(tableName, fc);
            case FunctionCallExpression fc when fc.ReferencedSymbol is FunctionSymbol fs && !IsBuiltIn(fs):
                return InlineTabularFunction(fc);
            case MaterializeExpression m:
                return Materialize(CompileTabular(m.Expression));
            case ToTableExpression tt:
                return CompileTabular(tt.Expression);
            default:
                throw new KqlNotSupportedException(node.Kind.ToString(), node.TextStart);
        }
    }

    private PlanNode ResolveTable(NameReference nr)
    {
        var symbol = nr.ReferencedSymbol;
        if (symbol is not null && _tabularLets.TryGetValue(symbol, out var let)) return let;
        if (symbol is ParameterSymbol && _parameters.Count > 0 && _parameters.Peek().TryGetValue(nr.SimpleName, out var pv) && pv is PlanNode pn)
            return pn;
        return Table(nr.SimpleName, nr);
    }

    private PlanNode Table(string name, SyntaxNode at)
    {
        if (_tables.TryGetValue(name, out var t)) return new SourceNode(t.Schema, t.Source, TranslatorFor(t.Source));
        throw new KqlException($"Table '{name}' is not registered (position {at.TextStart}).");
    }

    private IKqlQueryTranslator? TranslatorFor(IKqlTableSource source)
    {
        if (_translators.Count == 0) return null;
        var query = source.GetQuery(); // builds the query only; nothing is executed
        return ExpressionHelpers.IsInMemory(query) ? null : _translators.FirstOrDefault(t => t.CanTranslate(query));
    }

    /// <summary>
    /// Builds an operator without, and (when the input has a translator) with, provider translations. The translated
    /// form is used only if it needs no in-memory evaluation and the input really is provider-backed when the query
    /// runs, so provider-only expressions (e.g. EF.Functions) never execute in memory.
    /// </summary>
    private OperatorNode Translated(PlanNode input, Func<IKqlQueryTranslator?, OperatorNode> build)
    {
        var plain = build(null);
        if (input.Translator is not { } translator) return plain;
        var translated = build(translator);
        return translated.RequiresClient ? plain : Adaptive(input, plain, translated.ApplyTo, plain.ApplyTo);
    }

    /// <summary>An operator that picks its translated or plain form from the input it actually receives.</summary>
    private static OperatorNode Adaptive(PlanNode input, OperatorNode plain, Func<IQueryable, IQueryable> translated, Func<IQueryable, IQueryable> fallback,
        bool keepsProvider = true) =>
        new(input, plain.Schema, false, q => ExpressionHelpers.IsInMemory(q) ? fallback(q) : translated(q), plain.Name, keepsProvider);

    private PlanNode InlineTabularFunction(FunctionCallExpression call)
    {
        var body = call.GetCalledFunctionBody() as FunctionBody
                   ?? throw new KqlNotSupportedException($"function {call.Name.SimpleName}()", call.TextStart);
        var signature = call.ReferencedSignature!;
        var frame = new Dictionary<string, object>(StringComparer.Ordinal);
        var args = call.ArgumentList.Expressions.Select(e => e.Element).ToList();
        for (var i = 0; i < signature.Parameters.Count && i < args.Count; i++)
        {
            frame[signature.Parameters[i].Name] = args[i].ResultType is TableSymbol
                ? CompileTabular(args[i])
                : new ScalarCompiler(this, null, null).Compile(args[i]);
        }
        _parameters.Push(frame);
        try
        {
            foreach (var st in body.Statements)
                if (st.Element is LetStatement let) CompileLet(let);
            return CompileTabular(body.Expression);
        }
        finally
        {
            _parameters.Pop();
        }
    }

    /// <summary>Evaluates a tabular expression now and returns the first column of the first row.</summary>
    internal object? EvaluateScalar(KExpr tabularOrScalar)
    {
        if (tabularOrScalar.ResultType is not TableSymbol)
        {
            var e = new ScalarCompiler(this, null, null).Compile(tabularOrScalar);
            return LExpr.Lambda(LExpr.Convert(e, typeof(object))).Compile().DynamicInvoke();
        }
        return EvaluateColumn(tabularOrScalar).FirstOrDefault();
    }

    /// <summary>Evaluates a tabular expression now and returns the values of its first column.</summary>
    internal IReadOnlyList<object?> EvaluateColumn(KExpr tabular)
    {
        var plan = CompileTabular(tabular);
        var reader = RowReader.For(plan.Schema);
        var values = new List<object?>();
        foreach (var row in plan.Execute()) values.Add(reader(row)[0]);
        return values;
    }

    private PlanNode Materialize(PlanNode input)
    {
        IList? cache = null;
        return new RowsNode(input.Schema, () => cache ??= input.Execute().Cast<object>().ToList());
    }

    private PlanNode ScalarAsTable(KExpr expr)
    {
        var value = EvaluateScalar(expr);
        var schema = RowSchema.Emit([("print_0", RowSchema.AsScalar(expr.ResultType))]);
        return new RowsNode(schema, () => new[] { schema.Constructor!.Invoke([value]) });
    }
}
