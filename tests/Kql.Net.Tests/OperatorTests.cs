namespace Kql.Net.Tests;

public class OperatorTests
{
    private readonly KqlContext _kql = TestData.Context();

    private List<object?> Col(string query, string column) => _kql.Execute(query).Column(column).ToList();

    [Fact]
    public void TableReference_ReturnsAllRowsAndColumns()
    {
        var r = _kql.Execute("Orders");
        Assert.Equal(6, r.Count);
        Assert.Equal(["Id", "Customer", "Region", "Amount", "Quantity", "Timestamp", "Status", "Notes"], r.Columns.Select(c => c.Name));
        Assert.Equal("int", r.Columns[0].KqlType);
        Assert.Equal("real", r.Columns[3].KqlType);
        Assert.Equal("string", r.Columns[6].KqlType); // enum -> string
        Assert.Equal("Shipped", r[0, "Status"]);
    }

    [Fact]
    public void Where_FiltersRows()
    {
        Assert.Equal([3, 5], Col("Orders | where Amount > 200", "Id"));
        Assert.Equal([1, 3], Col("Orders | where Customer == 'alice' and Region == 'EU'", "Id"));
        Assert.Equal([2, 3, 5, 6], Col("Orders | where Customer == 'bob' or Amount >= 300", "Id"));
        Assert.Equal([4], Col("Orders | where Status != 'Shipped' and Quantity > 2", "Id"));
    }

    [Fact]
    public void Where_StringOperators()
    {
        Assert.Equal([1, 5], Col("Orders | where Notes has 'express'", "Id"));
        Assert.Equal([5], Col("Orders | where Notes has_cs 'EXPRESS'", "Id"));
        Assert.Equal([3], Col("Orders | where Notes contains 'wrap'", "Id"));
        Assert.Equal([2, 6], Col("Orders | where Customer startswith 'B'", "Id"));
        Assert.Equal([], Col("Orders | where Customer startswith_cs 'B'", "Id"));
        Assert.Equal([2, 6], Col("Orders | where Customer =~ 'BOB'", "Id"));
        Assert.Equal([1, 2, 3, 5, 6], Col("Orders | where Customer !~ 'CAROL'", "Id"));
        Assert.Equal([4, 5], Col("Orders | where Customer matches regex '^[cd]'", "Id"));
        Assert.Equal([2, 4, 6], Col("Orders | where Notes !has 'express' and isempty(Notes) or Customer == 'carol'", "Id"));
        Assert.Equal([1, 5], Col("Orders | where Notes has_any ('express', 'nothing')", "Id"));
        Assert.Equal([1], Col("Orders | where Notes has_all ('express', 'delivery')", "Id"));
    }

    [Fact]
    public void Where_InAndBetween()
    {
        Assert.Equal([1, 3, 4], Col("Orders | where Customer in ('alice', 'carol')", "Id"));
        Assert.Equal([2, 5, 6], Col("Orders | where Customer !in ('alice', 'carol')", "Id"));
        Assert.Equal([2, 6], Col("Orders | where Customer in~ ('BOB')", "Id"));
        Assert.Equal([2, 3, 4], Col("Orders | where Quantity in (1, 3, 5) | where Id < 6", "Id"));
        Assert.Equal([1, 2, 3, 4], Col("Orders | where Amount between (40 .. 300)", "Id"));
        Assert.Equal([5, 6], Col("Orders | where Amount !between (40 .. 300)", "Id"));
        Assert.Equal([1, 2, 3], Col("Orders | where Timestamp between (datetime(2024-01-01) .. 1d)", "Id"));
    }

    [Fact]
    public void Where_InSubquery()
    {
        Assert.Equal([1, 2, 3, 6], Col("Orders | where Customer in (Customers | where Tier < 3 | project Name)", "Id"));
    }

    [Fact]
    public void Extend_AddsAndReplacesColumns()
    {
        var r = _kql.Execute("Orders | extend Total = Amount * Quantity, Big = Amount > 100 | where Id == 1");
        Assert.Equal(241.0, r[0, "Total"]);
        Assert.Equal(true, r[0, "Big"]);
        Assert.Equal(10, r.Columns.Count);

        var replaced = _kql.Execute("Orders | extend Amount = Amount * 2 | where Id == 2 | project Amount");
        Assert.Equal(150.0, replaced.Scalar());
    }

    [Fact]
    public void Extend_UnnamedExpressionGetsGeneratedName()
    {
        var r = _kql.Execute("Orders | extend Quantity + 1 | take 1");
        Assert.Contains(r.Columns, c => c.Name == "Column1");
        Assert.Equal(3L, r[0, "Column1"]);
    }

    [Fact]
    public void Project_SelectsRenamesAndComputes()
    {
        var r = _kql.Execute("Orders | project Id, Who = toupper(Customer), Double = Amount * 2 | where Id == 2");
        Assert.Equal(["Id", "Who", "Double"], r.Columns.Select(c => c.Name));
        Assert.Equal("BOB", r[0, "Who"]);
        Assert.Equal(150.0, r[0, "Double"]);
    }

    [Fact]
    public void ProjectAwayKeepRenameReorder()
    {
        Assert.Equal(["Id", "Customer", "Region"], _kql.Execute("Orders | project-away Amount, Quantity, Timestamp, Status, Notes").Columns.Select(c => c.Name));
        Assert.Equal(["Id", "Customer"], _kql.Execute("Orders | project-keep Id, Customer").Columns.Select(c => c.Name));
        Assert.Equal(["Id", "Customer", "Area"], _kql.Execute("Orders | project Id, Customer, Region | project-rename Area = Region").Columns.Select(c => c.Name));
        Assert.Equal(["Region", "Id", "Customer"], _kql.Execute("Orders | project Id, Customer, Region | project-reorder Region").Columns.Select(c => c.Name));
        Assert.Equal(["Id", "Customer", "Region"], _kql.Execute("Orders | project-away A*, Q*, T*, S*, N*").Columns.Select(c => c.Name));
    }

    [Fact]
    public void TakeAndLimit()
    {
        Assert.Equal(3, _kql.Execute("Orders | take 3").Count);
        Assert.Equal(2, _kql.Execute("Orders | limit 2").Count);
        Assert.Equal(6, _kql.Execute("Orders | take 100").Count);
    }

    [Fact]
    public void Sort_DefaultsToDescending()
    {
        Assert.Equal([5, 3, 1, 2, 4, 6], Col("Orders | sort by Amount", "Id"));
        Assert.Equal([6, 4, 2, 1, 3, 5], Col("Orders | order by Amount asc", "Id"));
        Assert.Equal([3, 1, 2, 6, 4, 5], Col("Orders | sort by Customer asc, Amount desc", "Id"));
    }

    [Fact]
    public void Sort_NullsPlacement()
    {
        // default: asc => nulls first, desc => nulls last
        Assert.Equal([2, 4, 6, 5, 1, 3], Col("Orders | sort by Notes asc, Id asc", "Id"));
        Assert.Equal([3, 1, 5, 2, 4, 6], Col("Orders | sort by Notes desc, Id asc", "Id"));
        Assert.Equal([5, 1, 3, 2, 4, 6], Col("Orders | sort by Notes asc nulls last, Id asc", "Id"));
        Assert.Equal([2, 4, 6, 3, 1, 5], Col("Orders | sort by Notes desc nulls first, Id asc", "Id"));
    }

    [Fact]
    public void Sort_StringsAreOrdinal()
    {
        var kql = new KqlContext().AddTable("T", new[] { "b", "B", "a", "A" });
        Assert.Equal(["A", "B", "a", "b"], kql.Execute("T | sort by value asc").Column("value"));
    }

    [Fact]
    public void Top()
    {
        Assert.Equal([5, 3], Col("Orders | top 2 by Amount", "Id"));
        Assert.Equal([6, 4], Col("Orders | top 2 by Amount asc", "Id"));
    }

    [Fact]
    public void Count()
    {
        var r = _kql.Execute("Orders | where Region == 'US' | count");
        Assert.Equal("Count", r.Columns.Single().Name);
        Assert.Equal(3L, r.Scalar());
    }

    [Fact]
    public void Distinct()
    {
        Assert.Equal(3, _kql.Execute("Orders | distinct Region").Count);
        Assert.Equal(4, _kql.Execute("Orders | distinct Customer, Region").Count);
        Assert.Equal(6, _kql.Execute("Orders | distinct *").Count);
    }

    [Fact]
    public void Let_ScalarAndTabular()
    {
        Assert.Equal([3, 5], Col("let threshold = 200; Orders | where Amount > threshold", "Id"));
        Assert.Equal([3, 5], Col("let big = Orders | where Amount > 200; big | project Id", "Id"));
        Assert.Equal([5], Col("let maxAmount = toscalar(Orders | summarize max(Amount)); Orders | where Amount == maxAmount", "Id"));
    }

    [Fact]
    public void Let_UserDefinedScalarFunction()
    {
        Assert.Equal([241.0], Col("let total = (price:real, qty:long) { price * qty }; Orders | where Id == 1 | project T = total(Amount, Quantity)", "T"));
    }

    [Fact]
    public void Let_UserDefinedTabularFunction()
    {
        Assert.Equal([2, 5, 6], Col("let inRegion = (r:string) { Orders | where Region == r }; inRegion('US') | project Id", "Id"));
    }

    [Fact]
    public void MultipleStatements_ReturnEachResult()
    {
        var results = _kql.ExecuteAll("Orders | count; Customers | count");
        Assert.Equal(2, results.Count);
        Assert.Equal(6L, results[0].Scalar());
        Assert.Equal(4L, results[1].Scalar());
    }

    [Fact]
    public void Print()
    {
        var r = _kql.Execute("print x = 1 + 2, s = strcat('a', 'b'), 10");
        Assert.Equal(3L, r[0, "x"]);
        Assert.Equal("ab", r[0, "s"]);
        Assert.Equal(3, r.Columns.Count);
    }

    [Fact]
    public void Range()
    {
        Assert.Equal([1L, 3L, 5L, 7L, 9L], _kql.Execute("range x from 1 to 10 step 2").Column("x"));
        Assert.Equal(3, _kql.Execute("range d from datetime(2024-01-01) to datetime(2024-01-03) step 1d").Count);
    }

    [Fact]
    public void DataTable()
    {
        var r = _kql.Execute("datatable(Name:string, Age:long) ['ann', 30, 'ben', 25] | where Age > 26");
        Assert.Equal("ann", r.Scalar());
    }

    [Fact]
    public void GetSchema()
    {
        var r = _kql.Execute("Customers | getschema");
        Assert.Equal(["Name", "Country", "Tier"], r.Column("ColumnName"));
        Assert.Equal(["string", "string", "int"], r.Column("ColumnType"));
    }
}
