using StackExchange.Redis;
using Testcontainers.Redis;

namespace Kql.Net.Redis.Tests;

/// <summary>
/// Starts a Redis Stack container (for RedisJSON) once for the test run and seeds it. If Docker isn't available the
/// tests are skipped rather than failed.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public IConnectionMultiplexer? Redis { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder("redis/redis-stack-server:7.4.0-v3").Build();
            await _container.StartAsync();
            Redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString() + ",allowAdmin=true");
            await SeedAsync(Redis.GetDatabase());
        }
        catch (Exception e)
        {
            UnavailableReason = $"Redis container unavailable: {e.GetType().Name}: {e.Message}";
        }
    }

    private static async Task SeedAsync(IDatabase db)
    {
        await db.StringSetAsync("config:timeout", "30");
        await db.StringSetAsync("config:retries", "5");
        await db.StringSetAsync("config:threshold", "250");
        await db.StringSetAsync("other:key", "ignored");

        await db.HashSetAsync("user:1", [new("name", "alice"), new("age", "34"), new("city", "Paris"), new("joined", "2023-04-01")]);
        await db.HashSetAsync("user:2", [new("name", "bob"), new("age", "27"), new("city", "Berlin"), new("joined", "2024-01-15")]);
        await db.HashSetAsync("user:3", [new("name", "carol"), new("age", "41"), new("joined", "2022-11-30")]); // no city
        for (var i = 100; i < 1300; i++) await db.HashSetAsync($"bulk:{i}", [new("n", i.ToString()), new("even", (i % 2 == 0).ToString())]);

        await db.ExecuteAsync("JSON.SET", "order:1", "$", """{"customer":"alice","total":120.5,"items":[{"sku":"A","qty":2},{"sku":"B","qty":1}],"shipping":{"country":"FR"}}""");
        await db.ExecuteAsync("JSON.SET", "order:2", "$", """{"customer":"bob","total":75,"items":[{"sku":"C","qty":5}],"shipping":{"country":"DE"}}""");
        await db.ExecuteAsync("JSON.SET", "order:3", "$", """{"customer":"alice","total":300,"items":[],"shipping":{"country":"FR"}}""");

        for (var i = 0; i < 25; i++)
            await db.StreamAddAsync("events", [new("type", i % 3 == 0 ? "error" : "info"), new("ms", (i * 10).ToString())]);
    }

    public async Task DisposeAsync()
    {
        Redis?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
