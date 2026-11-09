using System.Collections;
using Kql.Net.Runtime;

namespace Kql.Net.Compilation;

/// <summary>
/// A node in the compiled logical plan. Executing a node yields an <see cref="IQueryable"/> whose element type
/// is <see cref="Schema"/>.RowType. Nodes are lazy: nothing touches the data until <see cref="Execute"/>.
/// </summary>
internal abstract class PlanNode(RowSchema schema)
{
    public RowSchema Schema { get; } = schema;

    public abstract IQueryable Execute();
}

/// <summary>A table registered with the context.</summary>
internal sealed class SourceNode(RowSchema schema, IKqlTableSource source) : PlanNode(schema)
{
    public override IQueryable Execute() => source.GetQuery();
}

/// <summary>An in-memory row set (datatable, print, range, count results...).</summary>
internal sealed class RowsNode(RowSchema schema, Func<IEnumerable> rows) : PlanNode(schema)
{
    public override IQueryable Execute() => ExpressionHelpers.ToClientQuery(rows(), Schema.RowType);
}

/// <summary>
/// Applies an operator to its input. When <paramref name="requiresClient"/> is set and the input is
/// provider-backed (e.g. EF Core), the input is first materialised so the operator runs in memory —
/// everything before this point is still translated by the provider.
/// </summary>
internal sealed class OperatorNode(PlanNode input, RowSchema schema, bool requiresClient, Func<IQueryable, IQueryable> apply, string name)
    : PlanNode(schema)
{
    public PlanNode Input { get; } = input;
    public bool RequiresClient { get; } = requiresClient;
    public string Name { get; } = name;

    public override IQueryable Execute()
    {
        var q = Input.Execute();
        if (RequiresClient && !ExpressionHelpers.IsInMemory(q))
            q = ExpressionHelpers.ToClientQuery(q, Input.Schema.RowType);
        return apply(q);
    }

    public override string ToString() => $"{Input} | {Name}";
}

/// <summary>Combines several inputs (join, union...).</summary>
internal sealed class CombineNode(IReadOnlyList<PlanNode> inputs, RowSchema schema, Func<IReadOnlyList<IQueryable>, IQueryable> apply)
    : PlanNode(schema)
{
    public override IQueryable Execute() => apply(inputs.Select(i => i.Execute()).ToList());
}
