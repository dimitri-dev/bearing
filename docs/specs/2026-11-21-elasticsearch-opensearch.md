# Spec — Elasticsearch and OpenSearch as query engines (query-only v1)

Date: 2026-11-21 · Scope: **new-feature, multi-file (Core seam + Sql + Data + Sessions + App + Cli tests)** ·
Spec mode: extended (rigor MAX) · Status: **approved 2026-11-21 (run until done)** · Branch:
`feat/add-elasticsearch-opensearch-support`

Decisions settled with the user (2026-11-21):

| # | Question | Decision |
|---|---|---|
| Q1 | Which query languages? | **SQL plus each engine's piped language**: Elasticsearch SQL + ES\|QL, OpenSearch SQL + PPL. |
| Q2 | Which sign-in methods? | **User + password, API key, and AWS profile (SigV4).** |
| Q3 | Is the `bearing` command in v1? | **Yes, for reads**, as on the other engines. |
| Q4 | May Bearing's signer hold AWS credentials in memory to sign OpenSearch requests (*Decision 2*)? | **Yes, for OpenSearch** (approved at the gate). Batch 4 runs. |

Everything else is a design choice made here. The ones most worth a second look are collected under
*Decisions for approval*; all are also in *Risks and assumptions*.

## Summary

Add two engines, **`elasticsearch`** ("Elasticsearch") and **`opensearch`** ("OpenSearch"), as the fifth and
sixth entries of the engine picker. They share one implementation in `src/Bearing.Data/Search/` that speaks
plain HTTP + JSON through `HttpClient` — **no client library and no new package**. They are two engines, not
one, because a provider is paired with one dialect by id (`ProviderTraits`), and the two differ in everything
a dialect owns: quoting (`"name"` vs `` `name` ``), endpoints, statement lists and sign-in.

A user can create a connection (host, port, TLS mode, optional SSH tunnel), browse indices, aliases and data
streams with their mapped fields, and run SQL, ES|QL (Elasticsearch) or PPL (OpenSearch) from the editor.
**The language is read from each statement's first word** — there is no per-tab mode. SQL results load a page
at a time from the server's own cursor, through the continuation paging DynamoDB introduced. The engines are
**query-only**: no grid editing, no transactions, no plans, no Activity panel.

## Decisions for approval

1. **Language per statement, from its first word** (no tab mode, nothing persisted). Rule in *Behaviour 4*.
2. **SigV4 makes Bearing code hold AWS credentials in memory.** §1.9c today says no Bearing code calls
   `GetCredentials`. To sign an OpenSearch request, Bearing's signer asks the AWS SDK for the resolved
   credentials **for each request** and computes the signature with the secret key. It never stores, logs or
   shows them. This is a change to the stated posture (§0.3) and needs your explicit yes. DynamoDB is unchanged.
   **Asked as its own question at the approval gate; batch 4 runs only on a yes.** On a no, AWS-profile
   sign-in is dropped from v1 (`SupportsAwsProfile` false on OpenSearch, no signer, no shared credential
   helper, DynamoDB untouched) and everything else stands.
3. **API key rides the password path.** No new credential kind: Elasticsearch gets an "Authentication" choice
   (User and password / API key); with API key the secret from the keychain or prompt is sent as
   `Authorization: ApiKey …`. The box is labelled "Password or API key"; the Prompt dialog still says password.
4. **TLS "Prefer" on an HTTP engine** = try HTTPS, and use plain HTTP only if the server does not speak TLS —
   what the existing advice text already says about Prefer. Never with an AWS profile off loopback.
5. **Cursor lifetime.** Elasticsearch cursors are asked to live 5 minutes (server default 45 s). OpenSearch
   fixes it by a cluster setting (default 1 minute) that a request cannot change. "Load more" after that fails
   with a sentence saying the cursor expired; nothing is re-run silently.
6. **SSH tunnel keeps the real host name** for these engines (new small Core seam), so TLS verification and
   SigV4 work through a bastion — the usual way to reach an OpenSearch domain in a VPC. Cuttable: without it,
   a tunnel still works for self-managed clusters, and AWS-profile sign-in through a tunnel is refused.
7. **Not verified by me, by construction:** anything against a real AWS domain or OpenSearch Serverless, and
   Elastic Cloud. SigV4 is unit-tested against AWS's published signature vectors only. You test those.

## Behaviour

1. **Connection editor.** Engine picker gains "Elasticsearch" and "OpenSearch" (after DynamoDB). Fields:

   | Field | Elasticsearch | OpenSearch |
   |---|---|---|
   | Host (required), Port (default 9200) | yes | yes |
   | User (optional) | yes | yes — with the AWS profile credential it is the **profile name** (hint says so; blank = the SDK's default chain) |
   | Password box | labelled "Password or API key" | "Password" |
   | Authentication (`Options["Authentication"]`) | "User and password" (default) / "API key" | — |
   | AWS region (`Options["Region"]`) | — | optional, suggestions from `AwsRegions`; blank = read from an `amazonaws.com` / `on.aws` host name |
   | Credential kinds | Stored password, Prompt | Stored password, Prompt, AWS profile |

   TLS mode, SSH tunnel, Read-only, Confirm writes, statement timeout, environment, folder and External access
   are shown. Hidden (existing flags): Manual commit, Session time zone, Database. A blank user and blank
   password send no `Authorization` header (a cluster with security off).
2. **Transport.** `Disable` → `http://`. `Require` → `https://`, any certificate accepted. `Verify CA` →
   chain must be trusted, host name not checked. `Verify Full` → normal HTTPS validation. `Prefer` → HTTPS,
   falling back to HTTP only when the TLS handshake itself fails (decided once per pool). Redirects are never
   followed. The OS trust store is the only trust source. With the **AWS profile** credential, plain HTTP is
   refused at connect unless the host is loopback.
3. **Sign-in.**
   - *User and password*: HTTP Basic.
   - *API key* (Elasticsearch): `Authorization: ApiKey <secret>`, the encoded form Kibana shows; User ignored.
   - *AWS profile* (OpenSearch): SigV4, service `aoss` when the host contains `.aoss.` or `.aoss-fips.`, else
     `es`. Region from the field, else from the host name; neither → refused at connect with a sentence.
     Credentials come from the AWS SDK (named profile, or its default chain when blank) and are re-asked per
     request, so a refreshed SSO or assumed-role session is picked up. A sign-in failure is classified
     `Authentication` and its message is built from the profile name alone (`aws sso login --profile <name>`).
     The dialog's AWS-profile hint becomes per engine (`ProviderTraits.AwsProfileNote`): DynamoDB keeps its
     sentence; OpenSearch says that the AWS SDK resolves the profile and that Bearing signs each request with
     the resolved credentials in memory, never storing, logging or showing them. Today's one shared sentence
     ("Bearing's own code never reads … an AWS secret") would be false here.
   - Which method is used is decided by the connection's credential kind and Authentication field, never by
     whether a password happens to be present (the dialog's Test sends the password box whatever the kind).
   - HTTP 401 → `Authentication`. HTTP 403 is a privilege, not a sign-in failure.
4. **Language of a statement.** Statements are separated by `;`. Each is sent on its own to one endpoint:

   | Engine | Sent as SQL when the first word is | Sent as the piped language when it is | Otherwise |
   |---|---|---|---|
   | Elasticsearch | `SELECT`, `DESCRIBE`, `DESC`, `EXPLAIN`, `SYS`, `DEBUG`, `SHOW` (not `SHOW INFO`) → `/_sql` | `FROM`, `ROW`, `TS`, `PROMQL`, `SHOW INFO` → `/_query` (ES\|QL) | piped if it has a top-level `\|`, else SQL |
   | OpenSearch | `SELECT`, `DELETE`, `SHOW TABLES`, `DESCRIBE TABLES` → `/_plugins/_sql` | `SEARCH`, `SOURCE`, `INDEX`, `DESCRIBE`, `SHOW`, `EXPLAIN`, `MULTISEARCH`, `UNION`, `MAKERESULTS`, `GRAPHLOOKUP`, a leading `\|` → `/_plugins/_ppl` (PPL) | piped if it has a top-level `\|`, else SQL |

   A server error is shown with the language it was sent as ("ES|QL: line 1:15: Unknown column …"), and its
   `line:column` becomes the error position when present. A trailing `;` is never sent.
5. **Results and paging.**
   - **SQL, Elasticsearch**: page size via `fetch_size`; a `cursor` in the answer means more rows. Works for
     any shape the server pages (aggregations included).
   - **SQL, OpenSearch**: `fetch_size` too. The server pages plain selects only; any other shape comes back
     whole, capped by the cluster's `plugins.query.size_limit` (default 10,000).
   - **ES|QL and PPL**: the server has no paging. Bearing shows **every row it returned** (ES|QL: 1,000
     without a `LIMIT`, never more than 10,000): the page size is not applied, the result is not marked
     truncated, and it stays exportable. The same holds for an OpenSearch SQL answer that came back without a
     cursor. When the server sent a warning about the result ("No limit defined, adding default limit of
     [1000]") the App's status line repeats it (`Done · 88 ms — No limit defined, …`), carried in a new
     `QueryResult.Notice` — not in `Message`, which on a result that can continue already means "the request
     budget cut this page short" in both hosts. The `bearing` command does not repeat notices.
   - "Load more" / "Fetch all" send only the cursor — the statement is not re-run. An expired or unknown
     cursor says so and leaves loaded rows untouched. Count is not offered. Cursors of results that are
     closed without being read to the end are left to expire on the server.
   - Values: integers → integer, floats → floating point, `unsigned_long` → decimal, `boolean` → boolean,
     text-like types (`keyword`, `text`, `ip`, `version`, …) → text, `binary` → bytes, date/time types →
     date-time in UTC (shown in the display zone like `timestamptz`), `date_nanos` → text (kept exact),
     objects, arrays, geo types and anything unknown → JSON text. A column's type is fixed by the declared
     type and the first page; a later value that does not fit **fails that page with a sentence naming the
     column** — nothing is altered or dropped. Elasticsearch SQL's own refusal of array fields is shown as it
     is (Bearing does not turn on first-value leniency).
6. **Schema tree.** Server → one database node named after the host → relations: indices (tables), aliases
   (views; "Show definition" lists the indices behind the alias), data streams (tables). Hidden and system
   indices (dot-prefixed, data-stream backing indices) are not listed; they can still be queried by name.
   Columns are the mapped fields, flattened with dotted names (`user.name`, multi-fields such as
   `title.keyword`), each with its mapping type. Sizes show store bytes and document count from
   `_cat/indices`. "Select top 100 rows" produces `select * from "idx" limit 100` (backticks on OpenSearch).
   No keys, constraints, indexes, routines, roles or long-tail kinds (existing empty-group behaviour).
7. **Write guard and read-only.** No released SQL, ES|QL or PPL statement writes, except OpenSearch 2.x's
   opt-in `DELETE`. The guard therefore flags `DELETE`, an ES|QL `INTO` command, and — conservatively — any
   statement whose first word is not a known read. Read-only connections refuse those through the existing
   `WriteRefusal`; "Confirm writes" confirms them. Read-only is client-side only
   (`EnforcesReadOnlyOnServer` false); the note names a role, API key or IAM policy without write privileges
   as the boundary.
8. **Withheld, each answered "no" rather than approximated:** grid editing (lock chip: "Read-only — editing
   isn't available on Elasticsearch connections yet." / "…OpenSearch…"), manual commit and transactions,
   count, query plans and `bearing explain`, Activity panel, session time zone, `:name` variables and `@set`,
   Format SQL, table DDL. Completion offers index and field names in SQL statements and **shows nothing inside
   an ES|QL or PPL statement**. Toggle-comment still writes `--`, which ES|QL and PPL do not read as a comment
   (known limitation).
9. **Statement timeout.** A client-side cancel of the HTTP request (the SQL Server arrangement, §1.9a); the
   error names the connection's timeout and classifies `Canceled`.
10. **SSH tunnel.** Supported. The request is dialled through the tunnel's loopback port but still names the
    real host, so the certificate, the `Host` header and the SigV4 signature are those of the real server.
11. **`bearing` command (exposed connections).** `connections`, `tables`, `describe`, `query`, `query --out`
    work under the existing forced settings (§1.11). Allow-list: Elasticsearch `SELECT`, `SHOW`, `DESCRIBE`,
    `DESC`, `FROM`, `ROW`, `TS`; OpenSearch `SELECT`, `SHOW`, `DESCRIBE`, `SEARCH`, `SOURCE`, `INDEX`. Refused
    on top of that, by name: an ES|QL statement using `COMPLETION`, `RERANK` (inference calls) or `INTO`; a
    PPL statement using `ml`, `kmeans`, `ad` or `rest`; any statement naming an index that starts with `.`.
    `explain` is refused. `--out` follows the cursor to the end; `--max-rows` trims.

## Requirements

- The system shall register `elasticsearch` and `opensearch` after `dynamodb`, each paired with its own traits and dialect.
- The system shall send each `;`-separated statement to exactly one endpoint chosen by the rule in Behaviour 4.
- When a statement is sent, the system shall omit a trailing `;` from the request body.
- When an Elasticsearch or OpenSearch SQL answer carries a `cursor`, the system shall return a result with a continuation and `Truncated` true.
- When a continuation is executed, the system shall send only the cursor to the endpoint that issued it.
- If the server rejects a cursor, then the system shall return an error saying the cursor expired or is unknown and shall not re-run the statement.
- If a later page holds a value that does not fit its column's type, then the system shall fail that page with an error naming the column.
- The system shall keep a continuation in memory only and shall not log, persist or display it.
- When the connection's authentication is "API key", the system shall send the secret as `Authorization: ApiKey <secret>` and shall not send the user name.
- When the credential kind is AWS profile, the system shall sign every request with SigV4 using service `aoss` for a host containing `.aoss.` or `.aoss-fips.` and `es` otherwise.
- If the credential kind is AWS profile and the scheme is `http` for a non-loopback host, then the system shall refuse the connect with a sentence.
- If the credential kind is AWS profile and no region is set or readable from the host name, then the system shall refuse the connect with a sentence naming the AWS region field.
- The system shall not write an AWS access key, secret key, session token, password, API key or `Authorization` header value to a log, a message or disk.
- The system shall not follow HTTP redirects.
- While the TLS mode is `Verify Full`, the system shall reject a certificate that is untrusted or not issued for the connection's host.
- While the TLS mode is `Prefer`, the system shall use plain HTTP only after a TLS handshake failure.
- When a connection has an SSH tunnel, the system shall dial the tunnel's loopback port while naming the connection's real host in the request URI.
- The system shall report grid editing, manual commit, count, explain, server activity, session time zone and script variables as unavailable on both engines.
- When the caret is inside an ES|QL or PPL statement, the system shall return no completion suggestions.
- The system shall flag as risky any statement whose first word is not a known read for its engine, an OpenSearch `DELETE`, and an ES|QL statement containing an `INTO` command.
- When the `bearing` command receives a statement that uses `COMPLETION`, `RERANK`, `INTO`, `ml`, `kmeans`, `ad` or `rest` as a pipe command, the system shall refuse it and log the refusal.
- When the `bearing` command receives a statement naming an index that begins with `.`, the system shall refuse it and log the refusal.
- The system shall list open, non-hidden indices, aliases and data streams as relations and their mapped fields as columns with dotted names.
- When an ES|QL, PPL or cursor-less SQL answer arrives, the system shall return every row the server sent with `Truncated` false.
- When the server sends a warning with a result that has rows, the system shall carry it in `QueryResult.Notice` and shall not set `Message`.
- The system shall choose the sign-in method from the connection's credential kind and Authentication field and shall not infer it from the presence of a password.
- The system shall redact string and number literals of each statement by the lexical rules of that statement's language.

## Success criteria

Live criteria (SC12–SC16) need both containers (`./build/test-db-search.sh`); a skip is not a pass (§4.2).
Filters are run as `dotnet test --filter "FullyQualifiedName~<class>"`.

| ID | Outcome | Verification | Channel | Observable |
|---|---|---|---|---|
| SC1 | Both engines are registered after `dynamodb`, named "Elasticsearch" / "OpenSearch", each with its own traits arm and dialect; flags answer as in *Architecture* | `ProviderRegistryTests`, `ProviderTraitsTests`, `SearchProviderTests` | test-run | Classes pass; registry ids equal `postgres, sqlserver, mysql, dynamodb, elasticsearch, opensearch` |
| SC2 | Language routing follows Behaviour 4 for every row of its table, including `SHOW INFO`, `DESCRIBE TABLES`, an unknown first word with and without a top-level `\|`, and words inside strings and comments; the `Sql` and `Data` scanners agree on a shared corpus | `SearchScanTests`, `SearchBatchTests`, `SearchScanParityTests` | test-run | Theories pass; parity test reports 0 disagreements |
| SC3 | Statement splitting, string detection, literal redaction and `EndsWithLineComment` read each language's own forms: `'…'`, `"…"`, `"""…"""`, backticks, `--`, `//`, `#`, `/* */` (nested on Elasticsearch SQL only), backslash escapes where the language has them; an ES\|QL `"x"` is redacted as a string, an Elasticsearch SQL `"x"` is kept as a name | `SearchScanTests`, `SearchBatchTests` | test-run | Theories pass |
| SC4 | Dialects: quoting (`"x"` / `` `x` ``, dotted field paths left bare), `SelectTopRows`, paging and count hooks null, `FormatRefusal` set, `SupportsExplainPlan` false, variables empty, write guard per Behaviour 7, CLI allow-list and denials per Behaviour 11 | `ElasticsearchDialectTests`, `OpenSearchDialectTests` | test-run | Theories pass |
| SC5 | No completion inside a piped statement; index and field names complete inside a SQL statement on both engines | `SearchCompletionTests` | test-run | Tests pass |
| SC6 | Transport: scheme and certificate handling per TLS mode; `Prefer` falls back only on a handshake failure; redirects not followed; Basic and ApiKey headers exact; blank user + password sends no `Authorization`; AWS profile over non-loopback `http` refused; the method follows the credential kind and Authentication field even when a password is supplied with the AWS profile kind | `SearchConnectionFactoryTests` | test-run | Theories pass |
| SC7 | SigV4: signatures equal AWS's published vectors; `x-amz-content-sha256` and (when present) `x-amz-security-token` are sent and signed; service and region resolution per Behaviour 3; no key, secret or token appears in any error text | `AwsSigV4Tests`, `SearchConnectionFactoryTests`, `SearchErrorsTests` | test-run | Theories pass |
| SC8 | DynamoDB sign-in behaves as before after the credential lookup is shared | `DynamoDbConnectionFactoryTests`, `DynamoDbProviderTests` | test-run | Existing tests pass unchanged |
| SC9 | Protocol: request bodies and response parsing for the four endpoints; type mapping per Behaviour 5; a continuation round-trips and anything else is refused on decode; a later-page misfit fails the page naming the column | `SearchProtocolTests`, `SearchValuesTests`, `SearchContinuationTests` | test-run | Theories pass |
| SC10 | Executor against a fake API: a batch runs statement by statement with `StatementIndex`; cursor → continuation; continuation sends only the cursor; a rejected cursor gives the expired sentence and 0 re-runs of the statement; a server warning reaches `Notice` and never `Message`; an ES\|QL / PPL / cursor-less SQL answer larger than `MaxRows` is returned whole with `Truncated` false; a 1 s timeout cancels with the timeout sentence | `SearchQueryExecutorTests` | test-run | Tests pass; fake records 0 statement re-sends on continuation |
| SC11 | Metadata against a fake API: indices, aliases and data streams become relations; hidden indices are left out; mappings flatten to dotted columns including multi-fields and nested; sizes tolerate missing fields | `SearchMetadataReaderTests`, `SearchMappingsTests` | test-run | Tests pass |
| SC12 | Live, both engines: connect with security off; snapshot lists a fixture index with its fields, an alias as a view and a data stream; sizes return | `SearchLiveTests` (schema) | test-run | Pass with 0 skipped while both containers are up |
| SC13 | Live, both engines: SQL over 250 documents at page size 100 returns 100 + continuation, then 100, then 50 and no continuation; 250 distinct ids | `SearchLiveTests` (paging) | test-run | Pass with 0 skipped |
| SC14 | Live: ES\|QL `FROM … \| LIMIT` and PPL `source=… \| head` return typed rows; ES\|QL without `LIMIT` carries the default-limit warning; a cursor closed on the server gives the expired sentence | `SearchLiveTests` (piped, expiry) | test-run | Pass with 0 skipped |
| SC15 | Live: value types round-trip (long, double, boolean, keyword, date, ip, object, array via ES\|QL/PPL); an OpenSearch aggregate SQL statement sent with `fetch_size` returns its rows | `SearchLiveTests` (values, shapes) | test-run | Pass with 0 skipped |
| SC16 | Live with security on (Elasticsearch): Basic sign-in works, a wrong password classifies `Authentication`, an API key created in the test signs in | `SearchLiveTests` (auth) | test-run | Pass with 0 skipped |
| SC17 | A tunnelled connection is built with the real host and the tunnel's loopback end; other engines still receive the rewritten host | `SshTunnelTests` | test-run | New cases pass; existing cases unchanged |
| SC18 | Editor: each engine shows its fields from Behaviour 1, offers its credential kinds, hides Manual commit / Session time zone, shows TLS and SSH; the password label follows the provider's label; the AWS-profile hint shows OpenSearch's sentence on OpenSearch and DynamoDB's unchanged on DynamoDB | `Ui/ConnectionEditorTests`, `ConnectionFieldModelTests` | test-run | New cases pass |
| SC19 | App: results on both engines carry the editing lock sentence; a SQL result with a continuation pages through `ExecuteContinuationAsync`; manual commit is never effective; a result's `Notice` appears in the run's status text | `SearchResultTests` | test-run | Tests pass |
| SC20 | `bearing`: an exposed connection of each engine is listed; allowed statements run; writes, denied pipe commands and dot-index names are refused and logged; `--out` follows the cursor; `--max-rows` trims an ES\|QL result; `explain` is refused | `SearchExposureTests`, `ConnectionListingTests` | test-run | Tests pass; fake executor records 0 calls for refused statements |
| SC21 | Nothing regresses and layering holds | `dotnet build`; `dotnet test`; csproj grep | build-output | Build exit 0; test exit 0; 0 `Reference` items in `Bearing.Core.csproj`; no new `PackageReference` anywhere |
| SC22 | Docs and rules name both engines | grep `Elasticsearch` and `OpenSearch` in `README.md`, `CLAUDE.md`, `AGENTS.md`, `docs/CLI.md`, `.claude/rules/{architecture,data-layer,security,testing}.md` | script-output | Every listed file has ≥ 1 match for each |

UI criteria (SC18, SC19) are headless assertions; the dialog and tree still need your eyeball QA (§4.3).

## Architecture and design

### Layering (unchanged, §2.2)

```
Core (TunnelEndpoint)  ←  Sql (ElasticsearchDialect, OpenSearchDialect, SearchScan)     Data (Search/*)
                               ↖                                                      ↗
                                Sessions (two ProviderTraits arms, tunnel hand-over)
                                         ↑
                                App (password label)        Cli (no source change)
```

### Core and Sql seams (additive, default members)

| Change | Why |
|---|---|
| `Core.Data.TunnelEndpoint(string Host, int Port)` + `IDbProvider.CreateConnectionFactory(ConnectionInfo info, string? password, TunnelEndpoint tunnel)` — default member that rewrites `Host`/`Port` and calls the two-argument overload | Today the tunnel's loopback end replaces the host before the provider sees it. An HTTP engine needs both: the loopback end to dial, the real name for TLS, `Host` and SigV4. Every existing provider and fake keeps today's behaviour through the default. |
| `ISqlDialect.CompletesAt(string sql, int caret)` — default `true` | Lets a dialect say "no popup here" for a statement in a language the grammar does not read. |
| `QueryResult.Notice` (`string?`, init) | What the server said about a result that has rows. `Message` cannot carry it: it is shown only for results without columns, and on a continuing result both hosts read it as the request-budget notice. |

No new `CredentialKind`, no `ConnectionInfo` field, no package.

### `Bearing.Sql`

- **`SearchScan`** — one small hand-written scanner for the four languages: tokens (word, quoted name,
  string, comment, punctuation), statement split on top-level `;`, top-level `|` segments, first word(s), and
  the language decision of Behaviour 4. Parameterised by engine; the lexical rules switch per statement once
  its language is known.
- **`ElasticsearchDialect`** (`Id` = `elasticsearch`): `"…"` quoting; `PgParseRules` for SQL completion;
  `SqlLiteralStyle.Postgres`.
- **`OpenSearchDialect`** (`Id` = `opensearch`): backtick quoting; `MySqlParseRules`; `SqlLiteralStyle.MySql`.
- Both: `TryAppendPage` / `Wrap` / `CountWrap` null; `ScanTargets` null; `SupportsExplainPlan` false;
  `FormatRefusal` set; variables and directives empty; `TransactionControl` null; `DescribeStatements`,
  `SplitStatements`, `InStringLiteral`, `RedactLiterals`, `CodeEnd` and `EndsWithLineComment` from
  `SearchScan`, each reading the statement's own language (`"x"` is a string in ES|QL and a name in
  Elasticsearch SQL; `//` and `#` end a line where the language says so — the Postgres default would let a
  run-all append its `;` inside such a comment); `ReadsLikePostgresForCounting` false; `CompletesAt` false in a
  piped statement; `ExternalReadVerbs` and `ExternalDeniedStatement` per Behaviour 11. A dotted path of plain
  words is not quoted. Pattern to mirror: `src/Bearing.Sql/PartiQlDialect.cs`.

### `Bearing.Data/Search`

| File | Holds |
|---|---|
| `SearchProvider.cs` | One class, two ids (`SearchEngine.Elasticsearch` / `.OpenSearch`); fields and flags |
| `ISearchApi.cs` | The seam: one method sending method + path + JSON body, returning status, body and `Warning` headers; `HttpSearchApi` over `HttpClient` (pattern: `IDynamoDbApi`) |
| `SearchConnectionFactory.cs` | Scheme and TLS per mode, `Prefer` probe, redirects off, tunnel dialling (`SocketsHttpHandler.ConnectCallback`), auth header or signer, `TestConnectionAsync` (`GET /`, falling back to `_cat/indices` when the root is not served) |
| `SearchErrors.cs` | Classification, sentences, scrubbing |
| `AwsSigV4.cs` | The signer (pure: request parts + credentials + clock → headers) |
| `Aws/AwsProfileCredentials.cs` | Profile → SDK credentials with the two typed failures, extracted from `DynamoDbConnectionFactory.Build` and used by both |
| `SearchBatch.cs` | `Data`'s own statement split and language decision (`Data` may not use `Sql`; parity pinned by test — the `PartiQlBatch` arrangement) |
| `SearchProtocol.cs` | Request and response shapes for `/_sql`, `/_query`, `/_plugins/_sql`, `/_plugins/_ppl` → one neutral page |
| `SearchValues.cs` | Declared type → `ColumnDescriptor`; JSON value → cell |
| `SearchContinuation.cs` | Opaque token: endpoint, cursor, column layout (pattern: `DynamoDbContinuation`) |
| `SearchQueryExecutor.cs` | Batch loop, first page, continuation, stream, timeout; offset paging, count and generated writes answer "not available" |
| `SearchMappings.cs`, `SearchMetadataReader.cs` | `_cat/indices`, `_alias`, `_data_stream`, `_mapping` → snapshot, sizes, alias definition; ids derived as MySQL's (`StableId`) |

Two new folders, both deliberate: `Data/Search/` follows the one-folder-per-engine convention (`Postgres/`,
`MySql/`, `DynamoDb/`) for the implementation the two engines share; `Data/Aws/` is new and holds only what
DynamoDB and OpenSearch both use. `AwsConfigProfiles` and `AwsRegions` stay in `Data/DynamoDb/` (no churn).

Provider flags (both): `EnforcesReadOnlyOnServer` false, `SupportsSessionTimeZone` false,
`SupportsIntegratedAuth` false, `SupportsEntraToken` false, `SupportsPasswordCredentials` true,
`SupportsManualCommit` false, `SupportsTlsModes` true, `SupportsSshTunnel` true, `SupportsServerActivity`
false. `SupportsAwsProfile`: false on Elasticsearch, true on OpenSearch.

### `Bearing.Sessions`

- `ProviderTraits.Elasticsearch` / `.OpenSearch` arms and `For` branches: `ReadsContinueByToken` true,
  `GridEditUnavailable` set, `OffersTableDdl` false, `WriteBoundary` set, `SamplesAttributes` false,
  `NetworkOptions` empty. Registered in the same batch as the provider (lesson 2026-11-19).
- `ConnectionFactoryBuilder.BuildAsync` / `TestAsync` hand the provider the unrouted record plus the tunnel's
  loopback end through the new overload.

### `Bearing.App` and `Bearing.Cli`

- App: the password label shows the provider's `Password` field label (`ConnectionFieldModel.PasswordLabel`);
  the AWS-profile hint's text comes from `ProviderTraits.AwsProfileNote`; `ResultSetBuilder`'s status line
  appends a result's `Notice`. Everything else is driven by existing flags and traits.
- Cli: no source change expected — the allow-list, denials, continuation following and caps are dialect- and
  trait-driven. Tests and `docs/CLI.md` only.

## Security and compliance impact

**`security_impact`: `new-auth-path`.**

- Two new ways to authenticate: an API key header, and SigV4 signing done by Bearing code with credentials
  the AWS SDK resolved (*Decision 2*). Neither writes anything new to disk; the API key and password use
  `ISecretStore` exactly as passwords do (§1.1).
- New outbound HTTP surface. Mitigations: no redirects; TLS per the typed mode; `Require` is never described
  as verified (§1.4); AWS-profile requests never over plain HTTP off loopback; error text scrubbed of
  `Authorization` values and AWS key ids.
- Read-only is client-side only (§1.9a's arrangement); copy must say so.
- `bearing` gains two unattended read paths, held to an allow-list plus the named denials. Accident
  prevention, not a boundary (§1.11b).
- The continuation is memory-only (§1.3).
- §1.9c gains an OpenSearch counterpart stating precisely what Bearing code holds and for how long.

## Constitution Check

| Rule | How the design satisfies it |
|---|---|
| §0.1 / §2.1 | Core gains one record and one default interface member; no reference added |
| §0.2 / §4.3 | UI claims are headless assertions; eyeball QA is asked for |
| §0.3 / §1.1 | Secrets only through `ISecretStore`; nothing logged; the SigV4 posture change is *Decision 2*, awaiting explicit approval |
| §0.4 / §9.1 | No growth of `MainWindow`, `ResultView` or `ShellViewModel`; the dialog change is one label binding |
| §0.5 / §2.2 | All HTTP and SQL text lives in `Data` and `Sql`; `Sql` and `Data` stay unlinked (two scanners, parity-tested) |
| §1.2 | Guard widens only: unknown first words are risky |
| §1.4 | Typed `TlsMode` decides the scheme and validation; no hardcoded credentials; validation disabled only in the mode that says so |
| §1.9a | Both engines answer `EnforcesReadOnlyOnServer` false |
| §1.11 | Allow-list per engine; `ForExternalHost` untouched |
| §2.6 | Whether a statement may run stays in `Sql` dialect + `Sessions`; hosts only ask |
| §4.1 / §4.2 | Hand-rolled fake `ISearchApi`; live tests are skippable theories over both engines |
| §5.8 | Batches take the token-engine path (`ReadsContinueByToken`) |

## Change manifest

**Core**
- `src/Bearing.Core/Data/TunnelEndpoint.cs` — create
- `src/Bearing.Core/Data/IDbProvider.cs` — modify — three-argument `CreateConnectionFactory` default member
- `src/Bearing.Core/Data/QueryResult.cs` — modify — `Notice`

**Sql**
- `src/Bearing.Sql/SearchScan.cs` — create
- `src/Bearing.Sql/ElasticsearchDialect.cs` — create
- `src/Bearing.Sql/OpenSearchDialect.cs` — create
- `src/Bearing.Sql/ISqlDialect.cs` — modify — `CompletesAt`
- `src/Bearing.Sql/CompletionEngine.cs` — modify — ask `CompletesAt` first

**Data**
- `src/Bearing.Data/Search/SearchProvider.cs`, `ISearchApi.cs`, `SearchConnectionFactory.cs`,
  `SearchErrors.cs`, `AwsSigV4.cs`, `SearchBatch.cs`, `SearchProtocol.cs`, `SearchValues.cs`,
  `SearchContinuation.cs`, `SearchQueryExecutor.cs`, `SearchMappings.cs`, `SearchMetadataReader.cs` — create
- `src/Bearing.Data/Aws/AwsProfileCredentials.cs` — create
- `src/Bearing.Data/DynamoDb/DynamoDbConnectionFactory.cs` — modify — use the shared credential lookup
- `src/Bearing.Data/ProviderRegistry.cs` — modify — register fifth and sixth
- `src/Bearing.Data/Bearing.Data.csproj` — modify — header comment only (mechanical)

**Sessions**
- `src/Bearing.Sessions/ProviderTraits.cs` — modify — two arms, `AwsProfileNote`
- `src/Bearing.Sessions/ConnectionFactoryBuilder.cs` — modify — tunnel hand-over

**App**
- `src/Bearing.App/Connections/ConnectionFieldModel.cs` — modify — `PasswordLabel`
- `src/Bearing.App/Views/ConnectionDialog.axaml`, `src/Bearing.App/Views/ConnectionDialog.axaml.cs` — modify —
  password label from the model; AWS-profile hint text from the traits
- `src/Bearing.App/Results/ResultSetBuilder.cs` — modify — status line appends a result's `Notice`

**Build, CI, docs**
- `build/test-db-search.sh` — create — `bearing-elasticsearch-test` (19200) and `bearing-opensearch-test`
  (19201), plus `bearing-elasticsearch-secure-test` (19202, security on) for SC16
- `.github/workflows/ci.yml` — modify — provision step
- `tests/Bearing.Data.Tests/Bearing.Data.Tests.csproj` — modify — link `tests/Shared/SearchTestServer.cs`
  (mechanical)
- `README.md`, `CLAUDE.md`, `AGENTS.md`, `docs/CLI.md` — modify
- `.claude/rules/architecture.md`, `.claude/rules/data-layer.md` (new §5.1c), `.claude/rules/security.md`
  (new §1.9d), `.claude/rules/testing.md` — modify

No new dependency: `HttpClient` and `System.Text.Json` are in the framework; `AWSSDK.Core` is already
referenced and pinned.

## Test manifest

| Path | Action | Covers |
|---|---|---|
| `tests/Bearing.Sql.Tests/SearchScanTests.cs` | create | SC2, SC3 |
| `tests/Bearing.Sql.Tests/ElasticsearchDialectTests.cs` | create | SC4 |
| `tests/Bearing.Sql.Tests/OpenSearchDialectTests.cs` | create | SC4 |
| `tests/Bearing.Sql.Tests/SearchCompletionTests.cs` | create | SC5 |
| `tests/Bearing.Data.Tests/SearchBatchTests.cs` | create | SC2, SC3 |
| `tests/Bearing.Data.Tests/SearchConnectionFactoryTests.cs` | create | SC6, SC7 |
| `tests/Bearing.Data.Tests/AwsSigV4Tests.cs` | create | SC7 |
| `tests/Bearing.Data.Tests/SearchErrorsTests.cs` | create | SC7 |
| `tests/Bearing.Data.Tests/DynamoDbConnectionFactoryTests.cs` | modify | SC8 (only if the extraction moves a tested member) |
| `tests/Bearing.Data.Tests/SearchProtocolTests.cs` | create | SC9 |
| `tests/Bearing.Data.Tests/SearchValuesTests.cs` | create | SC9 |
| `tests/Bearing.Data.Tests/SearchContinuationTests.cs` | create | SC9 |
| `tests/Bearing.Data.Tests/SearchQueryExecutorTests.cs` | create | SC10 |
| `tests/Bearing.Data.Tests/SearchMetadataReaderTests.cs` | create | SC11 |
| `tests/Bearing.Data.Tests/SearchMappingsTests.cs` | create | SC11 |
| `tests/Bearing.Data.Tests/SearchProviderTests.cs` | create | SC1 |
| `tests/Bearing.Data.Tests/ProviderRegistryTests.cs` | modify | SC1 |
| `tests/Bearing.Data.Tests/ProviderClassificationTests.cs` | modify | SC6, SC7 |
| `tests/Bearing.Data.Tests/SearchLiveTests.cs` | create | SC12–SC16 |
| `tests/Shared/SearchTestServer.cs` | create | SC12–SC16 (skip plumbing, `BEARING_TEST_{ELASTICSEARCH,OPENSEARCH}_*`) |
| `tests/Bearing.App.Tests/ProviderTraitsTests.cs` | modify | SC1 |
| `tests/Bearing.App.Tests/SshTunnelTests.cs` | modify | SC17 |
| `tests/Bearing.Data.Tests/SearchScanParityTests.cs` | create | SC2 (in `Data.Tests`: it sees `Data`'s internals and references `Sql`, as `PartiQlBatchTests` does) |
| `tests/Bearing.App.Tests/Ui/ConnectionEditorTests.cs` | modify | SC18 |
| `tests/Bearing.App.Tests/ConnectionFieldModelTests.cs` | modify | SC18 |
| `tests/Bearing.App.Tests/SearchResultTests.cs` | create | SC19 |
| `tests/Bearing.Cli.Tests/SearchExposureTests.cs` | create | SC20 |
| `tests/Bearing.Cli.Tests/ConnectionListingTests.cs` | modify | SC20 |

## Implementation batches

1. **Seams, no behaviour change.** `TunnelEndpoint`, the `CreateConnectionFactory` overload,
   `ConnectionFactoryBuilder` hand-over, `ISqlDialect.CompletesAt`, `CompletionEngine`, `QueryResult.Notice`.
   (SC17)
2. **`SearchScan` and the two dialects.** (SC2–SC5, Sql half)
3. **Transport.** `ISearchApi`, `SearchConnectionFactory` (TLS, Basic, API key, tunnel dialling),
   `SearchErrors`. (SC6)
4. **SigV4 — only if Decision 2 is approved.** `AwsSigV4`, `AwsProfileCredentials`, DynamoDB factory uses it,
   factory wiring. (SC7, SC8)
5. **Protocol.** `SearchBatch`, `SearchProtocol`, `SearchValues`, `SearchContinuation`, the parity test.
   (SC2, SC3, SC9)
6. **Executor.** `SearchQueryExecutor`. (SC10)
7. **Metadata.** `SearchMappings`, `SearchMetadataReader`. (SC11)
8. **Provider, registry, traits** — one batch, so a provider never exists without its traits arm. (SC1)
9. **Live plumbing.** Script, `SearchTestServer`, `SearchLiveTests`, CI step; containers run before reporting.
   (SC12–SC16)
10. **App.** Password label, AWS-profile hint per engine, `Notice` in the status line, editor and result
    tests. (SC18, SC19)
11. **`bearing` command.** Exposure tests, `docs/CLI.md`. (SC20)
12. **Docs and rules**, then the full verification pass. (SC21, SC22)

Depends: 2, 3 on 1; 4 on 3; 5 on 1 and 2; 6 on 3 and 5; 7 on 3; 8 on 2, 4, 6, 7; 9 on 8; 10 on 8; 11 on 8; 12 on
9, 10, 11.

## Risks and assumptions

**Verified against the code**
- [VERIFIED:src/Bearing.Sessions/ProviderTraits.cs] A provider and its dialect are paired by one id, so two
  quoting styles need two ids. `For` falls back to Postgres for an unknown id — hence batch 8.
- [VERIFIED:src/Bearing.App/Results/ContinuationPaging.cs] A result carrying `QueryResult.Continuation`
  already loads more and fetches all through `ExecuteContinuationAsync`; `CanCount` is false for token engines
  (`src/Bearing.App/ViewModels/ResultSetViewModel.cs`).
- [VERIFIED:src/Bearing.Cli/Tools/BearingHost.cs] The command follows continuations
  (`FollowContinuationsAsync`) and trims to `--max-rows` (`Capped`) for any engine whose traits say
  `ReadsContinueByToken`.
- [VERIFIED:src/Bearing.Sessions/SshTunnels.cs] `Route` replaces `Host` and `Port` with the loopback end, so
  the real name is lost before the provider — hence the seam.
- [COVERAGE:2] `CreateConnectionFactory` has two production callers, both in
  `src/Bearing.Sessions/ConnectionFactoryBuilder.cs` (`:31` build, `:55` test); changing both covers every
  tunnelled connect.
- [VERIFIED:src/Bearing.Sql/LineCommenter.cs] Toggle-comment takes no dialect, so it writes `--` everywhere.
- [VERIFIED:src/Bearing.App/Views/ConnectionDialog.axaml] The password label is a fixed "Password" today.
- [VERIFIED:src/Bearing.App/ViewModels/SchemaNodes.cs] A view-like relation offers "Show definition" whatever
  `OffersTableDdl` says, so an alias needs a definition text.

**Cited** (official docs or vendor source, gathered 2026-11-21; not live-tested)
- [CITED:https://www.elastic.co/docs/reference/query-languages/sql/sql-pagination] Elasticsearch SQL returns a
  `cursor` while rows remain; the next page is `{"cursor": …}`; `page_timeout` (default 45 s) is the cursor's
  retention; one command per request; `"…"` names, `'…'` strings, no `OFFSET`, no write statement.
- [CITED:https://www.elastic.co/docs/reference/query-languages/esql/limitations] ES|QL returns 1,000 rows by
  default and never more than 10,000; no cursor. Grammar: source commands `FROM`, `ROW`, `SHOW INFO`, `TS`,
  `PROMQL`; `"…"` and `"""…"""` strings, backtick names, `//` and `/* */` comments; no released write command
  (`INTO` exists behind a dev-build gate).
- [CITED:https://github.com/opensearch-project/sql/blob/main/docs/user/interfaces/endpoint.rst] OpenSearch SQL
  pages only with `fetch_size`, plain selects only; `plugins.sql.cursor.keep_alive` default 1 m, cluster-wide;
  `plugins.query.size_limit` default 10,000; `DELETE` exists on 2.x behind a setting and is removed in 3.0;
  backtick names; `'…'` and `"…"` strings.
- [CITED:https://github.com/opensearch-project/sql/tree/main/docs/user/ppl] PPL: one statement per request, no
  cursor, no write command; `ml`, `kmeans`, `ad` run ML jobs; first tokens as in Behaviour 4.
- [CITED:https://docs.aws.amazon.com/opensearch-service/latest/developerguide/serverless-clients.html] SigV4
  service is `aoss` for Serverless and `es` for domains; Serverless requires `x-amz-content-sha256`.
- [CITED:https://github.com/awslabs/aws-c-auth/tree/main/tests/aws-signing-test-suite/v4] AWS's SigV4 test
  vectors, used as fixtures for `AwsSigV4Tests`.

**Assumed — each is an open decision until its test or measurement lands**
- [ASSUMED] An OpenSearch SQL statement the server cannot page (aggregate, join) still returns its rows when
  sent with `fetch_size`. SC15 measures it; if it errors, the executor re-sends that statement once without
  `fetch_size` when the error says pagination is unsupported.
- [ASSUMED] The error for an expired or closed cursor is recognisable on both engines. SC14 pins the text by
  closing a cursor server-side; if it is not distinguishable, any failure of a cursor request gets the
  "cursor may have expired" wording with the server's message attached.
- [ASSUMED] `GET /*/_mapping?expand_wildcards=open` includes data streams' backing indices, so a data stream's
  columns come from one call. SC12 measures it; otherwise the reader asks `GET /<streams>/_mapping` once more.
- [ASSUMED] A quoted dotted field (`"a.b"`) and a bare one (`a.b`) both resolve in Elasticsearch SQL; the
  dialect emits the bare form for plain words. SC15 pins it.
- [ASSUMED] The ES|QL default-limit notice arrives as an HTTP `Warning` header readable by `HttpClient`. SC14.
- [ASSUMED] OpenSearch timestamps (`yyyy-MM-dd HH:mm:ss[.fff]`, no zone) are UTC, per the plugin source. SC15.
- [ASSUMED] Amazon OpenSearch domains and Serverless accept the signature as built (service name, signed
  `x-amz-security-token`, region parsed from the host). **Cannot be covered here — no AWS access.** Needs your
  test against a real domain.
- [ASSUMED] Closing the HTTP connection cancels the running query on the server (vendor source says so for
  Elasticsearch; nothing in Bearing's copy claims it).

**Risks**
- A cluster with thousands of indices returns a very large `_mapping`. Mitigation: read as a stream with a
  64 MB ceiling; above it the schema load fails with a sentence naming the size, and queries still run.
- Two scanners (`Sql`, `Data`) can drift (lesson 2026-11-19). Mitigation: `SearchScanParityTests`.
- "Otherwise" routing sends a mistyped `SELCT` to SQL and reports the server's parse error with the language
  named — acceptable, and the reason errors carry the language.
- OpenSearch's 1-minute cursor is short for a human; Bearing can only say so.
- New first tokens in future PPL / ES|QL versions are sent correctly (top-level `|`) but read as risky by the
  guard until listed — a prompt, never a silent run.
- The Prompt credential dialog says "password" for an API key (known limitation).
- Changing only Authentication, AWS region or the credential kind does not evict a live pool on save
  (`SameNetwork` ignores them, as it does every engine's options); the next acquire rebuilds it
  (`ConnectionSessionManager.SameConnection` compares options and kind). A result already on screen keeps
  loading on the identity it was read with.
- Two App helpers still split with the Postgres lexer (`ResultSetBuilder.StatementsBehind`,
  `GoToDefinition`); on text they cannot read they return null, the existing safe answer. Not changed.
- Wayland / headless: the editor rows and the tree need eyeball QA (§4.3).
- Rollback: additive only — removing the two registry entries and traits arms withdraws both engines.
- dirty-worktree: `.claude/analytics.json` (modified) and `.claude/observability/` (untracked) predate this
  work and are out of scope.

**Conditional descopes**
- If Decision 2 is declined: batch 4 is dropped whole and OpenSearch answers `SupportsAwsProfile` false
  (SC7's signature half and SC8 fall away; `AwsProfileNote` is not added).
- If the tunnel seam (batch 1's Core half) proves to disturb existing tunnel tests beyond the new overload,
  drop it: tunnels then reach these engines by rewritten host (certificate checked against 127.0.0.1, as on
  other engines) and AWS-profile sign-in through a tunnel is refused with a sentence.
- If the secured Elasticsearch container cannot be provisioned in CI within the job's memory, SC16 runs
  locally only and is asserted against the fake API in CI.

## Out of scope

- Grid editing; indexing, updating or deleting documents; index and mapping management.
- Query DSL (JSON) requests; EQL; ES|QL `SET` / `LET` directives and `?` parameters; async queries.
- Completion, formatting and comment toggling for ES|QL and PPL; an ANTLR grammar for any of the four languages.
- `:name` variables and `@set`; explain / translate views; the Activity panel (`_tasks`); session time zone.
- Bearer / JWT, SAML, Kerberos and client-certificate sign-in; a custom CA file or fingerprint; proxies beyond
  the system default; Elastic Cloud ID; a URL path prefix.
- Legacy Amazon Elasticsearch domains (`_opendistro/_sql`); Elasticsearch 7 and older; OpenSearch 1.x.
- Cross-cluster browsing (`cluster:index` works only as typed text); frozen and hidden indices in the tree.
- Closing server cursors when a result is discarded; total row counts.
- Release notes (written at release, §9.6a).
- `.claude/analytics.json`, `.claude/observability/` (dirty before this work).

## Rejected alternatives

- **One "Elasticsearch / OpenSearch" engine with the flavour detected from `GET /`.** The dialect would vary
  per connection, against the provider–dialect pairing by id, and Serverless does not serve `GET /`.
- **The official clients (`Elastic.Clients.Elasticsearch`, `OpenSearch.Client`).** Two large dependencies for
  four POSTs and four GETs; the Elastic client refuses OpenSearch servers.
- **A per-tab language mode.** State to persist and a toolbar control, for a question the statement answers.
- **A new `CredentialKind.ApiKey`.** A flag every provider and fake must answer, for a secret that travels
  exactly like a password.
- **The AWS SDK's internal `AWS4Signer`.** Public only in an `Internal` namespace, needs the SDK's own request
  type, and still requires Bearing to pass the keys.
- **`field_multi_value_leniency` on.** Returns one arbitrary value of an array — a lossy read.
- **Re-running the statement with an offset when a cursor expires.** Re-reads, and rows can shift.
- **Paging ES|QL / PPL client-side with a hidden row cache.** Hidden state keyed by text.

## INVEST+C

+C score 15/16: #15 (rollback) is a one-line note because nothing is migrated. INVEST: not Small (12
batches) — accepted, the feature was requested whole; batches 1–8 are independently verifiable.

## Open questions

None blocking beyond *Decisions for approval* 1–7.

## Prior Work Check

Run 2026-11-21. **Verdict: clear.** No match for `elastic`, `opensearch`, `lucene`, `kibana`, `esql`, `ppl` or
`sigv4` in `src/`, `tests/`, `docs/`, `build/`; no issue, PR or branch on the subject. Extended rather than
re-implemented: `QueryResult.Continuation` + `ContinuationPaging` + `BearingHost.FollowContinuationsAsync`
(paging), `ProviderTraits` flags (withheld features), `WriteGuard` / `WriteRefusal` / `ExternalSqlPolicy`,
`CredentialKind.AwsProfile` + `AwsConfigProfiles` + `AwsRegions`, `MySqlMetadataReader.StableId`,
`IDynamoDbApi`'s seam shape, `DynamoDbTestServer` + `build/test-db-dynamodb.sh` (live plumbing).
