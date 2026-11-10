namespace PutridParrot.Kql.Tests;

/// <summary>Examples follow the Microsoft Learn documentation for each operator, with its documented output.</summary>
public class MakeSeriesTests
{
    private readonly KqlContext _kql = new();

    private const string Data = """
        let data = datatable(timestamp:datetime, metric:real)
        [
            datetime(2016-12-31T06:00), 50,
            datetime(2017-01-01), 4,
            datetime(2017-01-02), 3,
            datetime(2017-01-03), 4,
            datetime(2017-01-03T03:00), 6,
            datetime(2017-01-05), 8,
            datetime(2017-01-05T13:40), 13,
            datetime(2017-01-06), 4,
            datetime(2017-01-07), 3,
            datetime(2017-01-08), 8,
            datetime(2017-01-08T21:00), 8,
            datetime(2017-01-09), 2,
            datetime(2017-01-09T12:00), 11,
            datetime(2017-01-10T05:00), 5,
        ];
        """;

    [Fact]
    public void DocumentedExample_AvgWithGapsAndOutOfRangeRows()
    {
        var r = _kql.Execute(Data + "data | make-series avg(metric) on timestamp from datetime(2017-01-01) to datetime(2017-01-10) step 1d");
        Assert.Equal(["avg_metric", "timestamp"], r.Columns.Select(c => c.Name));
        Assert.Equal(new List<object?> { 4.0, 3.0, 5.0, 0.0, 10.5, 4.0, 3.0, 8.0, 6.5 }, r[0, "avg_metric"]);
        var axis = (List<object?>)r[0, "timestamp"]!;
        Assert.Equal(9, axis.Count);
        Assert.Equal(new DateTime(2017, 1, 1), axis[0]);
        Assert.Equal(new DateTime(2017, 1, 9), axis[^1]);
    }

    [Fact]
    public void EmptyInput_GivesNoRows_UnlessNonEmpty()
    {
        Assert.Equal(0L, _kql.Execute(Data + "data | take 0 | make-series avg(metric) default=1.0 on timestamp from datetime(2017-01-01) to datetime(2017-01-10) step 1d | count").Scalar());
        var r = _kql.Execute(Data + "data | take 0 | make-series kind=nonempty avg(metric) default=1.0 on timestamp from datetime(2017-01-01) to datetime(2017-01-10) step 1d");
        Assert.Equal(Enumerable.Repeat((object?)1.0, 9).ToList(), r[0, "avg_metric"]);
    }

    [Fact]
    public void ByColumns_DefaultsAndMultipleAggregates()
    {
        var kql = TestData.Context();
        var r = kql.Execute("""
            Orders
            | make-series n = count(), total = sum(Quantity) default = -1 on Timestamp from datetime(2024-01-01) to datetime(2024-01-05) step 1d by Region
            | sort by Region asc
            """);
        Assert.Equal(["Region", "n", "total", "Timestamp"], r.Columns.Select(c => c.Name));
        Assert.Equal(["APAC", "EU", "US"], r.Column("Region"));
        Assert.Equal(new List<object?> { 0L, 1L, 0L, 0L }, r[0, "n"]);
        Assert.Equal(new List<object?> { -1L, 3L, -1L, -1L }, r[0, "total"]);
        Assert.Equal(new List<object?> { 1L, 0L, 1L, 1L }, r[2, "n"]);
    }

    [Fact]
    public void NoFromTo_UsesTheDataRangePerSeries()
    {
        var kql = TestData.Context();
        var r = kql.Execute("Orders | where Region == 'US' | make-series n = count() on Timestamp step 1d");
        Assert.Equal(new List<object?> { 1L, 0L, 1L, 1L }, r[0, "n"]); // 2024-01-01 .. 2024-01-04
        Assert.Equal(new DateTime(2024, 1, 1), ((List<object?>)r[0, "Timestamp"]!)[0]);
    }

    [Fact]
    public void NumericAxis_AndInRange()
    {
        var r = _kql.Execute("range x from 1 to 10 step 1 | make-series c = count() on x from 0 to 10 step 5");
        Assert.Equal(new List<object?> { 4L, 5L }, r[0, "c"]);
        Assert.Equal(new List<object?> { 0L, 5L }, r[0, "x"]);

        var inRange = _kql.Execute("range x from 1 to 10 step 1 | make-series c = count() on x in range(0, 10, 5)");
        Assert.Equal(new List<object?> { 4L, 5L, 1L }, inRange[0, "c"]); // stop is inclusive
    }

    [Fact]
    public void SeriesFunctions_OnMakeSeriesOutput()
    {
        var r = _kql.Execute(Data + """
            data
            | make-series s = sum(metric) default = real(null) on timestamp from datetime(2017-01-01) to datetime(2017-01-10) step 1d
            | extend filled = series_fill_forward(s), linear = series_fill_linear(s), zero = series_fill_const(s, 0)
            | extend stats = series_stats_dynamic(zero), doubled = series_multiply(zero, 2), smooth = series_fir(zero, dynamic([1,1,1]), true, true)
            """);
        var s = (List<object?>)r[0, "s"]!;
        Assert.Null(s[3]);                                                  // 2017-01-04 has no data
        Assert.Equal(10.0, ((List<object?>)r[0, "filled"]!)[3]);            // previous value (4 + 6)
        Assert.Equal(15.5, ((List<object?>)r[0, "linear"]!)[3]);            // between 10 and 21
        Assert.Equal(0L, ((List<object?>)r[0, "zero"]!)[3]);
        var stats = (Dictionary<string, object?>)r[0, "stats"]!;
        Assert.Equal(21.0, stats["max"]);
        Assert.Equal(4L, stats["max_idx"]);
        Assert.Equal(42.0, ((List<object?>)r[0, "doubled"]!)[4]);
        Assert.Equal((3.0 + 10.0 + 0.0) / 3, (double)((List<object?>)r[0, "smooth"]!)[2]!, 6);
    }
}

public class TopNestedTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void TwoLevels()
    {
        var r = _kql.Execute("Orders | top-nested 2 of Region by sum(Amount), top-nested 1 of Customer by sum(Amount)");
        Assert.Equal(["Region", "aggregated_Region", "Customer", "aggregated_Customer"], r.Columns.Select(c => c.Name));
        Assert.Equal(["US", "EU"], r.Column("Region"));
        Assert.Equal(["dave", "alice"], r.Column("Customer"));
        Assert.Equal(1089.99, (double)r[0, "aggregated_Region"]!, 6);
        Assert.Equal(420.5, (double)r[1, "aggregated_Customer"]!, 6);
    }

    [Fact]
    public void WithOthers_AllValues_AndAscending()
    {
        var others = _kql.Execute("Orders | top-nested 1 of Region with others = 'Other' by n = count()");
        Assert.Equal(["US", "Other"], others.Column("Region"));
        Assert.Equal([3L, 3L], others.Column("n"));

        Assert.Equal(3, _kql.Execute("Orders | top-nested of Region by count()").Count);
        Assert.Equal(["APAC"], _kql.Execute("Orders | top-nested 1 of Region by sum(Amount) asc").Column("Region"));
    }

    [Fact]
    public void DocumentedPattern_CarryColumnsWithIgnore()
    {
        // two most recent orders per customer, keeping their ids
        var r = _kql.Execute("""
            Orders
            | top-nested of Customer by Ignore0 = max(1), top-nested 2 of Timestamp by Ignore1 = max(Timestamp), top-nested of Id by Ignore2 = max(1)
            | project-away Ignore0, Ignore1, Ignore2
            | sort by Customer asc, Timestamp desc
            """);
        Assert.Equal(["Customer", "Timestamp", "Id"], r.Columns.Select(c => c.Name));
        Assert.Equal([3, 1, 6, 2, 4, 5], r.Column("Id"));
    }
}

public class MvApplyTests
{
    private readonly KqlContext _kql = new();

    [Fact]
    public void TopElementPerArray()
    {
        var r = _kql.Execute("datatable(index:long, Arr:dynamic) [0, dynamic([1, 5, 3]), 1, dynamic([10, 2, 5, 7])] | mv-apply topElem = Arr to typeof(long) on (top 1 by topElem)");
        Assert.Equal(["index", "Arr", "topElem"], r.Columns.Select(c => c.Name));
        Assert.Equal([5L, 10L], r.Column("topElem"));
        Assert.Equal(new List<object?> { 1L, 5L, 3L }, r[0, "Arr"]);
    }

    [Fact]
    public void SummarizePerArray_CarriesOtherColumns()
    {
        var r = _kql.Execute("datatable(index:long, Arr:dynamic) [0, dynamic([5, 1, 3]), 1, dynamic([4, 10, 8, 7])] | mv-apply Arr2 = Arr to typeof(long) on (top 2 by Arr2 | summarize Top2 = make_list(Arr2))");
        Assert.Equal(["index", "Arr", "Top2"], r.Columns.Select(c => c.Name));
        Assert.Equal(new List<object?> { 5L, 3L }, r[0, "Top2"]);
        Assert.Equal(new List<object?> { 10L, 8L }, r[1, "Top2"]);
    }

    [Fact]
    public void WithItemIndex()
    {
        var r = _kql.Execute("datatable(row:long, Arr:dynamic) [0, dynamic([5, 1, 3]), 1, dynamic([4, 10, 8, 7])] | mv-apply with_itemindex = index value = Arr to typeof(long) on (where index >= 2)");
        Assert.Equal(["row", "Arr", "value", "index"], r.Columns.Select(c => c.Name));
        Assert.Equal([0L, 1L, 1L], r.Column("row"));
        Assert.Equal([3L, 8L, 7L], r.Column("value"));
        Assert.Equal([2L, 2L, 3L], r.Column("index"));
    }

    [Fact]
    public void MultipleArrays_PadWithNull()
    {
        var r = _kql.Execute("""
            datatable(Val:int, Arr1:dynamic, Arr2:dynamic)
            [1, dynamic(['A1', 'A2']), dynamic(['B1', 'B2', 'B3']), 5, dynamic(['C1', 'C2']), dynamic(['D1'])]
            | mv-apply T1 = Arr1, T2 = Arr2 on (extend Out = strcat(T1, "_", T2) | summarize Out = make_list(Out))
            """);
        Assert.Equal(new List<object?> { "A1_B1", "A2_B2", "_B3" }, r[0, "Out"]);
        Assert.Equal(new List<object?> { "C1_D1", "C2_" }, r[1, "Out"]);
    }

    [Fact]
    public void OverObjects_LargestLinePerOrder()
    {
        var kql = new KqlContext().AddTable("Orders", new[]
        {
            new OrderDoc(1, [new Line("A", 3), new Line("B", 9)]),
            new OrderDoc(2, [new Line("C", 4)]),
        });
        var r = kql.Execute("Orders | mv-apply line = Lines on (top 1 by tolong(line.Qty)) | project Id, sku = tostring(line.Sku)");
        Assert.Equal(["B", "C"], r.Column("sku"));
    }

    public sealed record Line(string Sku, int Qty);

    public sealed record OrderDoc(int Id, List<Line> Lines);
}

public class ParseKvTests
{
    private readonly KqlContext _kql = new();

    [Fact]
    public void SpecifiedDelimiters()
    {
        var r = _kql.Execute("""
            print str="ThreadId:458745723, Machine:Node001, Text: The service is up, Level: Info"
            | parse-kv str as (Text: string, ThreadId:long, Machine: string) with (pair_delimiter=',', kv_delimiter=':')
            | project-away str
            """);
        Assert.Equal("The service is up", r[0, "Text"]);
        Assert.Equal(458745723L, r[0, "ThreadId"]);
        Assert.Equal("Node001", r[0, "Machine"]);
    }

    [Fact]
    public void Quoting()
    {
        var r = _kql.Execute("""
            print str='src=10.1.1.123 dst=10.1.1.124 bytes=125 failure="connection aborted" "event time"=2021-01-01T10:00:54'
            | parse-kv str as (['event time']:datetime, src:string, dst:string, bytes:long, failure:string) with (pair_delimiter=' ', kv_delimiter='=', quote='"')
            """);
        Assert.Equal(new DateTime(2021, 1, 1, 10, 0, 54), r[0, "event time"]);
        Assert.Equal("10.1.1.123", r[0, "src"]);
        Assert.Equal(125L, r[0, "bytes"]);
        Assert.Equal("connection aborted", r[0, "failure"]);
    }

    [Fact]
    public void DifferentOpenCloseQuotes_AndEscapes()
    {
        var r = _kql.Execute("""
            print str='src=10.1.1.123 failure=(connection aborted) (event time)=(2021-01-01 10:00:54)'
            | parse-kv str as (['event time']:datetime, failure:string) with (pair_delimiter=' ', kv_delimiter='=', quote='()')
            """);
        Assert.Equal("connection aborted", r[0, "failure"]);
        Assert.Equal(new DateTime(2021, 1, 1, 10, 0, 54), r[0, "event time"]);

        var escaped = _kql.Execute("""
            print str='failure="the remote host sent \\"bye!\\"" time=2021-01-01T10:00:54'
            | parse-kv str as (failure:string) with (pair_delimiter=' ', kv_delimiter='=', quote='"', escape='\\')
            """);
        Assert.Equal("the remote host sent \"bye!\"", escaped[0, "failure"]);
    }

    [Fact]
    public void Greedy()
    {
        const string input = "print str='name=John Doe phone=555 5555 city=New York' | parse-kv str as (name:string, phone:string, city:string) with (pair_delimiter=' ', kv_delimiter='='";
        var plain = _kql.Execute(input + ")");
        Assert.Equal(["John", "555", "New"], new[] { plain[0, "name"], plain[0, "phone"], plain[0, "city"] });
        var greedy = _kql.Execute(input + ", greedy=true)");
        Assert.Equal(["John Doe", "555 5555", "New York"], new[] { greedy[0, "name"], greedy[0, "phone"], greedy[0, "city"] });
    }

    [Fact]
    public void NonSpecifiedDelimiters()
    {
        var r = _kql.Execute("""
            print str="2021-01-01T10:00:34 [INFO] ThreadId:458745723, Machine:Node001, Text: Started"
            | parse-kv str as (Text: string, ThreadId:long, Machine: string)
            """);
        Assert.Equal("Started", r[0, "Text"]);
        Assert.Equal(458745723L, r[0, "ThreadId"]);
        Assert.Equal("Node001", r[0, "Machine"]);

        var quoted = _kql.Execute("""
            print str="ThreadId:458745723, Machine:Node001, Text: 'The service \\' is up'"
            | parse-kv str as (Text: string) with (quote="'", escape='\\')
            """);
        Assert.Equal("The service ' is up", quoted[0, "Text"]);
    }

    [Fact]
    public void Regex_AndMissingKeys()
    {
        var r = _kql.Execute("""
            print str=dynamic(["referer url: https://hostname.com/redirect?dest=/?h=1234", "request url: https://hostname.com/?h=1234", "advertiser id: 24fefbca-cf27-4d62-a623-249c2ad30c73"])
            | parse-kv str as (['referer url']:string, ['request url']:string, ['advertiser id']:guid, absent:long) with (regex=@'"([\w ]+)\s*:\s*([^"]*)"')
            """);
        Assert.Equal("https://hostname.com/redirect?dest=/?h=1234", r[0, "referer url"]);
        Assert.Equal(Guid.Parse("24fefbca-cf27-4d62-a623-249c2ad30c73"), r[0, "advertiser id"]);
        Assert.Null(r[0, "absent"]);
    }
}
