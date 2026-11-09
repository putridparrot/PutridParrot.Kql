using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;

namespace Kql.Net.Compilation;

// Row-level operators scheduled for Phase 5 of the plan.
internal sealed partial class QueryCompiler
{
    private PlanNode MvExpand(PlanNode input, MvExpandOperator op, TableSymbol output) =>
        throw new KqlNotSupportedException("mv-expand", op.TextStart);

    private PlanNode Parse(PlanNode input, ParseOperator op, TableSymbol output) =>
        throw new KqlNotSupportedException("parse", op.TextStart);

    private PlanNode Serialize(PlanNode input, SerializeOperator op, TableSymbol output) =>
        throw new KqlNotSupportedException("serialize with expressions", op.TextStart);
}
