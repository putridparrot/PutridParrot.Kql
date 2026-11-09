namespace Kql.Net.Tests;

public class EvaluatePluginTests
{
    private readonly KqlContext _kql = TestData.Context();

    private const string Bags = "datatable(id:long, d:dynamic) [1, dynamic({'a': 1, 'b': 'x'}), 2, dynamic({'a': 2, 'c': true}), 3, dynamic(null)]";

    [Fact]
    public void BagUnpack_InfersColumns_AndLaterOperatorsSeeThem()
    {
        var r = _kql.Execute(Bags + " | evaluate bag_unpack(d)");
        Assert.Equal(["id", "a", "b", "c"], r.Columns.Select(c => c.Name));
        Assert.Equal(["long", "long", "string", "bool"], r.Columns.Select(c => c.KqlType));
        Assert.Equal([1L, 2L, null], r.Column("a"));

        var after = _kql.Execute(Bags + " | evaluate bag_unpack(d) | extend z = a * 10 | where isnotnull(c) | project-away b");
        Assert.Equal(["id", "a", "c", "z"], after.Columns.Select(c => c.Name));
        Assert.Equal([20L], after.Column("z"));

        var summarized = _kql.Execute(Bags + " | evaluate bag_unpack(d) | summarize total = sum(a)");
        Assert.Equal(3L, summarized.Scalar());
    }

    [Fact]
    public void BagUnpack_Options()
    {
        Assert.Equal(["id", "p_a", "p_b", "p_c"], _kql.Execute(Bags + " | evaluate bag_unpack(d, 'p_')").Columns.Select(c => c.Name));

        const string conflict = "datatable(id:long, d:dynamic) [1, dynamic({'id': 9, 'x': 2})]";
        // a property that collides with an existing column is an error (the conflict-mode arguments aren't accepted
        // by the Kusto.Language version Kql.Net uses)
        Assert.Throws<KqlException>(() => _kql.Execute(conflict + " | evaluate bag_unpack(d)"));
        Assert.Equal(["id", "p_id", "p_x"], _kql.Execute(conflict + " | evaluate bag_unpack(d, 'p_')").Columns.Select(c => c.Name));
    }

    [Fact]
    public void BagUnpack_WithExplicitSchema()
    {
        var r = _kql.Execute(Bags + " | evaluate bag_unpack(d) : (id:long, a:long, b:string)");
        Assert.Equal(["id", "a", "b"], r.Columns.Select(c => c.Name));
        Assert.Equal(["x", null, null], r.Column("b"));
    }

    [Fact]
    public void Pivot_CountsByDefault()
    {
        var r = _kql.Execute("Orders | project Region, Status | evaluate pivot(Status) | sort by Region asc");
        Assert.Equal(["Region", "Cancelled", "Pending", "Shipped"], r.Columns.Select(c => c.Name));
        Assert.Equal([1L, 0L, 0L], r.Column("Cancelled"));
        Assert.Equal([0L, 2L, 2L], r.Column("Shipped"));
    }

    [Fact]
    public void Pivot_WithAggregation_AndFilterOnPivotedColumn()
    {
        var r = _kql.Execute("Orders | project Region, Status, Quantity | evaluate pivot(Status, sum(Quantity)) | where Shipped > 5 | project Region, Shipped");
        Assert.Equal(["EU", "US"], r.Column("Region").Order());
        Assert.Contains(11L, r.Column("Shipped")); // US: 10 + 1
    }

    [Fact]
    public void Narrow()
    {
        var r = _kql.Execute("Customers | take 1 | evaluate narrow()");
        Assert.Equal(["Name", "Country", "Tier"], r.Column("Column"));
        Assert.Equal(["alice", "France", "1"], r.Column("Value"));
    }
}

public class SearchTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void Search_AllTables()
    {
        var r = _kql.Execute("search 'alice'");
        Assert.Equal("$table", r.Columns[0].Name);
        Assert.Equal(["Customers", "Orders", "Orders"], r.Column("$table").Order());
    }

    [Fact]
    public void Search_Piped_TermsAndPredicates()
    {
        Assert.Equal([1, 5], _kql.Execute("Orders | search 'express' | project Id").Column("Id"));
        Assert.Equal([5], _kql.Execute("Orders | search kind=case_sensitive 'EXPRESS' | project Id").Column("Id"));
        Assert.Equal([5], _kql.Execute("Orders | search 'express' and Amount > 500 | project Id").Column("Id"));
    }

    [Fact]
    public void Search_ColumnAndWildcards()
    {
        Assert.Equal(["carol"], _kql.Execute("search in (Customers) Country:'Ja*' | project Name").Column("Name"));
        Assert.Equal([3], _kql.Execute("Orders | search '*wrap' | project Id").Column("Id"));
        Assert.Equal([3], _kql.Execute("Orders | search '*ift-wr*' | project Id").Column("Id"));
    }

    [Fact]
    public void WhereStarHas()
    {
        Assert.Equal([3], _kql.Execute("Orders | where * has 'gift' | project Id").Column("Id"));
    }

    [Fact]
    public void Find()
    {
        var r = _kql.Execute("find in (Orders, Customers) where Tier > 2 or Quantity > 5 project Customer, Name");
        Assert.Equal(["source_", "Customer", "Name"], r.Columns.Select(c => c.Name));
        Assert.Equal(2, r.Count);
        Assert.Contains("carol", r.Column("Name"));
        Assert.Contains("dave", r.Column("Customer"));

        // without `project`: source_, the columns common to the tables, and pack_ with the rest
        var kql = new KqlContext()
            .AddTable("T", new[] { new { id = 1L, cat = "a", v = 5L }, new { id = 2L, cat = "b", v = 7L } })
            .AddTable("U", new[] { new { id = 3L, name = "x", w = 1.5 } });
        var packed = kql.Execute("find in (T, U) where id > 1 | sort by id asc");
        Assert.Equal(["source_", "id", "pack_"], packed.Columns.Select(c => c.Name));
        Assert.Equal(["T", "U"], packed.Column("source_"));
        Assert.Equal(new Dictionary<string, object?> { ["cat"] = "b", ["v"] = 7L }, packed[0, "pack_"]);
        Assert.Equal(new Dictionary<string, object?> { ["name"] = "x", ["w"] = 1.5 }, packed[1, "pack_"]);
    }
}

public class MoreFunctionTests
{
    private readonly KqlContext _kql = new();

    private object? Eval(string expression) => _kql.Execute("print r = " + expression).Scalar();

    private static List<object?> L(params object?[] items) => [.. items];

    [Fact]
    public void Arrays()
    {
        Assert.Equal(L(1L, 2L, 3L, 4L), Eval("array_sort_asc(dynamic([4, 1, 3, 2]))"));
        Assert.Equal(L(4L, 3L, 2L, 1L), Eval("array_sort_desc(dynamic([4, 1, 3, 2]))"));
        Assert.Equal(L(3L, 2L, 1L), Eval("array_reverse(dynamic([1, 2, 3]))"));
        Assert.Equal(L(3L, 4L, 5L, 1L, 2L), Eval("array_rotate_left(dynamic([1, 2, 3, 4, 5]), 2)"));
        Assert.Equal(L(null, null, 1L, 2L, 3L), Eval("array_shift_right(dynamic([1, 2, 3, 4, 5]), 2)"));
        Assert.Equal(L(3L, 4L, 5L, -1L, -1L), Eval("array_shift_left(dynamic([1, 2, 3, 4, 5]), 2, -1)"));
        Assert.Equal(L(2L, 3L, 4L), Eval("array_slice(dynamic([1, 2, 3, 4, 5]), 1, 3)"));
        Assert.Equal(L(L(1L, 2L), L(3L, 4L, 5L)), Eval("array_split(dynamic([1, 2, 3, 4, 5]), 2)"));
        Assert.Equal(L(L(1L, 2L), L(3L, 4L)), Eval("zip(dynamic([1, 3]), dynamic([2, 4]))"));
        Assert.Equal(L(1L, 1L, 1L), Eval("repeat(1, 3)"));
        Assert.Equal(L(1L, 4L, 7L), Eval("range(1, 8, 3)"));
        Assert.Equal("a-b-c", Eval("strcat_array(dynamic(['a', 'b', 'c']), '-')"));
        Assert.Equal(L("x", "y", "x"), Eval("array_iff(dynamic([true, false, true]), 'x', 'y')"));
    }

    [Fact]
    public void Sets()
    {
        Assert.Equal(L(1L, 2L, 3L, 4L, 5L, 6L), Eval("set_union(dynamic([1, 2, 3]), dynamic([1, 2]), dynamic([4, 5, 6]))"));
        Assert.Equal(L(1L), Eval("set_intersect(dynamic([1, 2, 3]), dynamic([1, 2]), dynamic([1, 8]))"));
        Assert.Equal(L(3L), Eval("set_difference(dynamic([1, 2, 3]), dynamic([1, 2]))"));
        Assert.Equal(true, Eval("set_equals(dynamic([1, 2, 2]), dynamic([2, 1]))"));
        Assert.Equal(0.5, Eval("jaccard_index(dynamic([1, 2, 3]), dynamic([2, 3, 4]))"));
    }

    [Fact]
    public void Bags()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 3L }, Eval("bag_merge(dynamic({'a': 1}), dynamic({'a': 2, 'b': 3}))"));
        Assert.Equal(new Dictionary<string, object?> { ["b"] = 2L }, Eval("bag_remove_keys(dynamic({'a': 1, 'b': 2}), dynamic(['a']))"));
        Assert.Equal(true, Eval("bag_has_key(dynamic({'a': 1}), 'a')"));
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L, ["z"] = 9L }, Eval("bag_set_key(dynamic({'a': 1}), 'z', 9)"));
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 2L }, Eval("bag_zip(dynamic(['a', 'b']), dynamic([1, 2]))"));
    }

    [Fact]
    public void Parsing()
    {
        var url = (Dictionary<string, object?>)Eval("parse_url('scheme://username:password@host:1234/this/is/a/path?k1=v1&k2=v2#fragment')")!;
        Assert.Equal("scheme", url["Scheme"]);
        Assert.Equal("host", url["Host"]);
        Assert.Equal("1234", url["Port"]);
        Assert.Equal("/this/is/a/path", url["Path"]);
        Assert.Equal("username", url["Username"]);
        Assert.Equal("password", url["Password"]);
        Assert.Equal(new Dictionary<string, object?> { ["k1"] = "v1", ["k2"] = "v2" }, url["Query Parameters"]);
        Assert.Equal("fragment", url["Fragment"]);

        Assert.Equal(L("aaa", "b,b,b", "ccc"), Eval("parse_csv('aaa,\"b,b,b\",ccc')"));
        Assert.Equal(true, (bool?)_kql.Execute("print parse_version('1.2.10') > parse_version('1.2.9')").Scalar());
        Assert.Equal(L("app.exe", "a b", "c"), Eval("parse_command_line('app.exe \"a b\" c', 'windows')"));
        var path = (Dictionary<string, object?>)Eval(@"parse_path(@'C:\temp\file.txt')")!;
        Assert.Equal("file.txt", path["Filename"]);
        Assert.Equal("txt", path["Extension"]);
        Assert.Equal(@"C:\temp", path["DirectoryPath"]);
    }

    [Theory]
    [InlineData("parse_ipv4('127.0.0.1')", 2130706433L)]
    [InlineData("ipv4_is_in_range('192.168.1.6', '192.168.1.1/24')", true)]
    [InlineData("ipv4_is_in_range('192.168.2.6', '192.168.1.1/24')", false)]
    [InlineData("ipv4_is_private('192.168.1.1')", true)]
    [InlineData("ipv4_is_private('8.8.8.8')", false)]
    [InlineData("ipv4_compare('192.168.1.1', '192.168.1.255', 24)", 0L)]
    [InlineData("ipv4_compare('192.168.1.1', '192.168.1.255')", -1L)]
    [InlineData("ipv4_is_match('192.168.1.1/24', '192.168.1.255/24')", true)]
    [InlineData("format_ipv4_mask('192.168.1.255', 24)", "192.168.1.0/24")]
    [InlineData("format_ipv4('192.168.1.255', 24)", "192.168.1.0")]
    [InlineData("ipv4_netmask_suffix('192.168.1.1/24')", 24L)]
    [InlineData("has_ipv4('log: 10.0.0.1 connected', '10.0.0.1')", true)]
    [InlineData("has_ipv4('log: 10.0.0.10 connected', '10.0.0.1')", false)]
    [InlineData("ipv4_is_in_any_range('10.1.2.3', '192.168.0.0/16', '10.0.0.0/8')", true)]
    [InlineData("ipv6_compare('::ffff:7f00:1', '127.0.0.1')", 0L)]
    [InlineData("ipv6_is_in_range('fe80::85d:e82c:9446:7995', 'fe80::/64')", true)]
    [InlineData("parse_ipv6('127.0.0.1')", "0000:0000:0000:0000:0000:ffff:7f00:0001")]
    public void IpAddresses(string expression, object expected) => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void Ipv4RangeToCidrList() =>
        Assert.Equal(L("10.0.0.0/29", "10.0.0.8/30", "10.0.0.12/31"), Eval("ipv4_range_to_cidr_list('10.0.0.0', '10.0.0.13')"));

    [Theory]
    [InlineData("format_bytes(564)", "564 Bytes")]
    [InlineData("format_bytes(10332, 1)", "10.1 KB")]
    [InlineData("format_bytes(20010332)", "19 MB")]
    [InlineData("format_bytes(20010332, 2)", "19.08 MB")]
    [InlineData("format_bytes(20010332, 0, 'KB')", "19541 KB")]
    [InlineData("tohex(256)", "100")]
    [InlineData("strcmp('abc', 'abd')", -1L)]
    [InlineData("translate('abc', 'x', 'abc')", "xxx")]
    [InlineData("translate('ab', '', 'ab')", "")]
    [InlineData("replace_strings('this is an example', dynamic(['this', 'example']), dynamic(['that', 'test']))", "that is an test")]
    [InlineData("regex_quote('a.b')", @"a\.b")]
    [InlineData("indexof_regex('abc123', @'\\d')", 3L)]
    [InlineData("string_size('héllo')", 6L)]
    [InlineData("extract_json('$.a.b[1]', '{\"a\":{\"b\":[5,6]}}')", "6")]
    [InlineData("binary_and(6, 3)", 2L)]
    [InlineData("binary_shift_left(1, 4)", 16L)]
    [InlineData("bitset_count_ones(255)", 8L)]
    [InlineData("gzip_decompress_from_base64_string(gzip_compress_to_base64_string('hello'))", "hello")]
    [InlineData("zlib_decompress_from_base64_string(zlib_compress_to_base64_string('hello'))", "hello")]
    [InlineData("unicode_codepoints_to_string(unicode_codepoints_from_string('hé'))", "hé")]
    [InlineData("punycode_domain_to_string(punycode_domain_from_string('münchen.de'))", "münchen.de")]
    [InlineData("degrees(pi())", 180.0)]
    [InlineData("around(1.5, 1.0, 0.5)", true)]
    public void Misc(string expression, object expected) => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void Encoding_And_Time()
    {
        Assert.Equal(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Eval("base64_decode_toguid(base64_encode_fromguid(guid(01234567-89ab-cdef-0123-456789abcdef)))"));
        Assert.Equal(L(104L, 105L), Eval("base64_decode_toarray(base64_encode_fromarray(dynamic([104, 105])))"));
        Assert.Equal(new DateTime(2019, 1, 1), Eval("unixtime_microseconds_todatetime(1546300800000000)"));
        Assert.Equal(new DateTime(2019, 1, 1), Eval("unixtime_nanoseconds_todatetime(1546300800000000000)"));
        Assert.Equal(new DateTime(2020, 2, 2, 12, 2, 20), Eval("datetime_utc_to_local(datetime(2020-02-02 20:02:20), 'America/Los_Angeles')"));
        Assert.Equal(new DateTime(2020, 2, 2, 20, 2, 20), Eval("datetime_local_to_utc(datetime(2020-02-02 12:02:20), 'America/Los_Angeles')"));
    }

    [Fact]
    public void SeriesMath()
    {
        Assert.Equal(6.0, Eval("series_sum(dynamic([1, 2, 3]))"));
        Assert.Equal(L(1.0, 2.0), Eval("series_abs(dynamic([-1, 2]))"));
        Assert.Equal(1.0, (double)Eval("series_pearson_correlation(dynamic([1, 2, 3]), dynamic([2, 4, 6]))")!, 9);
        var fit = (Dictionary<string, object?>)Eval("series_fit_line_dynamic(dynamic([2, 4, 6]))")!;
        Assert.Equal(2.0, (double)fit["slope"]!, 9);
        Assert.Equal(1.0, (double)fit["rsquare"]!, 9);
        Assert.Equal(L(true, false), Eval("series_greater_equals(dynamic([3, 1]), 2)"));
        var outliers = (List<object?>)Eval("series_outliers(dynamic([1, 2, 1, 2, 1, 50]))")!;
        Assert.True((double)outliers[^1]! > 0);
        Assert.Equal(0.0, outliers[0]);
    }
}

public class MoreAggregateTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void Percentiles_MultipleColumns_AndArray()
    {
        var r = _kql.Execute("range x from 1 to 10 step 1 | summarize percentiles(x, 50, 90), arr = percentiles_array(x, 25, 75)");
        Assert.Equal(["percentile_x_50", "percentile_x_90", "arr"], r.Columns.Select(c => c.Name));
        Assert.Equal(5L, r[0, "percentile_x_50"]);
        Assert.Equal(9L, r[0, "percentile_x_90"]);
        Assert.Equal(new List<object?> { 3L, 8L }, r[0, "arr"]);
    }

    [Fact]
    public void WeightedPercentiles()
    {
        // value 1 with weight 9, value 100 with weight 1: the median is 1
        var r = _kql.Execute("datatable(v:long, w:long) [1, 9, 100, 1] | summarize p50 = percentilew(v, w, 50), p99 = percentilew(v, w, 99)");
        Assert.Equal(1L, r[0, "p50"]);
        Assert.Equal(100L, r[0, "p99"]);
    }

    [Fact]
    public void ConditionalStats_And_Covariance()
    {
        var r = _kql.Execute("""
            datatable(x:real, y:real, ok:bool) [1, 2, true, 2, 4, true, 3, 6, true, 100, 0, false]
            | summarize s = stdevif(x, ok), v = varianceif(x, ok), c = covarianceif(x, y, ok), cp = covariancepif(x, y, ok)
            """);
        Assert.Equal(1.0, (double)r[0, "s"]!, 9);
        Assert.Equal(1.0, (double)r[0, "v"]!, 9);
        Assert.Equal(2.0, (double)r[0, "c"]!, 9);
        Assert.Equal(4.0 / 3, (double)r[0, "cp"]!, 9);
    }

    [Fact]
    public void BinaryAll_And_MakeBagIf()
    {
        var r = _kql.Execute("datatable(v:long, b:dynamic) [6, dynamic({'a': 1}), 3, dynamic({'b': 2})] | summarize a = binary_all_and(v), o = binary_all_or(v), x = binary_all_xor(v), bag = make_bag_if(b, v > 4)");
        Assert.Equal(2L, r[0, "a"]);
        Assert.Equal(7L, r[0, "o"]);
        Assert.Equal(5L, r[0, "x"]);
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L }, r[0, "bag"]);
    }

    [Fact]
    public void Sketches()
    {
        var hll = _kql.Execute("Orders | summarize h = hll(Customer) by Region | summarize merged = hll_merge(h) | project n = dcount_hll(merged)");
        Assert.Equal(4L, hll.Scalar());

        var digest = _kql.Execute("range x from 1 to 100 step 1 | summarize t = tdigest(x) | project p = percentile_tdigest(t, 95), r = rank_tdigest(t, 10)");
        Assert.Equal(95L, KqlConvert(digest[0, "p"]));
        Assert.Equal(10L, KqlConvert(digest[0, "r"]));
    }

    private static long? KqlConvert(object? v) => Kql.Net.Runtime.KqlConvert.ToLong(v);
}
