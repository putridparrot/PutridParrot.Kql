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

    /// <summary>
    /// The translator for the provider this node's output still belongs to, or null once the pipeline has moved
    /// to in-memory evaluation (or the source has no translator).
    /// </summary>
    public virtual IKqlQueryTranslator? Translator => null;

    public abstract IQueryable Execute();
}

/// <summary>A table registered with the context.</summary>
internal sealed class SourceNode(RowSchema schema, IKqlTableSource source, IKqlQueryTranslator? translator) : PlanNode(schema)
{
    public override IKqlQueryTranslator? Translator => translator;

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
/// <param name="keepsProvider">
/// False for operators that always produce in-memory output (count, summarize without by...), even though they
/// can consume a provider-backed input. Defaults to <c>!requiresClient</c>.
/// </param>
internal sealed class OperatorNode(PlanNode input, RowSchema schema, bool requiresClient, Func<IQueryable, IQueryable> apply, string name,
    bool? keepsProvider = null)
    : PlanNode(schema)
{
    public PlanNode Input { get; } = input;
    public bool RequiresClient { get; } = requiresClient;
    public string Name { get; } = name;

    public override IKqlQueryTranslator? Translator => (keepsProvider ?? !RequiresClient) ? Input.Translator : null;

    public override IQueryable Execute() => ApplyTo(Input.Execute());

    /// <summary>Applies this operator to an already-executed input query.</summary>
    public IQueryable ApplyTo(IQueryable q)
    {
        if (RequiresClient && !ExpressionHelpers.IsInMemory(q))
            q = ExpressionHelpers.ToClientQuery(q, Input.Schema.RowType);
        return apply(q);
    }

    public override string ToString() => $"{Input} | {Name}";
}

/// <summary>Combines several inputs (join, union...).</summary>
/// <param name="translator">The provider translator that still applies to the output, when the combination can run in the provider.</param>
internal sealed class CombineNode(IReadOnlyList<PlanNode> inputs, RowSchema schema, Func<IReadOnlyList<IQueryable>, IQueryable> apply,
    IKqlQueryTranslator? translator = null)
    : PlanNode(schema)
{
    public override IKqlQueryTranslator? Translator => translator;

    public override IQueryable Execute() => apply(inputs.Select(i => i.Execute()).ToList());
}
