# Kql.Net coverage

Parsing and semantic checking cover **all** of KQL: Kql.Net uses Microsoft's `Kusto.Language` parser, so every
valid query parses, and errors come back as `KqlDiagnostic`s with positions. The tables below list what is
**executed**. Anything else throws `KqlNotSupportedException` (with the feature name and position) rather than
returning wrong results.

"SQL" means the operator is translated by an `IQueryable` provider such as EF Core when its input is still
provider-backed. "Memory" means it runs in .NET. The query is pushed down up to the first such operator, and
the rest of the pipeline runs in memory.

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
| `join` (all 9 kinds, `on col`, `on $left.a == $right.b`) | ✅ | Memory |
| `lookup` (leftouter/inner) | ✅ | Memory |
| `union` (incl. `withsource=`, outer schema) | ✅ | SQL if all inputs share a provider |
| `let` (scalar, tabular, user-defined scalar and tabular functions), `toscalar()`, `materialize()` | ✅ | n/a |
| `print`, `range`, `datatable`, `getschema`, `as`, `serialize` (no expressions) | ✅ | Memory |
| Multiple statements (`ExecuteAll`) | ✅ | n/a |
| `mv-expand`, `mv-apply`, `parse`, `parse-kv`, `serialize` with window functions, `top-nested`, `make-series` | ⏳ Phase 5 | |
| `arg_max` / `arg_min` (multi-column aggregates) | ⏳ Phase 2 follow-up | |
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

Deterministic calls with constant arguments (e.g. `ago(1d)`) are evaluated once at compile time, so a provider
sees a constant. Register more functions with `FunctionRegistry.Register(typeof(MyFunctions))`, using static
methods marked `[KqlFunction("name")]`.

## Aggregates

`count`, `countif`, `sum`, `sumif`, `avg`, `avgif`, `min`, `max`, `minif`, `maxif`, `dcount`/`count_distinct`,
`dcountif`, `take_any`/`any`, `take_anyif`, `make_list`, `make_list_if`, `make_list_with_nulls`, `make_set`,
`make_set_if`, `make_bag`, `stdev`, `stdevp`, `variance`, `variancep`, `percentile` (nearest rank).

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
| Custom | implement `IKqlTableSource` |
| CSV, Redis, `IAsyncEnumerable` | ⏳ Phase 6 |
