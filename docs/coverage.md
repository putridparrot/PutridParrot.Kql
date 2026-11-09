# Kql.Net coverage

Parsing and semantic checking cover **all** of KQL: Kql.Net uses Microsoft's `Kusto.Language` parser, so every
valid query parses, and errors come back as `KqlDiagnostic`s with positions. The tables below list what is
**executed**. Anything else throws `KqlNotSupportedException` (with the feature name and position) rather than
returning wrong results.

"SQL" means the operator is translated by an `IQueryable` provider such as EF Core when its input is still
provider-backed. "Memory" means it runs in .NET. The query is pushed down up to the first such operator, and
the rest of the pipeline runs in memory.

## EF Core translations (`Kql.Net.EntityFrameworkCore`)

The core package has no EF dependency. Plain LINQ already covers comparisons, arithmetic, `and`/`or`, `in`,
`between`, `iff`, `isnull`/`isempty`, aggregates and `arg_max`. Add the EF package and call
`kql.UseEntityFrameworkCore()` to have these run in SQL too:

| KQL | SQL form (provider-neutral) | Notes |
|---|---|---|
| `contains`, `startswith`, `endswith` (+ negations) | `lower(col)` + `Contains`/`StartsWith`/`EndsWith` | |
| `contains_cs`, `startswith_cs`, `endswith_cs` | plain string methods | case sensitivity follows the DB collation (SQL Server default is case-insensitive) |
| `=~`, `!~`, `in~` | `lower(col) = 'x'`, `lower(col) IN (...)` | |
| `like` | `EF.Functions.Like(lower(col), lower(pattern))` | `like_cs` stays in memory |
| `has`, `hasprefix`, `hassuffix`, `has_any`, `has_all` (in `where`) | substring prefilter in SQL, exact term match re-checked in memory | only where the result can just widen: under `and`/`or`; `!has` and `not(...)` run in memory |
| `tolower`, `toupper`, `strlen`, `substring` (constant positions), `strcat` | `lower`, `upper`, `length`, `substr`, `\|\|` | |
| `abs`, `round(x, n)`, `floor(x)` | `abs`, `round`, `floor` | |
| `bin(x, n)` for numbers, `bin(dt, 1d)`, `startofday` | `floor(x / n) * n`, date truncation | other time bins (e.g. `1h`) run in memory |
| `getyear`, `getmonth`, `dayofmonth`, `dayofyear`, `hourofday` | date part functions | |

Anything else, such as `matches regex`, `extract` or `parse`, falls back to memory at that operator. The
result is the same; only where the work happens changes. Write your own translator by implementing
`IKqlQueryTranslator` and registering it with `KqlContext.AddTranslator`.

## Tabular operators

| Operator | Status | Runs in |
|---|---|---|
| `where` / `filter` | ✅ | SQL unless it uses a memory-only function |
| `project`, `project-away`, `project-keep`, `project-rename`, `project-reorder` (incl. wildcards) | ✅ | SQL |
| `extend` | ✅ | SQL unless it uses a memory-only function |
| `take` / `limit`, `sample` | ✅ (`sample` = take) | SQL |
| `sort` / `order` (asc/desc, nulls first/last) | ✅ | SQL (ordinal string comparer in memory) |
| `top` | ✅ | SQL |
| `count` | ✅ | SQL |
| `distinct` (columns or `*`) | ✅ | SQL |
| `summarize … by …` | ✅ | SQL for count/sum/avg/min/max/countif/sumif/dcount; memory for the rest |
| `join` (all 9 kinds, `on col`, `on $left.a == $right.b`) | ✅ | SQL when both sides come from the same provider (e.g. one `DbContext`), except `fullouter`; memory otherwise |
| `lookup` (leftouter/inner) | ✅ | SQL / memory as for `join` |
| `union` (incl. `withsource=`, outer schema) | ✅ | SQL if all inputs share a provider |
| `let` (scalar, tabular, user-defined scalar and tabular functions), `toscalar()`, `materialize()` | ✅ | n/a |
| `print`, `range`, `datatable`, `getschema`, `as` | ✅ | Memory |
| `serialize` (with or without expressions) | ✅ | Memory when it has expressions |
| `mv-expand` (multiple columns, `to typeof()`, `with_itemindex`, `bagexpansion`, `limit`) | ✅ | Memory |
| `parse` / `parse-where` (`kind=simple`, `relaxed`, `regex`, `flags=`) | ✅ | Memory |
| Multiple statements (`ExecuteAll`) | ✅ | n/a |
| `mv-apply`, `parse-kv`, `top-nested`, `make-series` | ⏳ | |
| `render`, `evaluate` plugins, graph operators, `find`, `search`, `fork`, `facet`, cross-cluster/database | ❌ out of scope for now | |

## Scalar operators

`+ - * / %` (null on integer divide by zero), comparisons, `and`/`or`, `==`/`!=`/`=~`/`!~`,
`has`/`!has`/`has_cs`, `hasprefix`/`hassuffix` (+ `_cs`, negated), `contains`, `startswith`, `endswith` (+ `_cs`,
negated), `like`, `matches regex`, `in`/`!in`/`in~` (literal lists, dynamic arrays and subqueries), `between`/`!between`
(including `datetime .. timespan`), `has_any`, `has_all`, dynamic access `d.a`, `d['a']`, `d[0]`, `d[-1]`.

## Scalar functions

| Area | Functions |
|---|---|
| Conditional | `iff`/`iif`, `case`, `coalesce`, `isnull`, `isnotnull`, `isempty`, `isnotempty`, `not`, `column_ifexists` |
| String | `strlen`, `tolower`, `toupper`, `strcat`, `strcat_delim`, `substring`, `indexof`, `replace_string`, `replace_regex`, `trim`, `trim_start`, `trim_end`, `split`, `extract`, `extract_all`, `countof`, `reverse`, `strrep`, `hash_md5`, `hash_sha256`, `base64_encode_tostring`, `base64_decode_tostring`, `url_encode`, `url_decode` |
| Conversion | `tostring`, `toint`, `tolong`, `todouble`/`toreal`, `todecimal`, `tobool`, `todatetime`, `totimespan`, `toguid`, `parse_json`/`todynamic` |
| Math | `abs`, `round`, `bin`/`floor`, `bin_at`, `ceiling`, `sqrt`, `pow`, `exp`, `exp2`, `exp10`, `log`, `log2`, `log10`, `sign`, `pi`, `rand`, `sin`, `cos`, `tan`, `isnan`, `isinf`, `isfinite`, `min_of`, `max_of` |
| Datetime | `now`, `ago`, `startof/endof day/week/month/year`, `getyear`, `getmonth`/`monthofyear`, `dayofmonth`, `dayofyear`, `dayofweek`, `hourofday`, `week_of_year`, `datetime_add`, `datetime_diff`, `datetime_part`, `format_datetime`, `format_timespan`, `make_datetime`, `make_timespan`, `unixtime_*_todatetime` |
| Dynamic | `array_length`, `pack_array`, `pack`/`bag_pack`, `pack_all`, `bag_keys`, `array_concat`, `array_index_of`, `set_has_element`, `array_sum`, `gettype` |
| Misc | `new_guid`, `hash` |
| Window (need serialized input: after `sort`/`order`/`serialize`) | `row_number` (with start/restart), `prev`, `next` (offset, default), `row_cumsum` (restart), `row_rank_dense`, `row_rank_min`. Operators using them run in memory |

Deterministic calls with constant arguments (e.g. `ago(1d)`) are evaluated once at compile time, so a provider
sees a constant.

### Custom functions

Custom functions are registered per `KqlContext` and declared to the parser, so new names type-check like built-ins:

```csharp
var kql = new KqlContext()
    .AddFunction("double_it", (long? x) => x * 2)       // lambdas and closures
    .AddFunctions(typeof(MyFunctions));                  // static methods marked [KqlFunction("name")]
```

Using a built-in name replaces that function's implementation in this context only. Custom functions run in
memory.

## Null semantics

- **`==` and `!=`:** never true when either side is null (`null == null` is false), and the same goes for
  `!in`. Use `isnull()`/`isnotnull()` to test for nulls. `<`, `>` and the other orderings are also false with null.
  A comparison that involves null gives `false` rather than KQL's `null`; `where` treats the two the same.
- **Strings:** KQL strings are never null, so a null .NET string behaves as `""` (`Notes == ''` matches null
  `Notes`).
- **`join` / `lookup`:** keys that are null never match, but the row still appears unmatched in outer and anti joins.
- **`summarize … by`:** groups null keys together.

## Aggregates

`count`, `countif`, `sum`, `sumif`, `avg`, `avgif`, `min`, `max`, `minif`, `maxif`, `dcount`/`count_distinct`,
`dcountif`, `take_any`/`any`, `take_anyif`, `make_list`, `make_list_if`, `make_list_with_nulls`, `make_set`,
`make_set_if`, `make_bag`, `stdev`, `stdevp`, `variance`, `variancep`, `percentile` (nearest rank),
`arg_max` / `arg_min` (explicit columns or `*`; rows with a null key are ignored).

`arg_max`/`arg_min` translate to SQL as one correlated subquery per output column. That is correct, but
`arg_max(x, *)` on a wide table produces many subqueries.

## Sources

| Source | How |
|---|---|
| `List<T>`, `T[]`, any `IEnumerable<T>` | `AddTable("T", items)`: one column per public property/field |
| Scalars (`int[]`, `List<string>`) | single column named `value` |
| `Dictionary<K,V>` | `Key` and `Value` columns (`Value` is `dynamic` when V is an object) |
| Property bags (`IDictionary<string, object?>`, `ExpandoObject`) | schema inferred (mixed types become `dynamic`) |
| `DataTable` | `AddTable("T", dataTable)` |
| JSON array of objects | `AddJsonTable("T", json)` |
| `IQueryable<T>` (EF Core `DbSet`, any LINQ provider) | `AddTable("T", db.Things)`: translated where possible |
| CSV | `AddCsvTable("T", path, options)` or `AddTable("T", CsvTableSource.FromText(csv))`. Handles quoted fields, embedded commas and newlines, and custom delimiters. Types are inferred (long, real, bool, datetime, timespan, guid, string) or set explicitly; the file is re-read on each query |
| `IAsyncEnumerable<T>` | `AddTable("T", asyncRows)`, enumerated on each query, so it must be re-enumerable |
| Custom | implement `IKqlTableSource` |
| Redis (`Kql.Net.Redis` package) | `AddRedisStrings`, `AddRedisHashes`, `AddRedisJson` (RedisJSON) and `AddRedisStream`. Keys are found with SCAN and values fetched in pipelined batches; the schema is inferred from a sample and data is read on each query. Operators run in memory |

## Async results

- `ExecuteAsync(query)` returns a `KqlResult`. It reads EF Core queries with EF's own async enumeration.
- `StreamAsync(query)` yields rows as they're produced. Operators that need all their input (sort, summarize,
  in-memory joins) still buffer.
- `GetResultColumns(query)` describes a query's output columns without running it.
