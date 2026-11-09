# Kql.Net

Run [Kusto Query Language (KQL)](https://learn.microsoft.com/kusto/query/) queries over .NET objects and
databases.

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

The core `Kql.Net` package has no EF dependency, and DbSets work with it as they are. For more of KQL to run in
the database (`contains`, `has`, `=~`, `in~`, `startswith`, `bin()`, `tolower()`, `strlen()`...), add the
`Kql.Net.EntityFrameworkCore` package:

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

The `Kql.Net.Redis` package queries Redis data (built on StackExchange.Redis):

```csharp
var redis = ConnectionMultiplexer.Connect("localhost");
var kql = new KqlContext()
    .AddRedisHashes("Users", redis, "user:*")      // one row per hash, one column per field
    .AddRedisJson("Orders", redis, "order:*")      // RedisJSON documents
    .AddRedisStream("Events", redis, "events");    // stream entries with id + timestamp

kql.Execute("Events | where type == 'error' | summarize count() by bin(timestamp, 5m)");
```

## Other sources and async

- **CSV:** `AddCsvTable("Logs", "logs.csv")`, with column types inferred.
- **Async sources:** pass an `IAsyncEnumerable<T>` to `AddTable`.
- **Async results:** `await kql.ExecuteAsync(query)`, or `await foreach (var row in kql.StreamAsync(query))` to
  stream rows.

See [docs/coverage.md](docs/coverage.md) for supported operators, functions, sources and EF translations.

## Layout

- `src/Kql.Net`: the core library (depends only on Microsoft.Azure.Kusto.Language)
- `src/Kql.Net.EntityFrameworkCore`: optional EF Core translations
- `src/Kql.Net.Redis`: optional Redis sources
- `tests/Kql.Net.Redis.Tests`: Redis tests. They run against a Redis Stack container via Testcontainers and are
  skipped when Docker isn't available
- `tests/Kql.Net.Tests`: core xUnit tests (no EF)
- `tests/Kql.Net.EntityFrameworkCore.Tests`: EF Core (SQLite) parity and pushdown tests
- `samples/Kql.Net.Sample`: console demo (`dotnet run --project samples/Kql.Net.Sample`)
