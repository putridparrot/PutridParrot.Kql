namespace Kql.Net.Tests;

public class ArgMaxTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void ArgMax_Star_ReturnsWholeRowPerGroup()
    {
        var r = _kql.Execute("Orders | summarize arg_max(Amount, *) by Region | sort by Region asc");
        Assert.Equal(["Region", "Amount", "Id", "Customer", "Quantity", "Timestamp", "Status", "Notes"], r.Columns.Select(c => c.Name));
        Assert.Equal(["APAC", "EU", "US"], r.Column("Region"));
        Assert.Equal([4, 3, 5], r.Column("Id"));
        Assert.Equal(["carol", "alice", "dave"], r.Column("Customer"));
    }

    [Fact]
    public void ArgMin_ExplicitColumns()
    {
        var r = _kql.Execute("Orders | summarize arg_min(Amount, Id, Customer) by Region | sort by Region asc");
        Assert.Equal(["Region", "Amount", "Id", "Customer"], r.Columns.Select(c => c.Name));
        Assert.Equal([42.25, 120.5, 15.0], r.Column("Amount"));
        Assert.Equal([4, 1, 6], r.Column("Id"));
    }

    [Fact]
    public void ArgMax_Named_WithOtherAggregates_NoBy()
    {
        var r = _kql.Execute("Orders | summarize n = count(), latest = arg_max(Timestamp, Customer), total = sum(Quantity)");
        Assert.Equal(["n", "latest", "Customer", "total"], r.Columns.Select(c => c.Name));
        Assert.Equal(6L, r[0, "n"]);
        Assert.Equal(TestData.Day0.AddDays(3), r[0, "latest"]);
        Assert.Equal("bob", r[0, "Customer"]);
        Assert.Equal(22L, r[0, "total"]);
    }

    [Fact]
    public void ArgMax_IgnoresNullKeys()
    {
        var r = _kql.Execute("Orders | summarize arg_max(Notes, Id)");
        Assert.Equal("gift-wrap", r[0, "Notes"]); // nulls ignored; ordinal max
        Assert.Equal(3, r[0, "Id"]);
    }
}

public class MvExpandTests
{
    private readonly KqlContext _kql = new();

    [Fact]
    public void ExpandsArrayIntoRows()
    {
        var r = _kql.Execute("datatable(id:long, tags:dynamic) [1, dynamic(['a','b']), 2, dynamic(['c'])] | mv-expand tags");
        Assert.Equal([1L, 1L, 2L], r.Column("id"));
        Assert.Equal(["a", "b", "c"], r.Column("tags"));
        Assert.Equal("dynamic", r.Columns[1].KqlType);
    }

    [Fact]
    public void EmptyArrayDropsRow_NullKeepsRow()
    {
        var r = _kql.Execute("datatable(id:long, tags:dynamic) [1, dynamic([]), 2, dynamic(null), 3, dynamic([7])] | mv-expand tags");
        Assert.Equal([2L, 3L], r.Column("id"));
        Assert.Equal([null, 7L], r.Column("tags"));
    }

    [Fact]
    public void ToTypeOf_ConvertsElements()
    {
        var r = _kql.Execute("print xs = dynamic([1, 2, 'x']) | mv-expand xs to typeof(long)");
        Assert.Equal("long", r.Columns[0].KqlType);
        Assert.Equal([1L, 2L, null], r.Column("xs"));
    }

    [Fact]
    public void ParallelExpansion_PadsWithNull_AndItemIndex()
    {
        var r = _kql.Execute("print a = dynamic([1,2,3]), b = dynamic(['x','y']) | mv-expand with_itemindex=i a, b");
        Assert.Equal([1L, 2L, 3L], r.Column("a"));
        Assert.Equal(["x", "y", null], r.Column("b"));
        Assert.Equal([0L, 1L, 2L], r.Column("i"));
    }

    [Fact]
    public void NamedExpansion_AndLimit()
    {
        var r = _kql.Execute("print d = dynamic({'items': [10, 20, 30]}) | mv-expand item = d.items limit 2 | project item");
        Assert.Equal([10L, 20L], r.Column("item"));
    }

    [Fact]
    public void BagExpansion()
    {
        var bags = _kql.Execute("print d = dynamic({'a': 1, 'b': 2}) | mv-expand d");
        Assert.Equal(2, bags.Count);
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L }, bags[0, "d"]);

        var arrays = _kql.Execute("print d = dynamic({'a': 1, 'b': 2}) | mv-expand bagexpansion=array d");
        Assert.Equal(new List<object?> { "b", 2L }, arrays[1, "d"]);
    }

    [Fact]
    public void ExpandsListPropertiesOfObjects()
    {
        var kql = new KqlContext().AddTable("Posts", new[]
        {
            new Post("hello", ["news", "intro"]),
            new Post("update", ["news"]),
        });
        var r = kql.Execute("Posts | mv-expand Tags | summarize n = count() by tostring(Tags) | sort by n desc");
        Assert.Equal(["news", "intro"], r.Column("Tags"));
        Assert.Equal([2L, 1L], r.Column("n"));
    }

    public sealed record Post(string Title, List<string> Tags);
}

public class ParseTests
{
    private readonly KqlContext _kql = new KqlContext().AddTable("Logs", new[]
    {
        new Log("id=42 user=alice took=12.5ms status=ok"),
        new Log("id=43 user=bob took=7ms status=fail"),
        new Log("garbage line"),
        new Log("id=x user=carol took=1ms status=ok"),
    });

    public sealed record Log(string Line);

    [Fact]
    public void Simple_TypedAndStringColumns()
    {
        var r = _kql.Execute("Logs | parse Line with 'id=' id:long ' user=' user ' took=' took:real 'ms' *");
        Assert.Equal(["Line", "id", "user", "took"], r.Columns.Select(c => c.Name));
        Assert.Equal([42L, 43L, null, null], r.Column("id"));
        Assert.Equal(["alice", "bob", null, null], r.Column("user"));   // id=x fails the long pattern => no match
        Assert.Equal([12.5, 7.0, null, null], r.Column("took"));
    }

    [Fact]
    public void LastColumn_TakesRestOfString()
    {
        var r = _kql.Execute("Logs | parse Line with * 'status=' status | project status");
        Assert.Equal(["ok", "fail", null, "ok"], r.Column("status"));
    }

    [Fact]
    public void Relaxed_NullsOnlyFailingColumns()
    {
        var r = _kql.Execute("Logs | parse kind=relaxed Line with 'id=' id:long ' user=' user ' ' *");
        Assert.Equal([42L, 43L, null, null], r.Column("id"));
        Assert.Equal(["alice", "bob", null, "carol"], r.Column("user"));
    }

    [Fact]
    public void Regex_Mode()
    {
        var r = _kql.Execute(@"Logs | parse kind=regex flags=i Line with * 'USER=' user @'\s+took=' took:long | project user, took");
        Assert.Equal(["alice", "bob", null, "carol"], r.Column("user"));
        Assert.Equal([12L, 7L, null, 1L], r.Column("took"));
    }

    [Fact]
    public void ParseWhere_DropsNonMatchingRows()
    {
        var r = _kql.Execute("Logs | parse-where Line with 'id=' id:long ' user=' user ' ' *");
        Assert.Equal(["alice", "bob"], r.Column("user"));
    }
}

public class WindowFunctionTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void RowNumber_AfterSort()
    {
        var r = _kql.Execute("Orders | sort by Amount desc | extend rn = row_number() | project Id, rn");
        Assert.Equal([5, 3, 1, 2, 4, 6], r.Column("Id"));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], r.Column("rn"));
    }

    [Fact]
    public void Serialize_WithExpressions_AndRestart()
    {
        var r = _kql.Execute("Orders | sort by Customer asc, Id asc | serialize rn = row_number(1, Customer != prev(Customer)) | project Customer, rn");
        Assert.Equal(["alice", "alice", "bob", "bob", "carol", "dave"], r.Column("Customer"));
        Assert.Equal([1L, 2L, 1L, 2L, 1L, 1L], r.Column("rn"));
    }

    [Fact]
    public void PrevNext_WithOffsetAndDefault()
    {
        var r = _kql.Execute("Orders | sort by Id asc | extend p = prev(Id), n2 = next(Id, 2, -1) | project Id, p, n2");
        Assert.Equal([null, 1, 2, 3, 4, 5], r.Column("p"));
        Assert.Equal([3, 4, 5, 6, -1, -1], r.Column("n2"));
    }

    [Fact]
    public void RowCumSum()
    {
        var r = _kql.Execute("Orders | sort by Id asc | serialize total = row_cumsum(Quantity), byCustomer = row_cumsum(Quantity, Id == 4)");
        Assert.Equal([2, 3, 8, 11, 21, 22], r.Column("total"));
        Assert.Equal([2, 3, 8, 3, 13, 14], r.Column("byCustomer"));
    }

    [Fact]
    public void Ranks()
    {
        var r = _kql.Execute("Orders | sort by Region asc | extend dense = row_rank_dense(Region), mn = row_rank_min(Region) | project Region, dense, mn");
        Assert.Equal(["APAC", "EU", "EU", "US", "US", "US"], r.Column("Region"));
        Assert.Equal([1L, 2L, 2L, 3L, 3L, 3L], r.Column("dense"));
        Assert.Equal([1L, 2L, 2L, 4L, 4L, 4L], r.Column("mn"));
    }

    [Fact]
    public void Where_UsingPrev()
    {
        // orders whose amount is higher than the previous order's (by time)
        var r = _kql.Execute("Orders | sort by Timestamp asc | where Amount > prev(Amount) | project Id");
        Assert.Equal([3, 5], r.Column("Id"));
    }
}
