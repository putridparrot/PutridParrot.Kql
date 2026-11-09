namespace Kql.Net.Tests;

public class SummarizeTests
{
    private readonly KqlContext _kql = TestData.Context();

    private Dictionary<string, object?> ByKey(string query, string key, string value) =>
        _kql.Execute(query).AsDictionaries().ToDictionary(r => (string)r[key]!, r => r[value]);

    [Fact]
    public void CountBy()
    {
        var r = ByKey("Orders | summarize count() by Region", "Region", "count_");
        Assert.Equal(2L, r["EU"]);
        Assert.Equal(3L, r["US"]);
        Assert.Equal(1L, r["APAC"]);
    }

    [Fact]
    public void ColumnOrder_ByColumnsFirst()
    {
        var r = _kql.Execute("Orders | summarize Total = sum(Amount), n = count() by Region");
        Assert.Equal(["Region", "Total", "n"], r.Columns.Select(c => c.Name));
    }

    [Fact]
    public void SumAvgMinMax()
    {
        var r = _kql.Execute("Orders | where Region == 'US' | summarize s = sum(Quantity), a = avg(Amount), mn = min(Amount), mx = max(Timestamp)");
        Assert.Single(r.Rows);
        Assert.Equal(12L, r[0, "s"]); // sum(int) -> long
        Assert.Equal((75 + 999.99 + 15) / 3, (double)r[0, "a"]!, 6);
        Assert.Equal(15.0, r[0, "mn"]);
        Assert.Equal(TestData.Day0.AddDays(3), r[0, "mx"]);
    }

    [Fact]
    public void ConditionalAggregates()
    {
        var r = _kql.Execute("Orders | summarize big = countif(Amount > 100), bigSum = sumif(Amount, Amount > 100), d = dcount(Customer)");
        Assert.Equal(3L, r[0, "big"]);
        Assert.Equal(120.5 + 300 + 999.99, (double)r[0, "bigSum"]!, 6);
        Assert.Equal(4L, r[0, "d"]);
    }

    [Fact]
    public void NoBy_OnEmptyInput_ReturnsOneRow()
    {
        var r = _kql.Execute("Orders | where Amount > 1000000 | summarize n = count(), s = sum(Amount)");
        Assert.Single(r.Rows);
        Assert.Equal(0L, r[0, "n"]);
    }

    [Fact]
    public void WithBy_OnEmptyInput_ReturnsNoRows()
    {
        Assert.Empty(_kql.Execute("Orders | where Amount > 1000000 | summarize count() by Region").Rows);
    }

    [Fact]
    public void BinByTime()
    {
        var r = _kql.Execute("Orders | summarize n = count() by Day = bin(Timestamp, 1d) | sort by Day asc");
        Assert.Equal([2L, 2L, 1L, 1L], r.Column("n"));
        Assert.Equal(TestData.Day0, r[0, "Day"]);
    }

    [Fact]
    public void BinNumeric()
    {
        var r = _kql.Execute("Orders | summarize n = count() by b = bin(Amount, 100) | sort by b asc");
        Assert.Equal([0.0, 100.0, 300.0, 900.0], r.Column("b"));
        Assert.Equal([3L, 1L, 1L, 1L], r.Column("n"));
    }

    [Fact]
    public void MultipleKeys()
    {
        var r = _kql.Execute("Orders | summarize n = count() by Customer, Region | sort by Customer asc");
        Assert.Equal(4, r.Count);
        Assert.Equal(["alice", "bob", "carol", "dave"], r.Column("Customer"));
    }

    [Fact]
    public void ExpressionOverAggregates()
    {
        var r = _kql.Execute("Orders | summarize avgQty = sum(Quantity) * 1.0 / count() by Region | where Region == 'EU'");
        Assert.Equal(3.5, r[0, "avgQty"]);
    }

    [Fact]
    public void MakeListAndSet()
    {
        var r = _kql.Execute("Orders | summarize ids = make_list(Id), customers = make_set(Customer) by Region | where Region == 'US'");
        Assert.Equal(new List<object?> { 2L, 5L, 6L }, r[0, "ids"]);
        Assert.Equal(new List<object?> { "bob", "dave" }, r[0, "customers"]);
    }

    [Fact]
    public void Percentile_Stdev_Variance()
    {
        var kql = new KqlContext().AddTable("N", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        var r = kql.Execute("N | summarize p50 = percentile(value, 50), p90 = percentile(value, 90), sd = stdev(value), v = variance(value)");
        Assert.Equal(5L, r[0, "p50"]);
        Assert.Equal(9L, r[0, "p90"]);
        Assert.Equal(9.1666666, (double)r[0, "v"]!, 5);
        Assert.Equal(Math.Sqrt(9.1666666), (double)r[0, "sd"]!, 5);
    }

    [Fact]
    public void TakeAny()
    {
        var r = _kql.Execute("Orders | where Customer == 'bob' | summarize take_any(Region)");
        Assert.Equal("US", r.Scalar());
    }
}
