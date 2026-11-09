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
   provider) the query is translated to SQL up to the first operator that needs .NET-only logic, such as `has`;
   the rest runs in memory.

```
Products | where Stock > 0 | summarize Items = count(), Value = sum(Price * Stock) by Category | sort by Value desc
```
becomes a single SQL statement:
```sql
SELECT "p"."Category", COUNT(*) AS "Items", COALESCE(SUM("p"."Price" * CAST("p"."Stock" AS REAL)), 0.0) AS "Value"
FROM "Products" AS "p" WHERE CAST("p"."Stock" AS INTEGER) > 0
GROUP BY "p"."Category" ORDER BY COALESCE(SUM(...), 0.0) DESC
```

See [docs/coverage.md](docs/coverage.md) for supported operators, functions and sources.

## Layout

- `src/Kql.Net`: the library
- `tests/Kql.Net.Tests`: xUnit tests, including EF Core (SQLite) parity tests
- `samples/Kql.Net.Sample`: console demo (`dotnet run --project samples/Kql.Net.Sample`)
