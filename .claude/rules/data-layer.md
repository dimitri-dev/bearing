# Data Layer Rules (§5.x)

Raw ADO.NET — **no ORM**. Postgres / SQL Server are query targets; SQLite is local app state.

## §5.1 — Postgres (`Bearing.Data/Postgres`)
- Npgsql directly, behind `Core` provider interfaces.
- Column origin = table OID + attnum (`TableOID`/`ColumnAttributeNumber`); `BaseTableName` is null.
- Propagate `CancellationToken` through every async DB call.

## §5.1a — MySQL / MariaDB (`Bearing.Data/MySql`)
One provider (`mysql`) and one dialect for both engines. Differences are server facts, read from the handshake
version (`MySqlServerFlavor`) where they matter — never a user setting.
- `INSERT … RETURNING` only on MariaDB 10.5+; the executor picks `SqlWithoutReturning` up front elsewhere. No
  engine reads back an UPDATE.
- Ids are derived (`MySqlMetadataReader.StableId`, FNV over schema+name) and resolved back by re-hashing the
  table list. A database is the snapshot's one schema; column origin is by name with the database as catalog.
- A run of non-row statements comes back as one result with the batch total (measured; like SqlClient).
- A killed `SLEEP()`/`BENCHMARK()` *returns* (1 / 0) instead of failing — use a real query in timeout tests.
- `AllowUserVariables` on, `ConvertZeroDateTime` on, `AllowLoadLocalInfile`/`AllowPublicKeyRetrieval` reserved.
- `ExecuteWriteAsync` **prepares** each parameterized command (binary binding): the driver inlines unprepared
  values with escaping that ignores the server's `NO_BACKSLASH_ESCAPES`. `NoBackslashEscapes`/`IgnorePrepare` reserved.
- Reads are lossless: `TreatTinyAsBoolean` **off** (`tinyint(1)` holding 2 reads 2, not True) and `GuidFormat=None`
  (`char(36)` is text — a non-GUID value would fail the whole result). Both stay settable from the options bag.
- Session time zone is **not** supported (named zones need the server's tz tables).

## §5.1b — DynamoDB (`Bearing.Data/DynamoDb`)
Provider `dynamodb`, read through PartiQL `ExecuteStatement` over the AWS SDK for .NET — the one engine with no
connection, no password and no pool. Everything is behind `IDynamoDbApi` (the three calls Bearing makes:
`ExecuteStatement`, `ListTables`, `DescribeTable`), so the executor and reader are tested against a hand-rolled fake.
- Settings: region = `ConnectionInfo.Host` (required), AWS profile = `User` (blank = the SDK's default credential
  chain), `Options["Endpoint"]` for DynamoDB Local or a VPC endpoint. `https://` to any host; `http://` only for a
  loopback host (`localhost`, `127.0.0.0/8`, `::1`) — a refused setting fails the **connect** with its sentence, not
  the constructor. Loopback + blank profile signs with letters-only placeholder keys (DynamoDB Local rejects a hyphen
  in the key id); anything else is the SDK's. Secrets: §1.9c.
- Ids are derived, as MySQL's (§5.1a): a hash of the table name, re-hashed against the table list. One database,
  named after the region, with one schema named `""`; columns are the declared key attributes only (partition key
  ordinal 1, sort key 2; index-only keys follow, nullable), and `GetTableDetailsAsync` lists the indexes the
  snapshot's ordinals point at. `DescribeTable` is cached per session; a snapshot refresh forgets it.
- Attribute sample (`SampleAttributesAsync`, 2026-11-19): **only on a table's expand**, one `ExecuteStatement`
  with `Limit` 20 and no continuation, keys left out, rows marked `sampled` — a sample, never the schema. Refusals
  are typed (`AttributeSample.Refused`, scrubbed), only successes are cached until the next snapshot; it never feeds
  completion or the snapshot (the key ordinals drive the scan check). Other engines are **never asked**
  (`ProviderTraits.SamplesAttributes`, checked in the node and in `SchemaBrowser` before a reader is built — asking
  would connect, and a failure would hang "couldn't sample" under a table the catalog already described).
- **No `LIMIT`/`OFFSET`.** A read continues from DynamoDB's `NextToken`, carried opaquely in
  `QueryResult.Continuation` and handed back to `IQueryExecutor.ExecuteContinuationAsync`; `Truncated` means "a
  token remains", never a row count. The token holds statement text and a server token: **in memory only — never
  logged, persisted or shown** (§1.3), and decoding refuses anything but a token the class wrote.
- A page ends at the row cap, when DynamoDB sends no `NextToken`, or after 10 requests — whichever is first. A page
  cut short by the 10 says so in `Message`. Fetch all reads to the end with no request budget.
- **`Limit` only on a read without `WHERE`** (user decision 2026-11-19, PR #192 review; measured by
  `DynamoDbLiveTests.Limit_bounds_the_items_evaluated_not_the_items_returned`). `Limit` caps items *evaluated*, not
  returned. No `WHERE` (`PartiQlBatch.HasWhere` false): `Limit` = rows still wanted, the page is exact, and an
  unfiltered scan's first page reads about one page of items (§1.9c). Any `WHERE`: no `Limit`, each request evaluates
  up to 1 MB and returns every match, so a page can exceed the cap by one request's matches — **never drop them**,
  the token is past them. `ExecuteContinuationAsync` therefore promises *about* `maxRows`. Whoever must hold a cap
  trims and says truncated: `StreamRowsAsync` (exact), the `bearing` command's `--max-rows` (`BearingHost.Capped`,
  both export paths). The App shows the overshoot.
- Columns are fixed by the first page (§9.10): key attributes first, then the rest alphabetically — plus every name
  a plain projection lists (`PartiQlBatch.Projection`: one name per item, else null), so a filtered read whose first
  page is empty still gets its columns. `SELECT *` with an empty first page has nothing to name them from: key
  columns only, the rest in the overflow column (known limitation). A result that **continues** always ends with an
  `(other attributes)` JSON column — projections included — because a later page can bring an attribute the first
  did not show, or a value the first page's column type cannot hold; a result that fits one page never has it. A real
  attribute of that name pushes the overflow column to `(other attributes 2)` (`DynamoDbColumnLayout.OverflowName`).
- A continuation goes back only to the session that issued it: `ResultSetViewModel.SourceSession`, compared in
  `ContinuationPaging`; a set without one is never continued. Count is never offered on a token result
  (`ResultSetViewModel.CanCount`).
- Values: `S` text; `N` a decimal when every value in the column fits one exactly, else text; `BOOL`; `B` bytes;
  `M`, `L` and the sets as JSON text. A missing attribute and a DynamoDB `NULL` both read as null (a known
  limitation — keep them indistinguishable on purpose, never invent a difference).
- Never offered, and each answers "no" rather than an approximation: grid editing (`GridEditUnavailable`), manual
  commit and transactions (`SupportsManualCommit` false, `CreateTransactionScope` throws), counts and offset paging
  (`CountWrap`/`TryAppendPage` null), query plans, server activity, TLS modes, session time zone, `:name` variables,
  SSH tunnels (`SupportsSshTunnel` false: the SSH tab is hidden, and a tunnel a hand-edited project names is refused
  in `ConnectionFactoryBuilder.RouteAsync`, never routed).
  The timeout is a client-side cancel of the page read (the SQL Server arrangement, §1.9a).

## §5.1c — Elasticsearch / OpenSearch (`Bearing.Data/Search`)
Providers `elasticsearch` and `opensearch`, **query-only**, over one implementation (`SearchProvider`,
`SearchEngine`) on plain `HttpClient` + JSON — no client library, no package. Two ids because a provider is paired
with its dialect by id (`ProviderTraits`), and the two differ in quoting, endpoints, statement lists and sign-in.
Measured against Elasticsearch 9.5.6 and OpenSearch 3.10.0 containers (`SearchLiveTests`). Security: §1.9d.
- **One door:** every request (executor, reader, Test) goes through `SearchConnectionFactory.SendAsync` — scheme,
  `Prefer`, redirects, 401 and signing live there; never hold the `HttpClient` around it. Answers cap at 64 MB
  (declared, chunked and decompressed bytes alike). Port 0 = 9200; any other port outside 1–65535 is refused.
- **Four endpoints:** Elasticsearch SQL `/_sql`, ES|QL `/_query`, OpenSearch SQL `/_plugins/_sql`, PPL
  `/_plugins/_ppl`. One statement per request, no trailing `;` (Elasticsearch answers 400 to one); a batch stops
  at its first failure; a bare `;` sends nothing but keeps its `StatementIndex`.
- **Language per statement, from its first word** (no tab mode): an unknown first word is piped when it has a
  top-level `|`, else SQL. Decided in `SearchScan` (`Sql`) **and** `SearchBatch` (`Data` may not reference `Sql`)
  — **two copies kept equal by `SearchScanParityTests`; change them together.** Block comments nest in
  Elasticsearch SQL and ES|QL, not in OpenSearch SQL or PPL (measured).
- **Paging is the server's SQL cursor**, in `QueryResult.Continuation` (`SearchContinuation`: engine, cursor, column
  layout — never the statement). Continuing sends **only the cursor**, to the endpoint that issued it; a rejected
  cursor is `CursorRejectedSentence`, nothing re-run. Elasticsearch asks `page_timeout` 5 m and pages every SQL
  shape. OpenSearch's cursor lifetime is a cluster setting (`plugins.sql.cursor.keep_alive`), and `fetch_size` is
  sent **only for a plain select** (`SearchBatch.IsPlainSelect`) — with it, other shapes go to the legacy engine,
  which mislabels types — so any other shape arrives whole, capped by `plugins.query.size_limit`, with no cursor
  and no Bearing notice. **The cut gives no signal (measured, `Measured_15…`):** with the limit at 5 over 30
  documents, SQL `LIMIT`/`DISTINCT`/`GROUP BY` and PPL `fields`/`stats` answer 5 rows with `total` = `size` = 5, no
  cursor and no warning — so nothing is said; never guess from a row count. ES|QL, PPL and cursor-less answers are returned **whole**, `Truncated` false: `MaxRows`
  is not applied (cutting would hide rows nothing can fetch again); a host that must cap trims and says so
  (`BearingHost.Capped`). Count is never offered.
- **`QueryResult.Notice`, not `Message`:** the server's warnings (headers and body, each cut at 500 chars) go to
  `Notice`; the App's status line appends every distinct notice of the run, in result order (a continuation
  page's is not shown; the `bearing` command repeats none). `Message` stays null on every result with columns — on a
  continuing result both hosts read it as the request-budget note. An ES|QL answer with `is_partial: true` leads
  its `Notice` with `PartialAnswerSentence`; SQL and PPL answers are not read for `is_partial`. An error leads
  with its language (`ES|QL: …`), its `line:col` is the position, and the server's root cause is appended when it
  differs from the reason.
- **Values:** integers → `long`, `unsigned_long` → `decimal`, floats → `double`, `boolean`, text-likes (`keyword`,
  `text`, `ip`, `version`, `date_nanos`, …) → text, `binary` → bytes, `date`/`datetime`/`timestamp` → `DateTime`
  Utc (`DateOnly` when the first page holds dates only), `time` → `TimeOnly`; objects, arrays, geo, unknown → JSON
  text. **The first page fixes each column** (a misfit there makes it text); a later misfit **fails that page
  naming the column**, its type and the kind of value, never the value (§1.9d) — nothing altered or dropped. `field_multi_value_leniency` is never sent (it would pick one
  value of an array silently); Elasticsearch SQL's own array refusal is shown as it is.
- **Metadata:** one database named after the host, one schema `""`. `GET` `_cat/indices` (open only), `_alias`,
  `_data_stream` (400/404/405 = none), then `/*/_mapping` (64 MB ceiling; a stream takes its newest backing
  index's mapping). Indices and streams are tables, an alias is a view (Show definition = comment lines naming
  its targets). Dot-named indices, hidden/system streams and hidden aliases are **never listed** (still queryable
  by name). Columns = mapped fields flattened to dotted names (multi-fields included), no keys. Ids derived as
  MySQL's (`StableId`). Sizes from `store.size` / `docs.count`; a missing size gives no entry.
- **Quoting (measured):** Elasticsearch quotes any name that is not a plain lower-case word, or is a keyword, **whole**
  (`"user.name"`, `"logs-2026.11.21"` — a bare dotted or dashed index is a parse error; never per segment).
  OpenSearch leaves a dotted path of plain words bare and backticks the rest.
- **Withheld, each answering "no":** grid editing, manual commit and transactions, count and offset paging, query
  plans, the Activity panel, session time zone, `:name` variables and `@set` (a run with parameters sends
  nothing), Format SQL, table DDL, attribute samples. The timeout is a client-side cancel of the request (§1.9a).
  SQL statements complete through a borrowed rule set (Postgres on Elasticsearch, MySQL on OpenSearch); nothing
  completes inside ES|QL or PPL (`CompletesAt`). Toggle-comment writes `--`, which ES|QL and PPL don't read
  (known limitation).

## §5.2 — SQLite (`Bearing.Persistence`)
Best-effort: persistence failures must never crash the app — surface in the status bar, swallow on
shutdown/save. Pruning uses `julianday()` + FTS `'rebuild'`.

## §5.3 — Connection lifecycle
Pooled by `ConnectionSessionManager` (`SessionKey`, §9.4a) and `SchemaBrowser`. Long-running reads take a
`SessionLease`. `MaxPoolSize` 10 per pool.

## §5.4 — Writes
Inline edits generate **parameterized** DML (`ResultEditModel`) run as one transaction via
`ExecuteWriteAsync`. SQL-preview inlining is display-only.

### §5.4a — Server expressions in cells (#149)
The one non-parameter value: `Core.Data.SqlExpression` (`now()`, `default`, …).
- `ResultEditModel.ExpressionFor` says yes only for a non-`string` column **and** after `Coerce` failed.
- Emit the dialect's canonical spelling, never the user's text. Per-engine tables via
  `ISqlDialect.TryEditExpression` (`EditExpression` / `TSqlEditExpression`) — no translation (`now()` on
  SQL Server stays refused). Pass the dialect everywhere; never default one.
- `DmlGenerator.BuildWhere` throws on an expression in a key predicate.
- Menu (`OfferedEditExpressions`) is a subset of what's accepted, indexing the same table; it calls
  `SetCell` like typing does. Text columns refuse it.
- Argument-less expressions only.

### §5.4b — UPDATE reads back the shown columns
- `returning` / `output inserted.…` lists named base columns, never `*`. Placement is
  `ISqlDialect.UpdateStatement`'s call.
- `ApplyReturnedRow` matches base name, then display name, keeps local values for anything not returned.
- SQL Server Msg 334 (trigger + `OUTPUT`): `SqlWithoutReturning` is filled whenever a clause was added; lose
  the refill, never the save.

## §5.5 — Temporal mappings (Npgsql 10, pinned by `TemporalMappingTests`)
| Postgres | .NET |
|---|---|
| `timestamptz` | `DateTime`, `Kind = Utc` |
| `timestamp` | `DateTime`, `Kind = Unspecified` |
| `timetz` | `DateTimeOffset` |
| `date` / `time` / `interval` | `DateOnly` / `TimeOnly` / `TimeSpan` |

- `CellFormat` branches on `Kind`: Utc → `CellFormat.Zone` with offset; Unspecified as-is, no offset (header
  badge `ColumnKinds.IsTimestampWithoutZone`, never in cell text).
- Keep the edit round trip (`TryParseDate`'s `utcColumn`).

## §5.5a — Session time zone rides the startup packet (#163)
- `ConnectionInfo.SessionTimeZone`: null = this machine's zone, `server` = send nothing, else a zone id.
  Legacy bag `Timezone` wins while null; the key is reserved.
- Sent as Npgsql `Timezone` (startup packet, never `SET`). Always IANA (`ZoneFor`, Windows ids mapped with
  the machine's region via `FromWindowsId`); a zone with no IANA name sends nothing.
- Part of `SamePool`/`SameConnection` (resolved zone), not `SameNetwork`. Named in the status tip where
  `IDbProvider.SupportsSessionTimeZone`; hidden on SQL Server.
- `SqlValue.Literal` writes `timestamptz` with `+00:00`. Live tests: pin offsets in timestamp literals.

## §5.6 — Size reads tolerate vanished relations
A null from `pg_total_relation_size` and similar is skipped, not reported as 0. WHEN calling size/location
functions, decide what absence means before `GetInt64` does.

## §5.7 — Streamed batches
- Every `RowBatch` carries `Columns` (positional). A row-returning stream always yields a tail batch, even
  when empty; a no-result statement yields nothing.
- `TableFormats.WriteCsvHeader` / `WriteCsvRow` are the one CSV rendering (byte-identical streamed vs
  batch, `StreamedCsvExportTests`). Fakes fill-and-yield like the real executors.
- xlsx has no streaming form.

## §5.8 — A batch is read to the end; only a lone statement pages
- `ResultSetBuilder.IsBatch(dialect, sql)` (the connection's own split, `@set` lines blanked) is the one
  definition, shared by the run and the result sets.
- A lone statement reads one page (server-side limit) and pages/counts/fetches against its own text.
- A batch runs once with `MaxRows = ResultFetchAllMaxRows` per set and is **never** pageable: its text is not one
  query (`declare @x …; select …` can't sit in a derived table), and re-reading set *N* would re-run the rest.
- `HasMore = Truncated` always — a cut set never looks complete. `IsCutShort` (more rows, can't page) shows
  "first N rows", names the limit in the status, and refuses export (single and workbook).
- A batch **streams** (`IQueryExecutor.ExecuteStreamingAsync` → `BatchEvent`s: `ResultStarted` / `ResultRows` /
  `ResultEnded` / `BatchCompleted`). The real executors observe the very loop `ExecuteAsync` runs (`BatchStream`,
  a bounded channel — don't write a second read loop); the interface default replays `ExecuteAsync` for fakes/demo.
- `BatchRunAssembler` publishes each set when it begins (`SetRunResults`, then `ExtendRunResults` — one run, one
  stamp) and settles `StatementIndex`/`ExecutedSql`/messages at completion. An error part-way keeps earlier sets
  (streaming only; `ExecuteAsync` still reports the failure alone). Esc/error mark the set being read cut short.
- A cancelled streamed batch is still logged (unsuccessful): what it ran before Esc reached the server (§1.6).
- **Except an engine whose reads continue by token** (`ProviderTraits.ReadsContinueByToken` — DynamoDB,
  Elasticsearch, OpenSearch): its batch takes the lone-statement path (first page per set, `MaxRows =
  ResultPageSize`), and each set pages from its own `Continuation` (`BuildOne`: `pageable || Continuation is not
  null`). Read to the ceiling, an unfiltered scan in a batch would run far past the one page §1.9c lets it run
  unasked, and a set the request budget cut would lose its token; a search engine's SQL set keeps its cursor the
  same way (§5.1c). `HasMore = Continuation is not null || Truncated`.
