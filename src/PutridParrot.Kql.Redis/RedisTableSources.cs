using PutridParrot.Kql.Runtime;
using StackExchange.Redis;

namespace PutridParrot.Kql.Redis;

/// <summary>Options shared by the Redis table sources.</summary>
public sealed record RedisSourceOptions
{
    /// <summary>Database number (default: the connection's default database).</summary>
    public int Database { get; init; } = -1;

    /// <summary>Name of the column that holds each row's Redis key (default <c>key</c>).</summary>
    public string KeyColumn { get; init; } = "key";

    /// <summary>Keys requested per SCAN page (default 1000).</summary>
    public int ScanPageSize { get; init; } = 1000;

    /// <summary>Values fetched per pipelined batch (default 500).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>How many keys/entries to sample when inferring the schema (default 200).</summary>
    public int InferenceSampleSize { get; init; } = 200;

    /// <summary>Explicit column types, overriding inference (<c>typeof(object)</c> = dynamic).</summary>
    public IReadOnlyDictionary<string, Type>? ColumnTypes { get; init; }
}

/// <summary>
/// Table sources over Redis data. Keys are found with SCAN (never KEYS) and values are fetched in pipelined batches.
/// The schema is inferred from a sample when the source is created; data is read again on every query.
/// All query operators run in memory over the fetched rows.
/// </summary>
public static class RedisTableSources
{
    /// <summary>String keys matching <paramref name="pattern"/>: columns <c>key</c> and <c>value</c> (type inferred).</summary>
    public static RowSetTableSource Strings(IConnectionMultiplexer redis, string pattern, RedisSourceOptions? options = null)
    {
        options ??= new RedisSourceOptions();
        var o = options;
        IEnumerable<(RedisKey Key, string? Value)> Fetch() => Batched(redis, pattern, o,
            (batch, key) => batch.StringGetAsync(key), (key, value) => (key, (string?)value));

        var sample = Fetch().Take(o.InferenceSampleSize).ToList();
        var valueType = Override(o, "value") ?? TextTypeInference.Infer(sample.Select(s => s.Value));
        return new RowSetTableSource([(o.KeyColumn, typeof(string)), ("value", valueType)],
            () => Fetch().Select(s => new object?[] { (string?)s.Key, Convert(s.Value, valueType) }));
    }

    /// <summary>
    /// Hash keys matching <paramref name="pattern"/>: a <c>key</c> column, then one column per hash field seen in the
    /// sample (types inferred from the values). Fields missing from a hash are null.
    /// </summary>
    public static RowSetTableSource Hashes(IConnectionMultiplexer redis, string pattern, RedisSourceOptions? options = null)
    {
        options ??= new RedisSourceOptions();
        var o = options;
        IEnumerable<(RedisKey Key, HashEntry[] Fields)> Fetch() => Batched(redis, pattern, o,
            (batch, key) => batch.HashGetAllAsync(key), (key, fields) => (key, fields));

        var sample = Fetch().Take(o.InferenceSampleSize).ToList();
        // SCAN order isn't stable, so take field order from the sample in key order (stable column order across runs)
        var fieldNames = new List<string>();
        foreach (var (_, fields) in sample.OrderBy(s => (string?)s.Key, StringComparer.Ordinal))
            foreach (var f in fields)
                if (!fieldNames.Contains(f.Name!)) fieldNames.Add(f.Name!);

        var types = fieldNames.Select(name => Override(o, name) ?? TextTypeInference.Infer(
            sample.Select(s => (string?)s.Fields.FirstOrDefault(f => f.Name == name).Value))).ToArray();
        var columns = new List<(string, Type)> { (o.KeyColumn, typeof(string)) };
        columns.AddRange(fieldNames.Select((n, i) => (n, types[i])));
        var index = fieldNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);

        return new RowSetTableSource(columns, () => Fetch().Select(h =>
        {
            var row = new object?[fieldNames.Count + 1];
            row[0] = (string?)h.Key;
            foreach (var f in h.Fields)
                if (index.TryGetValue(f.Name!, out var i)) row[i + 1] = Convert(f.Value, types[i]);
            return row;
        }));
    }

    /// <summary>
    /// RedisJSON documents (JSON.GET) at keys matching <paramref name="pattern"/>: a <c>key</c> column plus one column per
    /// top-level property (nested objects and arrays become <c>dynamic</c>). Requires the RedisJSON module (Redis Stack / Redis 8).
    /// </summary>
    public static RowSetTableSource Json(IConnectionMultiplexer redis, string pattern, RedisSourceOptions? options = null)
    {
        options ??= new RedisSourceOptions();
        var o = options;
        IEnumerable<IDictionary<string, object?>> Fetch() => Batched(redis, pattern, o,
            (batch, key) => batch.ExecuteAsync("JSON.GET", key),
            (key, result) =>
            {
                var doc = result.IsNull ? null : Dynamic.Parse((string?)result);
                var row = new Dictionary<string, object?> { [o.KeyColumn] = (string?)key };
                if (doc is Dictionary<string, object?> bag)
                    foreach (var (k, v) in bag) row[k] = v;
                else
                    row["value"] = doc;
                return (IDictionary<string, object?>)row;
            });

        IReadOnlyList<(string, Type)>? columns = null;
        if (o.ColumnTypes is { Count: > 0 })
        {
            var inferred = RowSetTableSource.FromDictionaries(Fetch(), inferenceSampleSize: o.InferenceSampleSize);
            columns = inferred.Columns.Select(c => (c.Name, Override(o, c.Name) ?? c.Type)).ToList();
        }
        return RowSetTableSource.FromDictionaries(Fetch(), columns, o.InferenceSampleSize);
    }

    /// <summary>
    /// Entries of a Redis stream: <c>id</c>, <c>timestamp</c> (from the entry id) and one column per field seen in the sample.
    /// Entries are read in pages with XRANGE.
    /// </summary>
    public static RowSetTableSource Stream(IConnectionMultiplexer redis, string streamKey, RedisSourceOptions? options = null)
    {
        options ??= new RedisSourceOptions();
        var o = options;
        var db = redis.GetDatabase(o.Database);

        IEnumerable<StreamEntry> Fetch()
        {
            RedisValue start = "-";
            while (true)
            {
                var page = db.StreamRange(streamKey, start, "+", o.BatchSize);
                foreach (var e in page) yield return e;
                if (page.Length < o.BatchSize) yield break;
                start = "(" + page[^1].Id; // exclusive start (Redis 6.2+)
            }
        }

        var sample = Fetch().Take(o.InferenceSampleSize).ToList();
        var fieldNames = new List<string>();
        foreach (var e in sample)
            foreach (var v in e.Values)
                if (!fieldNames.Contains(v.Name!)) fieldNames.Add(v.Name!);
        var types = fieldNames.Select(name => Override(o, name) ?? TextTypeInference.Infer(
            sample.Select(e => (string?)e.Values.FirstOrDefault(v => v.Name == name).Value))).ToArray();
        var columns = new List<(string, Type)> { ("id", typeof(string)), ("timestamp", typeof(DateTime)) };
        columns.AddRange(fieldNames.Select((n, i) => (n, types[i])));
        var index = fieldNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);

        return new RowSetTableSource(columns, () => Fetch().Select(e =>
        {
            var row = new object?[fieldNames.Count + 2];
            var id = (string?)e.Id;
            row[0] = id;
            row[1] = TimestampOf(id);
            foreach (var v in e.Values)
                if (index.TryGetValue(v.Name!, out var i)) row[i + 2] = Convert(v.Value, types[i]);
            return row;
        }));
    }

    /// <summary>The time encoded in a stream entry id (<c>&lt;milliseconds&gt;-&lt;sequence&gt;</c>).</summary>
    public static DateTime? TimestampOf(string? streamId)
    {
        if (streamId is null) return null;
        var dash = streamId.IndexOf('-');
        return long.TryParse(dash < 0 ? streamId : streamId[..dash], out var ms) ? DateTime.UnixEpoch.AddMilliseconds(ms) : null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>SCANs keys matching the pattern on every primary, fetching values in pipelined batches.</summary>
    private static IEnumerable<TRow> Batched<TValue, TRow>(IConnectionMultiplexer redis, string pattern, RedisSourceOptions o,
        Func<IBatch, RedisKey, Task<TValue>> get, Func<RedisKey, TValue, TRow> map)
    {
        var db = redis.GetDatabase(o.Database);
        var dbNumber = o.Database < 0 ? db.Database : o.Database;
        foreach (var server in redis.GetServers().Where(s => s.IsConnected && !s.IsReplica))
        {
            foreach (var chunk in server.Keys(dbNumber, pattern, o.ScanPageSize).Chunk(o.BatchSize))
            {
                var batch = db.CreateBatch();
                var tasks = chunk.Select(k => get(batch, k)).ToArray();
                batch.Execute();
                Task.WaitAll(tasks);
                for (var i = 0; i < chunk.Length; i++) yield return map(chunk[i], tasks[i].Result);
            }
        }
    }

    private static Type? Override(RedisSourceOptions o, string column) =>
        o.ColumnTypes is not null && o.ColumnTypes.TryGetValue(column, out var t) ? t : null;

    private static object? Convert(RedisValue value, Type type) =>
        value.IsNull ? null : type == typeof(object) ? Dynamic.Parse((string?)value) : TextTypeInference.Parse((string?)value, type);
}
