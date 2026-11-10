namespace PutridParrot.Kql.Tests;

// The plan cache and cached in-memory delegates must never change results: each execution sees the current data,
// the current clock and the current registrations.
public class CachingTests
{
    public record Item(int Id, string Cat, double Amount);

    private static List<Item> Items() => [new(1, "a", 10), new(2, "b", 20), new(3, "a", 30)];

    [Fact]
    public void RepeatedQuery_SeesChangedData()
    {
        var items = Items();
        var kql = new KqlContext().AddTable("Items", items);
        const string query = "Items | where Amount > 5 | summarize s = sum(Amount) by Cat | sort by Cat asc";
        Assert.Equal(40.0, kql.Execute(query)[0, "s"]);
        items.Add(new Item(4, "a", 100));
        Assert.Equal(140.0, kql.Execute(query)[0, "s"]);
        items.Clear();
        Assert.Empty(kql.Execute(query).Rows);
    }

    [Fact]
    public void RepeatedQuery_InMemoryOperatorsSeeChangedData()
    {
        var items = Items();
        var kql = new KqlContext().AddTable("Items", items);
        // window functions and joins consume their inputs through cached delegates too
        const string window = "Items | sort by Id asc | extend total = row_cumsum(Amount) | summarize max(total)";
        const string join = "Items | join kind=inner (Items | project Id, Twice = Amount * 2) on Id | summarize sum(Twice)";
        Assert.Equal(60.0, kql.Execute(window).Scalar());
        Assert.Equal(120.0, kql.Execute(join).Scalar());
        items.Add(new Item(4, "c", 40));
        Assert.Equal(100.0, kql.Execute(window).Scalar());
        Assert.Equal(200.0, kql.Execute(join).Scalar());
    }

    [Fact]
    public void ClockQueries_AreEvaluatedEachTime()
    {
        var kql = new KqlContext();
        var first = (DateTime)kql.Execute("print t = now()").Scalar()!;
        Thread.Sleep(20);
        var second = (DateTime)kql.Execute("print t = now()").Scalar()!;
        Assert.True(second > first);

        var items = Items();
        kql.AddTable("Items", items);
        var a = (DateTime)kql.Execute("print t = ago(1h)").Scalar()!;
        Thread.Sleep(20);
        Assert.True((DateTime)kql.Execute("print t = ago(1h)").Scalar()! > a);
    }

    [Fact]
    public void Toscalar_ReadsCurrentData()
    {
        var items = Items();
        var kql = new KqlContext().AddTable("Items", items);
        const string query = "let n = toscalar(Items | count); Items | extend n | take 1 | project n";
        Assert.Equal(3L, kql.Execute(query).Scalar());
        items.Add(new Item(4, "c", 40));
        Assert.Equal(4L, kql.Execute(query).Scalar());
    }

    [Fact]
    public void Pivot_ColumnsFollowTheData()
    {
        var items = Items();
        var kql = new KqlContext().AddTable("Items", items);
        const string query = "Items | project Cat, Amount | evaluate pivot(Cat, sum(Amount))";
        Assert.Equal(2, kql.Execute(query).Columns.Count);
        items.Add(new Item(4, "c", 40));
        Assert.Equal(3, kql.Execute(query).Columns.Count);
    }

    [Fact]
    public void Reregistering_InvalidatesCachedPlans()
    {
        var kql = new KqlContext().AddTable("Items", Items());
        Assert.Equal(3L, kql.Execute("Items | count").Scalar());
        kql.AddTable("Items", Items().Take(1).ToList());
        Assert.Equal(1L, kql.Execute("Items | count").Scalar());

        kql.AddFunction("f", (long? x) => x + 1);
        Assert.Equal(2L, kql.Execute("print f(1)").Scalar());
        kql.AddFunction("f", (long? x) => x + 100);
        Assert.Equal(101L, kql.Execute("print f(1)").Scalar());
    }

    [Fact]
    public void FoldedCustomFunctions_RunEachTime()
    {
        var calls = 0;
        var kql = new KqlContext().AddFunction("counter", (long? _) => (long?)++calls);
        Assert.Equal(1L, kql.Execute("print counter(0)").Scalar());
        Assert.Equal(2L, kql.Execute("print counter(0)").Scalar());
    }

    [Fact]
    public void Materialize_IsNotSharedBetweenExecutions()
    {
        var items = Items();
        var kql = new KqlContext().AddTable("Items", items);
        const string query = "let m = materialize(Items | where Amount > 0); m | count";
        Assert.Equal(3L, kql.Execute(query).Scalar());
        items.Add(new Item(4, "c", 40));
        Assert.Equal(4L, kql.Execute(query).Scalar());
    }

    [Fact]
    public void ConcurrentExecutions_OfTheSameQuery()
    {
        var items = Enumerable.Range(0, 1000).Select(i => new Item(i, "c" + i % 7, i)).ToList();
        var kql = new KqlContext().AddTable("Items", items);
        const string query = "Items | where Amount >= 10 | summarize n = count(), s = sum(Amount) by Cat | sort by Cat asc";
        var expected = kql.Execute(query).Rows.Select(r => string.Join(",", r)).ToList();
        Parallel.For(0, 200, _ =>
        {
            var rows = kql.Execute(query).Rows.Select(r => string.Join(",", r)).ToList();
            Assert.Equal(expected, rows);
        });
    }

    [Fact]
    public void CacheCanBeTurnedOff()
    {
        var items = Items();
        var kql = new KqlContext { PlanCacheSize = 0 }.AddTable("Items", items);
        Assert.Equal(3L, kql.Execute("Items | count").Scalar());
        items.Add(new Item(4, "c", 40));
        Assert.Equal(4L, kql.Execute("Items | count").Scalar());
    }

    // ------------------------------------------------------------------ emitted row equality

    [Fact]
    public void Distinct_UsesKqlValueEquality()
    {
        var kql = new KqlContext();
        var r = kql.Execute("""
            datatable(s:string, n:long, x:real)
            [
                "a", 1, 0.5,
                "a", 1, 0.5,
                "A", 1, 0.5,
                "a", long(null), real(nan),
                "a", long(null), real(nan),
            ]
            | distinct s, n, x
            """);
        Assert.Equal(3, r.Count);
        // dynamic values group by value, not by reference
        var bags = kql.Execute("""
            datatable(d:dynamic) [dynamic([1, 2]), dynamic([1, 2]), dynamic({"x": 1}), dynamic({"x": 1}), dynamic([2, 1])]
            | summarize count() by tostring(d)
            """);
        Assert.Equal(3, bags.Count);
        var sets = (List<object?>)kql.Execute("""
            datatable(d:dynamic) [dynamic([1, 2]), dynamic([1, 2]), dynamic({"x": 1}), dynamic({"x": 1}), dynamic([2, 1])]
            | summarize make_set(d)
            """).Scalar()!;
        Assert.Equal(3, sets.Count);
    }

    [Fact]
    public void GroupByAndJoin_OnNullableKeys()
    {
        var kql = new KqlContext();
        var groups = kql.Execute("datatable(k:long, v:long) [1, 1, 1, 2, long(null), 3, long(null), 4] | summarize s = sum(v) by k | sort by k asc");
        Assert.Equal([3L, 7L], groups.Column("s").Cast<long>().Order());
        // null join keys never match
        var joined = kql.Execute("""
            let L = datatable(k:long, a:string) [1, "one", long(null), "none"];
            let R = datatable(k:long, b:string) [1, "uno", long(null), "nada"];
            L | join kind=inner R on k | count
            """);
        Assert.Equal(1L, joined.Scalar());
    }

    [Fact]
    public void MakeSet_And_SetFunctions_OverManyValues()
    {
        var kql = new KqlContext();
        Assert.Equal(1000, ((List<object?>)kql.Execute("range x from 1 to 100000 step 1 | summarize s = make_set(x % 1000)").Scalar()!).Count);
        Assert.Equal(true, kql.Execute("print set_equals(dynamic([1, 2, 3, 3]), dynamic([3, 2, 1]))").Scalar());
        Assert.Equal(2, ((List<object?>)kql.Execute("print set_intersect(dynamic([1, 2, 3]), dynamic([2, 3, 4]))").Scalar()!).Count);
        Assert.Single(((List<object?>)kql.Execute("print set_difference(dynamic([1, 2, 3]), dynamic([2, 3, 4]))").Scalar()!));
    }

    // ------------------------------------------------------------------ result mapping

    public class Mutable
    {
        public int Id { get; set; }
        public string? Cat { get; private set; }
        public double Amount;
    }

    public record Positional(long Id, string Cat, double? Amount, string? Missing);

    [Fact]
    public void As_MapsPropertiesFieldsAndConstructors()
    {
        var kql = new KqlContext().AddTable("Items", Items());
        var mutable = kql.Execute<Mutable>("Items | sort by Id asc");
        Assert.Equal((3, "a", 30.0), (mutable[2].Id, mutable[2].Cat, mutable[2].Amount));
        var records = kql.Execute<Positional>("Items | sort by Id asc");
        Assert.Equal(new Positional(2, "b", 20, null), records[1]);
    }
}
