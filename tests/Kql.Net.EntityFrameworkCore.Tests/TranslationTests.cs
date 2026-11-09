namespace Kql.Net.EntityFrameworkCore.Tests;

/// <summary>Parity tests with plain EF Core: anything EF can't translate falls back to memory.</summary>
public sealed class EfCoreWithoutTranslatorTests() : EfCoreParityTests(useTranslator: false);

/// <summary>Parity tests with <c>UseEntityFrameworkCore()</c>, plus checks that operators reach SQL.</summary>
public sealed class EfCoreTranslatedTests() : EfCoreParityTests(useTranslator: true)
{
    /// <summary>Asserts parity, and that the query ran as a single SQL statement containing all fragments.</summary>
    private void AssertPushedDown(string query, params string[] sqlFragments)
    {
        _sql.Clear();
        AssertSame(query);
        var sql = Assert.Single(_sql);
        foreach (var fragment in sqlFragments) Assert.Contains(fragment, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Sales | where Customer contains 'LI' | project Id | sort by Id asc", "lower(")]
    [InlineData("Sales | where Customer startswith 'B' | project Id | sort by Id asc", "LIKE")]
    [InlineData("Sales | where Customer endswith 'OL' | project Id | sort by Id asc", "lower(")]
    [InlineData("Sales | where Customer =~ 'ALICE' | project Id | sort by Id asc", "lower(")]
    [InlineData("Sales | where Customer in~ ('ALICE', 'Dave') | project Id | sort by Id asc", "IN (")]
    [InlineData("Sales | where Customer contains_cs 'li' | project Id | sort by Id asc", "instr")]
    [InlineData("Sales | where Customer !contains 'O' | project Id | sort by Id asc", "lower(")]
    [InlineData("Sales | where Customer like 'a%' | project Id | sort by Id asc", "LIKE")]
    public void StringOperators_ArePushedDown(string query, string sqlFragment) => AssertPushedDown(query, "WHERE", sqlFragment);

    [Theory]
    [InlineData("Sales | project Id, c = toupper(Customer), n = strlen(Region) | sort by Id asc", "upper(")]
    [InlineData("Sales | project Id, s = substring(Customer, 0, 2), j = strcat(Customer, '-', Region) | sort by Id asc", "substr")]
    [InlineData("Sales | project Id, y = getyear(Timestamp), m = getmonth(Timestamp), d = dayofmonth(Timestamp), h = hourofday(Timestamp) | sort by Id asc", "strftime")]
    [InlineData("Sales | project Id, r = round(Amount, 1), a = abs(Amount - 100) | sort by Id asc", "round")]
    public void Functions_ArePushedDown(string query, string sqlFragment) => AssertPushedDown(query, sqlFragment);

    [Fact]
    public void BinNumeric_IsPushedDown() =>
        AssertPushedDown("Sales | summarize n = count() by b = bin(Amount, 100) | sort by b asc", "GROUP BY");

    [Fact]
    public void BinLong_IsPushedDown() =>
        AssertPushedDown("Sales | summarize n = count() by b = bin(Quantity, 5) | sort by b asc", "GROUP BY");

    [Fact]
    public void BinDay_IsPushedDown() =>
        AssertPushedDown("Sales | summarize n = count() by d = bin(Timestamp, 1d) | sort by d asc", "GROUP BY");

    [Fact]
    public void BinHour_FallsBackToMemory_ButStillCorrect()
    {
        AssertSame("Sales | summarize n = count() by h = bin(Timestamp, 1h) | sort by h asc");
    }

    [Fact]
    public void Has_PrefiltersInSql_ThenRechecksExactly()
    {
        // 'li' is a substring of 'alice' but not a whole term: SQL prefilter matches, exact recheck removes it
        _sql.Clear();
        AssertSame("Sales | where Customer has 'li' | project Id");
        Assert.Contains("lower(", Assert.Single(_sql), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_efKql.Execute("Sales | where Customer has 'li'").Rows);

        AssertSame("Sales | where Customer has 'alice' or Region has 'apac' | project Id | sort by Id asc");
        AssertSame("Sales | where Customer has_any ('bob', 'nobody') | project Id | sort by Id asc");
    }

    [Fact]
    public void Has_UnderNegation_IsNotApproximated()
    {
        // `not(x has y)` can't use a superset filter; it must be evaluated exactly (in memory)
        AssertSame("Sales | where not(Customer has 'bob') | project Id | sort by Id asc");
        AssertSame("Sales | where Customer !has 'bob' | project Id | sort by Id asc");
    }

    [Fact]
    public void Has_InProjection_IsExact()
    {
        AssertSame("Sales | project Id, m = Customer has 'li' | sort by Id asc");
    }

    [Fact]
    public void MixedTranslatableAndNot_FallsBackCorrectly()
    {
        // matches regex has no translation: the whole where runs in memory, after the translated prefix
        _sql.Clear();
        AssertSame("Sales | where Region == 'US' | where Customer matches regex '^b' and Customer contains 'o' | project Id | sort by Id asc");
        Assert.Contains("WHERE", Assert.Single(_sql));
    }

    [Fact]
    public void AfterClientOperator_LaterOperatorsRunInMemory()
    {
        AssertSame("Sales | where Customer matches regex '^[ab]' | where Customer contains 'o' | summarize n = count() by Region | sort by Region asc");
    }
}
