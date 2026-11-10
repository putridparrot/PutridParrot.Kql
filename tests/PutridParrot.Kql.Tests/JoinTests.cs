namespace PutridParrot.Kql.Tests;

public class JoinTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void InnerJoin_DifferentKeyNames()
    {
        var r = _kql.Execute("Orders | join kind=inner (Customers) on $left.Customer == $right.Name | project Id, Country | sort by Id asc");
        Assert.Equal([1, 2, 3, 4, 6], r.Column("Id"));
        Assert.Equal(["France", "USA", "France", "Japan", "USA"], r.Column("Country"));
    }

    [Fact]
    public void InnerUnique_IsDefault_DedupesLeft()
    {
        // innerunique keeps one left row per key: alice, bob, carol (dave has no customer)
        var r = _kql.Execute("Orders | join (Customers | project Customer = Name, Country) on Customer");
        Assert.Equal(3, r.Count);
        Assert.Contains(r.Columns, c => c.Name == "Customer1"); // duplicate right column is renamed
    }

    [Fact]
    public void LeftOuter()
    {
        var r = _kql.Execute("Orders | join kind=leftouter (Customers) on $left.Customer == $right.Name | project Id, Country | sort by Id asc");
        Assert.Equal(6, r.Count);
        Assert.Null(r[4, "Country"]); // dave
    }

    [Fact]
    public void RightOuterAndFullOuter()
    {
        var right = _kql.Execute("Orders | join kind=rightouter (Customers) on $left.Customer == $right.Name");
        Assert.Equal(6, right.Count); // 5 matches + erin
        var full = _kql.Execute("Orders | join kind=fullouter (Customers) on $left.Customer == $right.Name");
        Assert.Equal(7, full.Count); // 5 matches + dave + erin
    }

    [Fact]
    public void SemiAndAnti()
    {
        Assert.Equal([5], _kql.Execute("Orders | join kind=leftanti (Customers) on $left.Customer == $right.Name").Column("Id"));
        Assert.Equal(5, _kql.Execute("Orders | join kind=leftsemi (Customers) on $left.Customer == $right.Name").Count);
        Assert.Equal(["erin"], _kql.Execute("Orders | join kind=rightanti (Customers) on $left.Customer == $right.Name").Column("Name"));
        var rs = _kql.Execute("Orders | join kind=rightsemi (Customers) on $left.Customer == $right.Name");
        Assert.Equal(["Name", "Country", "Tier"], rs.Columns.Select(c => c.Name));
        Assert.Equal(3, rs.Count);
    }

    [Fact]
    public void Lookup()
    {
        var r = _kql.Execute("Orders | lookup (Customers | project Customer = Name, Country) on Customer | project Id, Country | sort by Id asc");
        Assert.Equal(6, r.Count);
        Assert.Equal("France", r[0, "Country"]);
        Assert.Null(r[4, "Country"]);
    }

    [Fact]
    public void Union()
    {
        var r = _kql.Execute("union (Orders | project Who = Customer), (Customers | project Who = Name)");
        Assert.Equal(10, r.Count);
        var piped = _kql.Execute("Orders | project Who = Customer | union (Customers | project Who = Name)");
        Assert.Equal(10, piped.Count);
    }

    [Fact]
    public void Union_OuterSchemaFillsNulls()
    {
        var r = _kql.Execute("union withsource=Src (Orders | project Id), (Customers | project Name)");
        Assert.Equal(["Src", "Id", "Name"], r.Columns.Select(c => c.Name));
        Assert.Equal(10, r.Count);
        Assert.Null(r[0, "Name"]);
    }
}
