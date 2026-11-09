namespace Kql.Net.Tests;

public class NullSemanticsTests
{
    private readonly KqlContext _kql = TestData.Context()
        .AddTable("Pairs", new[]
        {
            new Pair(1, 10, 10),
            new Pair(2, null, null),
            new Pair(3, 5, null),
            new Pair(4, 5, 6),
        });

    public sealed record Pair(int Id, long? A, long? B);

    private List<object?> Ids(string query) => _kql.Execute(query).Column("Id").ToList();

    [Fact]
    public void Equality_IsNeverTrueWithNull()
    {
        Assert.Equal([1], Ids("Pairs | where A == B"));              // null == null is not a match
        Assert.Equal([4], Ids("Pairs | where A != B"));              // nor is 5 != null
        Assert.Equal([1, 3, 4], Ids("Pairs | where A != 7"));        // literal: null A is excluded
        Assert.Equal([3, 4], Ids("Pairs | where A == 5"));
        Assert.Equal([2], Ids("Pairs | where isnull(A)"));           // isnull() is how to find nulls
    }

    [Fact]
    public void Equality_InProjection_IsFalseForNull()
    {
        var r = _kql.Execute("Pairs | sort by Id asc | project same = A == B");
        Assert.Equal([true, false, false, false], r.Column("same"));
    }

    [Fact]
    public void NotIn_ExcludesNull()
    {
        Assert.Equal([1, 4], Ids("Pairs | where B !in (7, 8)"));
        Assert.Equal([3, 4], Ids("Pairs | where A in (5)"));
    }

    [Fact]
    public void Strings_TreatNullAsEmpty()
    {
        // KQL strings are never null: a null CLR string behaves as ""
        Assert.Equal([2, 4, 6], Ids("Orders | where Notes == ''"));
        Assert.Equal([1, 3, 5], Ids("Orders | where Notes != ''"));
        Assert.Equal([2, 4, 6], Ids("Orders | where isempty(Notes)"));
    }

    [Fact]
    public void Join_NullKeysDoNotMatch()
    {
        var r = _kql.Execute("Pairs | join kind=inner (Pairs | project B2 = B, Id2 = Id) on $left.A == $right.B2 | project Id, Id2 | sort by Id asc");
        Assert.Equal([1], r.Column("Id"));                           // 10 == 10; nulls never match

        var outer = _kql.Execute("Pairs | join kind=leftouter (Pairs | project B2 = B, Id2 = Id) on $left.A == $right.B2 | sort by Id asc");
        Assert.Equal(4, outer.Count);                                // unmatched (incl. null-key) rows are kept
        Assert.Null(outer[1, "Id2"]);
    }

    [Fact]
    public void Summarize_StillGroupsNullsTogether()
    {
        var r = _kql.Execute("Pairs | summarize n = count() by B | where isnull(B)");
        Assert.Equal(2L, r[0, "n"]);
    }
}

public class FunctionRegistryTests
{
    [Fact]
    public void LambdaFunction_IsKnownToTheParserAndTyped()
    {
        var kql = TestData.Context().AddFunction("double_it", (long? x) => x * 2);
        Assert.Empty(kql.Validate("print double_it(21)"));
        var r = kql.Execute("Orders | where Id == 2 | project d = double_it(Quantity)");
        Assert.Equal("long", r.Columns[0].KqlType);
        Assert.Equal(2L, r.Scalar());
    }

    [Fact]
    public void Functions_AreScopedToTheirContext()
    {
        var with = new KqlContext().AddFunction("greet", (string? name) => "hello " + name);
        var without = new KqlContext();

        Assert.Equal("hello bob", with.Execute("print greet('bob')").Scalar());
        Assert.NotEmpty(without.Validate("print greet('bob')"));
    }

    [Fact]
    public void OverridingBuiltIn_OnlyAffectsThatContext()
    {
        var custom = new KqlContext().AddFunction("strlen", (string? s) => (long?)42);
        Assert.Equal(42L, custom.Execute("print strlen('abc')").Scalar());
        Assert.Equal(3L, new KqlContext().Execute("print strlen('abc')").Scalar());
    }

    [Fact]
    public void ClosureFunction_CapturesState()
    {
        var rates = new Dictionary<string, double> { ["EU"] = 1.1, ["US"] = 1.0, ["APAC"] = 0.9 };
        var kql = TestData.Context().AddFunction("rate", (string? region) => region is not null && rates.TryGetValue(region, out var r) ? r : (double?)null);
        var r = kql.Execute("Orders | where Id == 1 | project v = Amount * rate(Region)");
        Assert.Equal(120.5 * 1.1, (double)r.Scalar()!, 6);
    }

    [Fact]
    public void AttributedType_RegistersOverloads()
    {
        var kql = new KqlContext().AddFunctions(typeof(MathFunctions));
        Assert.Equal(6L, kql.Execute("print clamp_to(9, 1, 6)").Scalar());
        Assert.Equal(2.5, kql.Execute("print clamp_to(2.5, 1.0, 6.0)").Scalar());
    }

    [Fact]
    public void ParamsFunction()
    {
        var kql = new KqlContext().AddFunctions(typeof(MathFunctions));
        Assert.Equal(10L, kql.Execute("print total(1, 2, 3, 4)").Scalar());
    }

    public static class MathFunctions
    {
        [Runtime.KqlFunction("clamp_to")]
        public static long? Clamp(long? v, long? lo, long? hi) => v is null ? null : Math.Clamp(v.Value, lo ?? long.MinValue, hi ?? long.MaxValue);

        [Runtime.KqlFunction("clamp_to")]
        public static double? Clamp(double? v, double? lo, double? hi) => v is null ? null : Math.Clamp(v.Value, lo ?? double.MinValue, hi ?? double.MaxValue);

        [Runtime.KqlFunction("total")]
        public static long? Total(params long?[] values) => values.Sum();
    }
}
