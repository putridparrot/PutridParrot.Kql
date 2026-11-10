# Kql.Net

[![CI](https://github.com/putridparrot/Kql.Net/actions/workflows/ci.yml/badge.svg)](https://github.com/putridparrot/Kql.Net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PutridParrot.Kql.Net.svg)](https://www.nuget.org/packages/PutridParrot.Kql.Net)

Run [Kusto Query Language (KQL)](https://learn.microsoft.com/kusto/query/) queries over .NET objects and
databases.

```
dotnet add package PutridParrot.Kql.Net
dotnet add package PutridParrot.Kql.Net.EntityFrameworkCore   # optional: more KQL runs as SQL
dotnet add package PutridParrot.Kql.Net.Redis                 # optional: Redis sources
dotnet add package PutridParrot.Kql.Net.Geo                   # optional: H3, buffers, unions, IP geolocation
```

The packages target .NET 10. Namespaces are `Kql.Net`, `Kql.Net.EntityFrameworkCore`, `Kql.Net.Redis` and
`Kql.Net.Geo`.

```csharp
var kql = new KqlContext()
    .AddTable("Orders", orders)              // List<Order>, arrays, any IEnumerable<T>
    .AddTable("Customers", db.Customers)     // EF Core DbSet / any IQueryable<T>
    .AddTable("Stock", stockByProduct)       // Dictionary<string, int> -> Key, Value
    .AddJsonTable("Events", json);           // JSON array of objects

KqlResult result = kql.Execute("""
    Orders
    | where Timestamp > ago(7d)
    | join kind=inner (Customers) on $left.CustomerId == $right.Id
    | summarize Revenue = sum(Amount), Orders = count() by Country
    | top 5 by Revenue
    """);

Console.WriteLine(result);                       // text table
var rows = kql.Execute<CountryRevenue>(query);   // map to your own types / records
var table = result.ToDataTable();                // or .ToJson(), .AsDictionaries()
```

## How it works

1. **Parse and bind**: Microsoft's `Kusto.Language` parses the query and checks it against a schema built from
   your registered types. You get full KQL syntax, type checking and precise error positions
   (`KqlContext.Validate`).
2. **Compile**: the bound syntax tree is compiled into LINQ expression trees over `IQueryable`. Each row shape
   produced by `project`, `extend` or `summarize` becomes a runtime-emitted type that has the same form as a C#
   anonymous type, so LINQ providers can translate it.
3. **Execute**: for in-memory data the expression trees run as LINQ to Objects. For EF Core (or another
   provider) the query is translated to SQL up to the first operator that needs .NET-only logic, such as
   `matches regex`; the rest runs in memory.

## Entity Framework Core

The core `PutridParrot.Kql.Net` package has no EF dependency, and DbSets work with it as they are. For more of
KQL to run in the database (`contains`, `has`, `=~`, `in~`, `startswith`, `bin()`, `tolower()`, `strlen()`...),
add the `PutridParrot.Kql.Net.EntityFrameworkCore` package:

```csharp
var kql = new KqlContext()
    .UseEntityFrameworkCore()
    .AddTable("Products", db.Products.AsNoTracking());
```

```
Products | where Stock > 0 and Name contains "g" | summarize Items = count(), Value = sum(Price * Stock) by Category | sort by Value desc
```
becomes a single SQL statement:
```sql
SELECT "p"."Category", COUNT(*) AS "Items", COALESCE(SUM("p"."Price" * CAST("p"."Stock" AS REAL)), 0.0) AS "Value"
FROM "Products" AS "p"
WHERE CAST("p"."Stock" AS INTEGER) > 0 AND instr(lower("p"."Name"), 'g') > 0
GROUP BY "p"."Category" ORDER BY COALESCE(SUM(...), 0.0) DESC
```

## Redis

The `PutridParrot.Kql.Net.Redis` package queries Redis data (built on StackExchange.Redis):

```csharp
var redis = ConnectionMultiplexer.Connect("localhost");
var kql = new KqlContext()
    .AddRedisHashes("Users", redis, "user:*")      // one row per hash, one column per field
    .AddRedisJson("Orders", redis, "order:*")      // RedisJSON documents
    .AddRedisStream("Events", redis, "events");    // stream entries with id + timestamp

kql.Execute("Events | where type == 'error' | summarize count() by bin(timestamp, 5m)");
```

With RediSearch (Redis Stack / Redis 8), `AddRedisSearch` queries an index. Filters on NUMERIC and TAG fields,
`take`, numeric `top` and `count` then run in Redis as FT.SEARCH, and the rest of the query runs in memory:

```csharp
kql.AddRedisSearch("Products", redis, "idx:products");
kql.Execute("Products | where price > 100 and category == 'Tools' | top 10 by price desc");
// FT.SEARCH idx:products "(@price:[(100 +inf]) (@category:{Tools})" ...
```

## Geospatial

The core package has most of the `geo_*` functions: distances, point-in-polygon, areas, geohash and S2 cells,
WKT, and others. The `PutridParrot.Kql.Net.Geo` package adds H3 cells, buffers, unions and polygon
intersections (built on NetTopologySuite and pocketken.H3), plus `geo_info_from_ip_address` over a lookup you
supply:

```csharp
var kql = new KqlContext()
    .UseGeo(ip => myGeoIpDatabase.Find(ip))      // optional IP lookup returning GeoIpInfo
    .AddTable("Stores", stores);

kql.Execute("Stores | summarize count() by cell = geo_point_to_h3cell(Lng, Lat, 7)");
```

## Other sources and async

- **CSV:** `AddCsvTable("Logs", "logs.csv")`, with column types inferred.
- **Async sources:** pass an `IAsyncEnumerable<T>` to `AddTable`.
- **Async results:** `await kql.ExecuteAsync(query)`, or `await foreach (var row in kql.StreamAsync(query))` to
  stream rows.

See [docs/coverage.md](https://github.com/putridparrot/Kql.Net/blob/main/docs/coverage.md) for supported operators, functions, sources and EF translations.

## Layout

- `src/Kql.Net`: the core library (depends only on Microsoft.Azure.Kusto.Language)
- `src/Kql.Net.EntityFrameworkCore`: optional EF Core translations
- `src/Kql.Net.Redis`: optional Redis sources
- `src/Kql.Net.Geo`: optional geospatial functions (H3, buffers, unions, intersections, IP geolocation)
- `tests/Kql.Net.Geo.Tests`: tests for the geo package
- `tests/Kql.Net.Redis.Tests`: Redis tests. They run against a Redis Stack container via Testcontainers and are
  skipped when Docker isn't available
- `tests/Kql.Net.Tests`: core xUnit tests (no EF)
- `tests/Kql.Net.EntityFrameworkCore.Tests`: EF Core (SQLite) parity and pushdown tests
- `samples/Kql.Net.Sample`: console demo (`dotnet run --project samples/Kql.Net.Sample`)

## Building and releasing

`dotnet test Kql.Net.slnx` runs every test; `dotnet pack Kql.Net.slnx -c Release -o artifacts` builds the four
packages (with symbol packages and Source Link). Shared build settings are in `Directory.Build.props`, and
package metadata is in `src/Directory.Build.props`.

GitHub Actions ([ci.yml](https://github.com/putridparrot/Kql.Net/blob/main/.github/workflows/ci.yml)) builds, tests and packs every push and pull request, and
uploads the packages (versioned `0.0.0-ci.<run>`) as a build artifact. To release, push a version tag:

```
git tag v1.0.0
git push origin v1.0.0
```

That builds version `1.0.0` and pushes it to nuget.org. The workflow needs a `NUGET_API_KEY` secret in a
`nuget` environment (Settings → Environments). Add protection rules there if releases should need approval.
