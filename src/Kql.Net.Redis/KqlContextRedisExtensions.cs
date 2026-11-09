using StackExchange.Redis;

namespace Kql.Net.Redis;

/// <summary>Registers Redis data as KQL tables.</summary>
public static class KqlContextRedisExtensions
{
    /// <summary>String keys matching a pattern (e.g. <c>"config:*"</c>) as a table with <c>key</c> and <c>value</c> columns.</summary>
    public static KqlContext AddRedisStrings(this KqlContext context, string name, IConnectionMultiplexer redis, string pattern,
        RedisSourceOptions? options = null) =>
        context.AddTable(name, RedisTableSources.Strings(redis, pattern, options));

    /// <summary>Hash keys matching a pattern (e.g. <c>"user:*"</c>): one row per hash, one column per field.</summary>
    public static KqlContext AddRedisHashes(this KqlContext context, string name, IConnectionMultiplexer redis, string pattern,
        RedisSourceOptions? options = null) =>
        context.AddTable(name, RedisTableSources.Hashes(redis, pattern, options));

    /// <summary>RedisJSON documents at keys matching a pattern: one row per document, one column per top-level property.</summary>
    public static KqlContext AddRedisJson(this KqlContext context, string name, IConnectionMultiplexer redis, string pattern,
        RedisSourceOptions? options = null) =>
        context.AddTable(name, RedisTableSources.Json(redis, pattern, options));

    /// <summary>A Redis stream: one row per entry, with <c>id</c>, <c>timestamp</c> and one column per field.</summary>
    public static KqlContext AddRedisStream(this KqlContext context, string name, IConnectionMultiplexer redis, string streamKey,
        RedisSourceOptions? options = null) =>
        context.AddTable(name, RedisTableSources.Stream(redis, streamKey, options));
}
