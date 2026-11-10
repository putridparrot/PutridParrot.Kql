using Kql.Net.Redis.Search;
using StackExchange.Redis;

namespace Kql.Net.Redis.Tests;

[Collection("redis")]
public sealed class RedisSearchTests(RedisFixture fixture)
{
    private IConnectionMultiplexer Redis
    {
        get
        {
            Skip.If(fixture.Redis is null, fixture.UnavailableReason);
            return fixture.Redis!;
        }
    }

    private (KqlContext Search, KqlContext Memory, List<string> Commands) Contexts(bool pushDownText = false)
    {
        var commands = new List<string>();
        var search = new KqlContext().AddRedisSearch("Products", Redis, "idx:products",
            new RedisSearchOptions { PushDownText = pushDownText, PageSize = 64, OnCommand = commands.Add });
        // the same documents, queried purely in memory
        var memory = new KqlContext().AddTable("Products", search.Execute("Products").ToDataTable());
        commands.Clear();
        return (search, memory, commands);
    }

    private static List<string> Rows(KqlResult r, bool ordered) =>
        ordered ? r.Rows.Select(x => string.Join("|", x)).ToList() : r.Rows.Select(x => string.Join("|", x)).Order(StringComparer.Ordinal).ToList();

    [SkippableFact]
    public void Schema_ComesFromTheIndex()
    {
        var (search, _, _) = Contexts();
        var columns = search.GetResultColumns("Products");
        Assert.Equal(["key", "name", "category", "price", "stock", "description"], columns.Select(c => c.Name));
        Assert.Equal(["string", "string", "string", "real", "real", "string"], columns.Select(c => c.KqlType));
        Assert.Equal(RedisFixture.ProductCount, search.Execute("Products").Count);
    }

    [SkippableTheory]
    [InlineData("Products | where price > 100 and category == 'Tools' | project key, price")]
    [InlineData("Products | where category in ('Garden', 'Kitchen') or price < 10 | project key")]
    [InlineData("Products | where category =~ 'tools' | project key")]
    [InlineData("Products | where category in~ ('TOOLS', 'garden') | project key")]
    [InlineData("Products | where category == 'Home Office' | project key")]
    [InlineData("Products | where category == 'tools' | project key")]
    [InlineData("Products | where isnull(stock) | project key")]
    [InlineData("Products | where isnotnull(stock) and stock != 7 | project key")]
    [InlineData("Products | where stock != 7 | project key")]
    [InlineData("Products | where not(price between (10 .. 50)) | project key")]
    [InlineData("Products | where price in (3, 4.5, 6) | project key, price")]

    [InlineData("Products | where description has 'stainless' and price < 100 | project key")]
    [InlineData("Products | where name == 'Product 5' | project key")]
    [InlineData("Products | where strlen(name) > 10 and price > 290 | project key")]
    [InlineData("Products | extend p2 = price * 2 | where p2 > 100 and category == 'Garden' | project key, p2")]
    [InlineData("Products | project k = key, c = category, price | where c == 'Kitchen' and price <= 30 | project k")]
    [InlineData("Products | summarize n = count(), avg(price) by category")]
    [InlineData("Products | where price > 250 | count")]
    [InlineData("Products | where category == 'Tools' | count")]
    public void MatchesInMemoryResults(string query)
    {
        foreach (var pushDownText in new[] { false, true })
        {
            var (search, memory, _) = Contexts(pushDownText);
            Assert.Equal(Rows(memory.Execute(query), false), Rows(search.Execute(query), false));
        }
    }

    [SkippableTheory]
    [InlineData("Products | top 5 by price desc | project key, price")]
    [InlineData("Products | top 5 by price asc | project key, price")]
    [InlineData("Products | top 5 by stock desc | project key, stock")]
    [InlineData("Products | top 5 by stock asc | project stock")]                       // nulls first: any five of them
    [InlineData("Products | where stock > 190 | top 20 by stock asc | project key, stock")] // fewer than 20 non-null
    [InlineData("Products | where price > 100 | sort by price desc | take 3 | project key")]
    [InlineData("Products | sort by category asc, price desc | take 4 | project key")]
    [InlineData("Products | sort by name asc | take 3 | project name")]
    public void OrderedResultsMatch(string query)
    {
        var (search, memory, _) = Contexts();
        Assert.Equal(Rows(memory.Execute(query), true), Rows(search.Execute(query), true));
    }

    [SkippableFact]
    public void FiltersArePushedIntoFtSearch()
    {
        var (search, _, commands) = Contexts();
        search.Execute("Products | where price > 100 and category == 'Tools' | project key");
        var command = Assert.Single(commands);
        Assert.Contains("@price:[(100 +inf]", command);
        Assert.Contains("@category:{Tools}", command);

        commands.Clear();
        search.Execute("Products | where category == 'Home Office' | project key");
        Assert.Contains(@"@category:{Home\ Office}", Assert.Single(commands));

        // untranslatable conditions don't prevent the rest from being pushed
        commands.Clear();
        search.Execute("Products | where strlen(name) > 10 and stock < 50 | project key");
        Assert.All(commands, c => Assert.Contains("@stock:[-inf (50]", c));
    }

    [SkippableFact]
    public void CountAndTopAreAnsweredByRedis()
    {
        var (search, _, commands) = Contexts();
        Assert.Equal(26L, search.Execute("Products | where price >= 262.5 | count").Scalar());
        Assert.Contains("LIMIT 0 0", Assert.Single(commands));

        commands.Clear();
        search.Execute("Products | top 3 by price desc");
        Assert.Contains("SORTBY price DESC LIMIT 0 3", Assert.Single(commands));

        commands.Clear();
        Assert.Equal(7, search.Execute("Products | where category == 'Garden' | take 7").Count);
        // a TAG match is a superset of `==` (tags are case-insensitive), so the limit can't be pushed with it
        Assert.DoesNotContain("LIMIT 0 7", string.Join("\n", commands));

        commands.Clear();
        Assert.Equal(7, search.Execute("Products | where price < 100 | take 7").Count);
        Assert.Contains("LIMIT 0 7", Assert.Single(commands));
    }

    [SkippableFact]
    public void TextPushdown_IsOptIn()
    {
        var (search, _, commands) = Contexts();
        search.Execute("Products | where description has 'stainless'");
        Assert.DoesNotContain("@description", string.Join("\n", commands));

        (search, _, commands) = Contexts(pushDownText: true);
        Assert.Equal(40, search.Execute("Products | where description has 'stainless'").Count);
        Assert.Contains("@description:(stainless)", string.Join("\n", commands));

        // the documented trade-off: "steel_knife" is one RediSearch term, but two KQL terms
        var (plain, _, _) = Contexts();
        Assert.Equal(40, plain.Execute("Products | where description has 'knife'").Count);
        Assert.Empty(search.Execute("Products | where description has 'knife'").Rows);
    }

    [SkippableFact]
    public void JsonIndex()
    {
        var kql = new KqlContext().AddRedisSearch("Docs", Redis, "idx:docs");
        var columns = kql.GetResultColumns("Docs");
        Assert.Equal(["key", "title", "year", "tags"], columns.Select(c => c.Name));
        Assert.Equal("dynamic", columns[3].KqlType);

        var r = kql.Execute("Docs | where year > 2000 | sort by year asc | project ['title'], year, tags");
        Assert.Equal(["Beta", "Gamma"], r.Column("title"));
        Assert.Equal(2005.0, r[0, "year"]);
        Assert.Equal(["b"], (List<object?>)r[0, "tags"]!);
        Assert.Equal(1L, kql.Execute("Docs | where set_has_element(tags, 'a') | count").Scalar());
    }
}
