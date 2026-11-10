using System.Collections;
using Kql.Net.Runtime;
using Kql.Net.Schema;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RowSchema = Kql.Net.Runtime.RowSchema;
using KExpr = Kusto.Language.Syntax.Expression;
using LExpr = System.Linq.Expressions.Expression;

namespace Kql.Net.Compilation;

// `evaluate` plugins. bag_unpack and pivot produce columns that depend on the data, so their input is read when the
// query is compiled (the output schema must be known to compile the operators that follow).
internal sealed partial class QueryCompiler
{
    private PlanNode Evaluate(PlanNode input, EvaluateOperator op, TableSymbol output)
    {
        MarkNotCacheable(); // the output columns come from the data
        var name = op.FunctionCall.Name.SimpleName;
        var args = op.FunctionCall.ArgumentList.Expressions.Select(e => e.Element).ToList();
        return name switch
        {
            "bag_unpack" => BagUnpack(input, args, output, op),
            "pivot" => Pivot(input, args, output),
            "narrow" => Narrow(input),
            _ => throw new KqlNotSupportedException($"evaluate {name}", op.TextStart)
        };
    }

    private static List<object> ReadAll(PlanNode input) => input.Execute().Cast<object>().ToList();

    private static RowsNode Rows(IReadOnlyList<(string Name, ScalarSymbol Type)> columns, IEnumerable<object?[]> values)
    {
        var schema = RowSchema.Emit(columns);
        var create = RowFactory.For(schema);
        var types = schema.Columns.Select(c => c.ClrType).ToArray();
        var rows = values.Select(v => create(types.Select((t, i) =>
            t == typeof(object) ? Dynamic.Normalize(v[i]) : KqlConvert.ChangeType(v[i], t)).ToArray())).ToList();
        return new RowsNode(schema, () => rows.CastTo(schema.RowType));
    }

    // ------------------------------------------------------------------ bag_unpack

    private PlanNode BagUnpack(PlanNode input, List<KExpr> args, TableSymbol output, EvaluateOperator op)
    {
        if (args.Count == 0 || args[0] is not NameReference bagRef)
            throw new KqlNotSupportedException("bag_unpack of a non-column expression", op.TextStart);
        var prefix = args.Count > 1 ? EvaluateScalar(args[1]) as string ?? "" : "";
        var conflict = args.Count > 2 ? EvaluateScalar(args[2]) as string ?? "error" : "error";
        var ignored = args.Count > 3 ? Dynamic.Elements(EvaluateScalar(args[3])).Select(KqlConvert.ToStringValue).ToHashSet() : [];

        var reader = RowReader.For(input.Schema);
        var bagIndex = input.Schema.Columns.ToList().FindIndex(c => c.Name == bagRef.SimpleName);
        var sourceCols = input.Schema.Columns.Where((_, i) => i != bagIndex).ToList();
        var rows = ReadAll(input).Select(r => reader(r)).ToList();
        var bags = rows.Select(r => Dynamic.Normalize(r[bagIndex]) as Dictionary<string, object?>).ToList();

        // explicit output schema (`evaluate bag_unpack(d) : (a:long, ...)`): take each column from the row or the bag
        if (!output.IsOpen)
        {
            var cols = output.Columns.Select(c => (c.Name, RowSchema.AsScalar(c.Type))).ToList();
            return Rows(cols, rows.Select((r, ri) => cols.Select(c =>
            {
                var si = sourceCols.FindIndex(s => s.Name == c.Name);
                if (si >= 0) return r[input.Schema.Columns.ToList().IndexOf(sourceCols[si])];
                return bags[ri] is { } bag && bag.TryGetValue(c.Name.StartsWith(prefix) ? c.Name[prefix.Length..] : c.Name, out var v) ? v : null;
            }).ToArray()));
        }

        // property names in first-seen order, with a type inferred from their values
        var props = new List<string>();
        var types = new Dictionary<string, ScalarSymbol?>();
        foreach (var bag in bags.OfType<Dictionary<string, object?>>())
        {
            foreach (var (key, value) in bag)
            {
                if (ignored.Contains(key)) continue;
                if (!types.ContainsKey(key))
                {
                    props.Add(key);
                    types[key] = null;
                }
                if (value is null) continue;
                var t = value is List<object?> or Dictionary<string, object?> ? ScalarTypes.Dynamic : KqlTypes.FromClr(value.GetType()).KqlType;
                types[key] = types[key] is null || types[key] == t ? t
                    : (types[key] == ScalarTypes.Long && t == ScalarTypes.Real) || (types[key] == ScalarTypes.Real && t == ScalarTypes.Long) ? ScalarTypes.Real
                    : ScalarTypes.Dynamic;
            }
        }

        var columns = sourceCols.Select(c => (c.Name, c.KqlType)).ToList();
        var propColumns = new List<(string Column, string Property)>();
        foreach (var p in props)
        {
            var colName = prefix + p;
            var existing = columns.FindIndex(c => c.Name == colName);
            if (existing >= 0)
            {
                switch (conflict)
                {
                    case "keep_source": continue;
                    case "replace_source": columns.RemoveAt(existing); break;
                    default: throw new KqlException($"bag_unpack: property '{p}' conflicts with an existing column (use columnsConflict='replace_source' or 'keep_source').");
                }
            }
            columns.Add((colName, types[p] ?? ScalarTypes.Dynamic));
            propColumns.Add((colName, p));
        }

        var sourceIndex = sourceCols.ToDictionary(c => c.Name, c => input.Schema.Columns.ToList().IndexOf(c));
        var propIndex = propColumns.ToDictionary(x => x.Column, x => x.Property);
        return Rows(columns, rows.Select((r, ri) => columns.Select(c =>
            propIndex.TryGetValue(c.Name, out var p) ? (bags[ri] is { } bag && bag.TryGetValue(p, out var v) ? v : null)
            : r[sourceIndex[c.Name]]).ToArray()));
    }

    // ------------------------------------------------------------------ pivot

    private PlanNode Pivot(PlanNode input, List<KExpr> args, TableSymbol output)
    {
        var rowType = input.Schema.RowType;
        var pivotFn = AsObjectFunc(RowFunction(input, args[0]), rowType);
        var aggregate = args.Count > 1 ? AggregateOverRows(input, args[1]) : null;
        var aggType = args.Count > 1 ? RowSchema.AsScalar(args[1].ResultType) : ScalarTypes.Long;

        // group columns: the ones Kusto lists (by default every column not used by the pivot/aggregation)
        var groupCols = output.Columns.Select(c => input.Schema.Get(c.Name)).ToList();
        var reader = RowReader.For(input.Schema);
        var all = input.Schema.Columns.ToList();
        var groupIdx = groupCols.Select(c => all.IndexOf(c)).ToArray();
        var listType = typeof(List<>).MakeGenericType(rowType);

        var groups = new List<(object?[] Key, Dictionary<string, IList> ByPivot)>();
        var groupIndex = new Dictionary<object?[], int>(KeyComparer.Instance);
        var pivotValues = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in input.Execute())
        {
            var values = reader(row!);
            var key = new object?[groupIdx.Length];
            for (var i = 0; i < key.Length; i++) key[i] = values[groupIdx[i]];
            if (!groupIndex.TryGetValue(key, out var gi))
            {
                groupIndex[key] = gi = groups.Count;
                groups.Add((key, new Dictionary<string, IList>(StringComparer.Ordinal)));
            }
            var byPivot = groups[gi].ByPivot;
            var pv = KqlConvert.ToStringValue(pivotFn(row!)) ?? "";
            pivotValues.Add(pv);
            if (!byPivot.TryGetValue(pv, out var list)) byPivot[pv] = list = (IList)Activator.CreateInstance(listType)!;
            list.Add(row);
        }

        var aggregateFn = aggregate is null ? null : AsObjectFunc(aggregate, typeof(IEnumerable<>).MakeGenericType(rowType));
        var columns = groupCols.Select(c => (c.Name, c.KqlType)).Concat(pivotValues.Select(v => (v, aggType))).ToList();
        var empty = (IList)Activator.CreateInstance(listType)!;
        return Rows(columns, groups.Select(g => g.Key.Concat(pivotValues.Select(v =>
        {
            var bucket = g.ByPivot.TryGetValue(v, out var list) ? list : empty;
            return aggregateFn is null ? (object?)(long)bucket.Count : aggregateFn(bucket);
        })).ToArray()));
    }

    /// <summary>Wraps a compiled <c>Func&lt;T, object?&gt;</c> as <c>Func&lt;object, object?&gt;</c> without DynamicInvoke.</summary>
    private static Func<object, object?> AsObjectFunc(Delegate fn, Type argType)
    {
        var p = LExpr.Parameter(typeof(object), "x");
        var call = LExpr.Invoke(LExpr.Constant(fn), LExpr.Convert(p, argType));
        return LExpr.Lambda<Func<object, object?>>(call, p).Compile();
    }

    /// <summary>Hashes/compares group keys (value arrays) with KQL value equality.</summary>
    private sealed class KeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null || x.Length != y.Length) return x == y;
            for (var i = 0; i < x.Length; i++) if (!KqlComparer.ValueEquals(x[i], y[i])) return false;
            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = 17;
            foreach (var v in key) hash = hash * 31 + KqlComparer.ValueHash(v);
            return hash;
        }
    }

    // ------------------------------------------------------------------ narrow

    private PlanNode Narrow(PlanNode input)
    {
        var reader = RowReader.For(input.Schema);
        var names = input.Schema.Columns.Select(c => c.Name).ToArray();
        return new RowsNode(RowSchema.Emit([("Row", ScalarTypes.Long), ("Column", ScalarTypes.String), ("Value", ScalarTypes.String)]), () =>
        {
            var schema = RowSchema.Emit([("Row", ScalarTypes.Long), ("Column", ScalarTypes.String), ("Value", ScalarTypes.String)]);
            var create = RowFactory.For(schema);
            var result = new List<object>();
            long i = 0;
            foreach (var row in input.Execute())
            {
                var values = reader(row);
                for (var c = 0; c < names.Length; c++)
                    result.Add(create([(long?)i, names[c], values[c] is null ? null : KqlConvert.ToStringValue(values[c])]));
                i++;
            }
            return result.CastTo(schema.RowType);
        });
    }
}
