namespace PutridParrot.Kql.Tests;

public class FunctionTests
{
    private readonly KqlContext _kql = new();

    private object? Eval(string expression) => _kql.Execute("print r = " + expression).Scalar();

    [Theory]
    [InlineData("strlen('hello')", 5L)]
    [InlineData("toupper('abc')", "ABC")]
    [InlineData("tolower('ABC')", "abc")]
    [InlineData("substring('hello', 1, 3)", "ell")]
    [InlineData("strcat('a', 1, 'b')", "a1b")]
    [InlineData("strcat_delim('-', 'a', 'b', 'c')", "a-b-c")]
    [InlineData("indexof('hello', 'l')", 2L)]
    [InlineData("replace_string('aXbXc', 'X', '-')", "a-b-c")]
    [InlineData("replace_regex('a1b22c', @'\\d+', '#')", "a#b#c")]
    [InlineData("trim(' ', '  hi  ')", "hi")]
    [InlineData("extract(@'(\\d+)-(\\d+)', 2, 'id 12-34')", "34")]
    [InlineData("reverse('abc')", "cba")]
    [InlineData("countof('abcabc', 'bc')", 2L)]
    [InlineData("strrep('ab', 3)", "ababab")]
    [InlineData("tostring(42)", "42")]
    [InlineData("toint('17')", 17)]
    [InlineData("tolong('x')", null)]
    [InlineData("todouble('1.5')", 1.5)]
    [InlineData("iff(1 > 2, 'yes', 'no')", "no")]
    [InlineData("case(1 > 2, 'a', 2 > 1, 'b', 'c')", "b")]
    [InlineData("coalesce(long(null), 5)", 5L)]
    [InlineData("isnull(long(null))", true)]
    [InlineData("isempty('')", true)]
    [InlineData("isnotempty('x')", true)]
    [InlineData("not(true)", false)]
    [InlineData("abs(-3)", 3L)]
    [InlineData("round(2.567, 2)", 2.57)]
    [InlineData("bin(17, 5)", 15L)]
    [InlineData("floor(7.9, 2.0)", 6.0)]
    [InlineData("pow(2, 10)", 1024.0)]
    [InlineData("sqrt(16)", 4.0)]
    [InlineData("10 / 4", 2L)]
    [InlineData("10 / 4.0", 2.5)]
    [InlineData("10 % 3", 1L)]
    [InlineData("1 / 0", null)]
    [InlineData("min_of(3, 1, 2)", 1L)]
    [InlineData("max_of(3, 1, 2)", 3L)]
    [InlineData("hash_md5('abc')", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("base64_encode_tostring('hi')", "aGk=")]
    [InlineData("getyear(datetime(2024-05-06))", 2024)]
    [InlineData("getmonth(datetime(2024-05-06))", 5)]
    [InlineData("dayofmonth(datetime(2024-05-06))", 6)]
    [InlineData("hourofday(datetime(2024-05-06 13:00))", 13)]
    [InlineData("datetime_diff('day', datetime(2024-01-10), datetime(2024-01-01))", 9L)]
    [InlineData("datetime_part('month', datetime(2024-03-01))", 3)]
    [InlineData("gettype(dynamic([1,2]))", "array")]
    [InlineData("array_length(dynamic([1,2,3]))", 3L)]
    [InlineData("tostring(dynamic({'a': 1}).a)", "1")]
    [InlineData("tolong(parse_json('{\"x\": {\"y\": [10, 20]}}').x.y[1])", 20L)]
    [InlineData("tolong(dynamic([1,2,3])[-1])", 3L)]
    public void Scalar(string expression, object? expected)
    {
        Assert.Equal(expected, Eval(expression));
    }

    [Fact]
    public void DateTimeFunctions()
    {
        Assert.Equal(new DateTime(2024, 5, 6), Eval("startofday(datetime(2024-05-06 13:45))"));
        Assert.Equal(new DateTime(2024, 5, 1), Eval("startofmonth(datetime(2024-05-06))"));
        Assert.Equal(new DateTime(2024, 1, 1), Eval("startofyear(datetime(2024-05-06))"));
        Assert.Equal(new DateTime(2024, 5, 7, 13, 0, 0), Eval("datetime_add('day', 1, datetime(2024-05-06 13:00))"));
        Assert.Equal(new DateTime(2024, 5, 6, 12, 0, 0), Eval("bin(datetime(2024-05-06 13:45), 6h)"));
        Assert.Equal(TimeSpan.FromHours(36), Eval("datetime(2024-01-02 12:00) - datetime(2024-01-01)"));
        Assert.Equal(new DateTime(2024, 1, 2), Eval("datetime(2024-01-01) + 1d"));
        Assert.Equal(TimeSpan.FromHours(3), Eval("1h * 3"));
        Assert.Equal("2024-05-06", Eval("format_datetime(datetime(2024-05-06), 'yyyy-MM-dd')"));

        var ago = (DateTime)Eval("ago(1h)")!;
        Assert.InRange(ago, DateTime.UtcNow.AddHours(-1).AddMinutes(-1), DateTime.UtcNow.AddHours(-1).AddMinutes(1));
    }

    [Fact]
    public void Split_ReturnsDynamicArray()
    {
        Assert.Equal(new List<object?> { "a", "b", "c" }, Eval("split('a,b,c', ',')"));
        Assert.Equal("b", Eval("tostring(split('a,b,c', ',')[1])"));
    }

    [Fact]
    public void PackAndBags()
    {
        var bag = (Dictionary<string, object?>)Eval("pack('a', 1, 'b', 'x')")!;
        Assert.Equal(1L, bag["a"]);
        Assert.Equal("x", bag["b"]);
        Assert.Equal(new List<object?> { "a", "b" }, Eval("bag_keys(dynamic({'a':1,'b':2}))"));
    }

    [Fact]
    public void PackAll()
    {
        var kql = TestData.Context();
        var bag = (Dictionary<string, object?>)kql.Execute("Customers | take 1 | project b = pack_all()").Scalar()!;
        Assert.Equal("alice", bag["Name"]);
        Assert.Equal(1L, bag["Tier"]);
    }

    [Fact]
    public void CustomFunctionLibrary_CanBeRegistered()
    {
        _kql.AddFunctions(typeof(CustomFunctions));
        // Kusto.Language must know the function to bind it; built-ins with the same name are overridden.
        Assert.Equal("HELLO!", Eval("toupper('hello!')"));
    }

    public static class CustomFunctions
    {
        [PutridParrot.Kql.Runtime.KqlFunction("toupper")]
        public static string? Shout(string? s) => s?.ToUpperInvariant();
    }
}
