using StackExchange.Redis;

namespace PutridParrot.Kql.Redis.Tests;

[Collection("redis")]
public sealed class RedisSourceTests(RedisFixture fixture)
{
    private IConnectionMultiplexer Redis
    {
        get
        {
            Skip.If(fixture.Redis is null, fixture.UnavailableReason);
            return fixture.Redis!;
        }
    }

    [SkippableFact]
    public void Strings()
    {
        var kql = new KqlContext().AddRedisStrings("Config", Redis, "config:*");
        var r = kql.Execute("Config | extend name = tostring(split(key, ':')[1]) | where value > 10 | project name, value | sort by name asc");
        Assert.Equal(["threshold", "timeout"], r.Column("name"));
        Assert.Equal([250L, 30L], r.Column("value")); // inferred as long
    }

    [SkippableFact]
    public void Hashes_InferSchema_AndMissingFieldsAreNull()
    {
        var kql = new KqlContext().AddRedisHashes("Users", Redis, "user:*");
        var schema = kql.Execute("Users | getschema");
        Assert.Equal(["key", "name", "age", "city", "joined"], schema.Column("ColumnName"));
        Assert.Equal(["string", "string", "long", "string", "datetime"], schema.Column("ColumnType"));

        var r = kql.Execute("Users | where age > 30 | project name, city | sort by name asc");
        Assert.Equal(["alice", "carol"], r.Column("name"));
        Assert.Equal(["Paris", null], r.Column("city"));
    }

    [SkippableFact]
    public void Hashes_ManyKeys_AreScannedInBatches()
    {
        var kql = new KqlContext().AddRedisHashes("Bulk", Redis, "bulk:*", new RedisSourceOptions { BatchSize = 100, ScanPageSize = 250 });
        var r = kql.Execute("Bulk | summarize n = count(), evens = countif(even), total = sum(n)");
        Assert.Equal(1200L, r[0, "n"]);
        Assert.Equal(600L, r[0, "evens"]);
        Assert.Equal(Enumerable.Range(100, 1200).Sum(x => (long)x), r[0, "total"]);
    }

    [SkippableFact]
    public void Json_Documents_WithNestedValues()
    {
        var kql = new KqlContext().AddRedisJson("Orders", Redis, "order:*");
        var r = kql.Execute("""
            Orders
            | extend country = tostring(shipping.country)
            | summarize revenue = sum(total), orders = count() by customer, country
            | sort by customer asc
            """);
        Assert.Equal(["alice", "bob"], r.Column("customer"));
        Assert.Equal([420.5, 75.0], r.Column("revenue"));
        Assert.Equal(["FR", "DE"], r.Column("country"));

        var items = kql.Execute("Orders | mv-expand item = items | summarize qty = sum(tolong(item.qty)) by sku = tostring(item.sku) | sort by sku asc");
        Assert.Equal(["A", "B", "C"], items.Column("sku"));
        Assert.Equal([2L, 1L, 5L], items.Column("qty"));
    }

    [SkippableFact]
    public void Stream_EntriesWithTimestamps_AcrossPages()
    {
        var kql = new KqlContext().AddRedisStream("Events", Redis, "events", new RedisSourceOptions { BatchSize = 10 });
        var r = kql.Execute("Events | summarize n = count(), errors = countif(type == 'error'), maxMs = max(ms)");
        Assert.Equal(25L, r[0, "n"]);
        Assert.Equal(9L, r[0, "errors"]);
        Assert.Equal(240L, r[0, "maxMs"]);

        var latest = kql.Execute("Events | top 1 by timestamp desc | project timestamp");
        Assert.InRange((DateTime)latest.Scalar()!, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [SkippableFact]
    public void DataIsReadOnEachQuery()
    {
        var db = Redis.GetDatabase();
        var kql = new KqlContext().AddRedisStrings("Live", Redis, "live:*");
        db.StringSet("live:a", "1");
        try
        {
            var before = (long)kql.Execute("Live | count").Scalar()!;
            db.StringSet("live:b", "2");
            Assert.Equal(before + 1, kql.Execute("Live | count").Scalar());
        }
        finally
        {
            db.KeyDelete(["live:a", "live:b"]);
        }
    }

    [SkippableFact]
    public void JoinRedisWithInMemoryObjects()
    {
        var kql = new KqlContext()
            .AddRedisHashes("Users", Redis, "user:*")
            .AddTable("Cities", new[] { new City("Paris", "FR"), new City("Berlin", "DE") });
        var r = kql.Execute("Users | join kind=leftouter (Cities) on $left.city == $right.Name | project name, Country | sort by name asc");
        Assert.Equal(["FR", "DE", null], r.Column("Country"));
    }

    public sealed record City(string Name, string Country);
}
