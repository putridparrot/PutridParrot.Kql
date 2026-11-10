using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Kql.Net.EntityFrameworkCore;
using Kql.Net.Tests;

namespace Kql.Net.EntityFrameworkCore.Tests;

public sealed class Sale
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public string Region { get; set; } = "";
    public double Amount { get; set; }
    public int Quantity { get; set; }
    public DateTime Timestamp { get; set; }
    public OrderStatus Status { get; set; }
}

public sealed class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public int Tier { get; set; }
}

public sealed class SalesDb(DbContextOptions<SalesDb> options) : DbContext(options)
{
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Client> Clients => Set<Client>();
}

/// <summary>
/// Runs the same queries against EF Core (SQLite) and in-memory lists and checks they agree, and that the
/// translatable parts were pushed down into SQL.
/// </summary>
public abstract class EfCoreParityTests : IDisposable
{
    protected readonly SqliteConnection _connection = new("DataSource=:memory:");
    protected readonly List<string> _sql = [];
    protected readonly SalesDb _db;
    protected readonly KqlContext _efKql;
    protected readonly KqlContext _memKql;

    protected EfCoreParityTests(bool useTranslator)
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<SalesDb>()
            .UseSqlite(_connection)
            .LogTo(s => { if (s.Contains("SELECT")) _sql.Add(s); }, Microsoft.Extensions.Logging.LogLevel.Information)
            .Options;
        _db = new SalesDb(options);
        _db.Database.EnsureCreated();
        var sales = TestData.Orders().Select(o => new Sale
        {
            Id = o.Id, Customer = o.Customer, Region = o.Region, Amount = o.Amount,
            Quantity = o.Quantity, Timestamp = o.Timestamp, Status = o.Status
        }).ToList();
        var clients = TestData.Customers().Select((c, i) => new Client { Id = i + 1, Name = c.Name, Country = c.Country, Tier = c.Tier }).ToList();
        _db.Sales.AddRange(sales);
        _db.Clients.AddRange(clients);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _sql.Clear();

        _efKql = new KqlContext()
            .AddTable("Sales", _db.Sales.AsNoTracking())
            .AddTable("Clients", _db.Clients.AsNoTracking());
        if (useTranslator) _efKql.UseEntityFrameworkCore();
        _memKql = new KqlContext().AddTable("Sales", sales).AddTable("Clients", clients);
    }

    public virtual void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    protected string LastSql => string.Join(Environment.NewLine, _sql);

    protected void AssertSame(string query, bool ordered = true)
    {
        var expected = _memKql.Execute(query);
        var actual = _efKql.Execute(query);
        Assert.Equal(expected.Columns.Select(c => c.Name), actual.Columns.Select(c => c.Name));
        var e = expected.Rows.Select(r => string.Join("|", r.Select(Format))).ToList();
        var a = actual.Rows.Select(r => string.Join("|", r.Select(Format))).ToList();
        if (!ordered)
        {
            e.Sort(StringComparer.Ordinal);
            a.Sort(StringComparer.Ordinal);
        }
        Assert.Equal(e, a);
    }

    private static string Format(object? v) => v switch
    {
        double d => Math.Round(d, 6).ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O").Replace("Z", ""),
        _ => Runtime.KqlConvert.ToStringValue(v) ?? "<null>"
    };

    [Fact]
    public void Where_Project_Sort_Take_ArePushedDown()
    {
        const string q = "Sales | where Amount > 50 and Region == 'US' | project Id, Customer, Total = Amount * Quantity | sort by Total desc | take 5";
        AssertSame(q);
        Assert.Contains("WHERE", LastSql);
        Assert.Contains("ORDER BY", LastSql);
        Assert.Contains("LIMIT", LastSql);
    }

    [Fact]
    public void Summarize_IsPushedDown()
    {
        const string q = "Sales | summarize n = count(), total = sum(Amount), mx = max(Quantity) by Region";
        AssertSame(q, ordered: false);
        Assert.Contains("GROUP BY", LastSql);
    }

    [Fact]
    public void Summarize_WithoutBy()
    {
        AssertSame("Sales | where Region == 'EU' | summarize n = count(), a = avg(Amount)");
        AssertSame("Sales | where Region == 'nowhere' | summarize n = count()");
    }

    [Fact]
    public void Extend_And_InList_ArePushedDown()
    {
        AssertSame("Sales | extend Big = Amount > 100 | where Customer in ('alice', 'bob') | project Id, Big | sort by Id asc");
        Assert.Contains("IN (", LastSql);
    }

    [Fact]
    public void ClientOnlyOperators_FallBackAfterTranslatedPrefix()
    {
        // `has` has no SQL translation: the WHERE on Region still runs in SQL, `has` runs in memory.
        AssertSame("Sales | where Region == 'US' | where Customer has 'bob' | project Id | sort by Id asc");
        Assert.Contains("WHERE", LastSql);
    }

    [Fact]
    public void MixedConjuncts_TranslatablePartRunsInSql()
    {
        // one `where` mixing a SQL-translatable condition with a client-only one: the SQL part still filters in SQL
        AssertSame("Sales | where Customer matches regex '^[a-c]' and Region == 'US' and Amount > 10 | project Id | sort by Id asc");
        Assert.Contains("WHERE", LastSql);
        Assert.Contains("'US'", LastSql);
    }

    [Fact]
    public void EnumColumns()
    {
        AssertSame("Sales | where Status == 'Shipped' | project Id, Status | sort by Id asc");
    }

    [Fact]
    public void Count_And_Distinct()
    {
        AssertSame("Sales | where Quantity > 1 | count");
        AssertSame("Sales | distinct Region", ordered: false);
    }

    [Fact]
    public void BinByDay()
    {
        AssertSame("Sales | summarize n = count() by d = bin(Timestamp, 1d) | sort by d asc");
    }

    [Fact]
    public void ArgMax()
    {
        AssertSame("Sales | summarize arg_max(Amount, Id, Customer) by Region | sort by Region asc");
        AssertSame("Sales | summarize arg_min(Timestamp, *) by Customer | sort by Customer asc");
    }

    [Fact]
    public void WindowFunctions_And_RowOperators()
    {
        AssertSame("Sales | where Region == 'US' | sort by Id asc | extend rn = row_number(), p = prev(Amount) | project Id, rn, p");
        AssertSame("Sales | where Quantity > 1 | parse Customer with who:string | project Id, who | sort by Id asc");
        Assert.Contains("WHERE", LastSql);
    }

    [Fact]
    public void Join_EfWithInMemory()
    {
        _efKql.AddTable("Customers", TestData.Customers());
        _memKql.AddTable("Customers", TestData.Customers());
        AssertSame("Sales | join kind=inner (Customers) on $left.Customer == $right.Name | project Id, Country | sort by Id asc");
    }

    /// <summary>Asserts parity and that the whole query ran as one SQL statement containing the fragments.</summary>
    protected void AssertSingleSql(string query, bool ordered, params string[] fragments)
    {
        _sql.Clear();
        AssertSame(query, ordered);
        // AssertSame runs the in-memory context too, which logs nothing; the EF query must be a single statement
        var sql = Assert.Single(_sql);
        foreach (var f in fragments) Assert.Contains(f, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("inner", "INNER JOIN")]
    [InlineData("innerunique", "JOIN")]
    [InlineData("leftouter", "LEFT JOIN")]
    [InlineData("rightouter", "LEFT JOIN")]
    public void Join_SameDatabase_RunsInSql(string kind, string sqlFragment)
    {
        AssertSingleSql($"Sales | join kind={kind} (Clients) on $left.Customer == $right.Name | project Id, Name, Country | sort by Id asc, Name asc",
            true, sqlFragment);
    }

    [Theory]
    [InlineData("leftsemi")]
    [InlineData("leftanti")]
    public void SemiAntiJoin_SameDatabase_UsesExists(string kind)
    {
        AssertSingleSql($"Sales | join kind={kind} (Clients) on $left.Customer == $right.Name | project Id | sort by Id asc", true, "EXISTS");
    }

    [Theory]
    [InlineData("rightsemi")]
    [InlineData("rightanti")]
    public void RightSemiAntiJoin_SameDatabase_UsesExists(string kind)
    {
        AssertSingleSql($"Sales | join kind={kind} (Clients) on $left.Customer == $right.Name | project Name | sort by Name asc", true, "EXISTS");
    }

    [Fact]
    public void DefaultJoin_WithFilters_AndSummarize_RunsInSql()
    {
        AssertSingleSql("""
            Sales
            | where Amount > 20
            | join (Clients | where Tier < 3) on $left.Customer == $right.Name
            | summarize total = sum(Amount) by Country
            | sort by Country asc
            """, true, "JOIN", "GROUP BY");
    }

    [Fact]
    public void Lookup_SameDatabase_RunsInSql()
    {
        AssertSingleSql("Sales | lookup (Clients | project Customer = Name, Country) on Customer | project Id, Country | sort by Id asc", true, "LEFT JOIN");
    }

    [Fact]
    public async Task ExecuteAsync_And_StreamAsync_MatchSyncResults()
    {
        const string q = "Sales | where Amount > 20 | project Id, Customer | sort by Id asc";
        var expected = _memKql.Execute(q).Rows.Select(r => string.Join("|", r)).ToList();
        var asyncResult = await _efKql.ExecuteAsync(q);
        Assert.Equal(expected, asyncResult.Rows.Select(r => string.Join("|", r)).ToList());

        var streamed = new List<string>();
        await foreach (var row in _efKql.StreamAsync(q)) streamed.Add(string.Join("|", row));
        Assert.Equal(expected, streamed);
    }

    [Fact]
    public void FullOuterJoin_FallsBackToMemory()
    {
        AssertSame("Sales | join kind=fullouter (Clients) on $left.Customer == $right.Name | project Id, Name | sort by Id asc, Name asc");
    }

    [Fact]
    public void Join_AcrossDbContexts_FallsBackToMemory()
    {
        // a second DbContext has a different query provider, so the join can't be one SQL statement
        using var other = new SalesDb(new DbContextOptionsBuilder<SalesDb>().UseSqlite(_connection).Options);
        _efKql.AddTable("OtherClients", other.Clients.AsNoTracking());
        _memKql.AddTable("OtherClients", _memKql.Execute<Client>("Clients"));
        AssertSame("Sales | join kind=inner (OtherClients) on $left.Customer == $right.Name | where Name contains 'a' | project Id, Country | sort by Id asc");
    }
}
