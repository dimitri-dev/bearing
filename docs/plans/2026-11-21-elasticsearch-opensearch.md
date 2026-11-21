# Plan — Elasticsearch and OpenSearch as query engines (query-only v1)

Spec: `docs/specs/2026-11-21-elasticsearch-opensearch.md` (+ `.json`). Branch:
`feat/add-elasticsearch-opensearch-support` (from `main` @ `9008b4b`). Todo: `tasks/todo.md`.

## Package legitimacy

No new package. `HttpClient` and `System.Text.Json` are part of .NET 10; `AWSSDK.Core` 4.1.1.1 is already
pinned in `Directory.Packages.props` and referenced through `AWSSDK.DynamoDBv2`. SC21 asserts
`Directory.Packages.props` is unchanged.

## Batches

Verification for every batch: `dotnet build` then `dotnet test` (CLAUDE.md, *Tech Stack*), plus the batch's
own classes with `dotnet test --filter "FullyQualifiedName~<class>"`. On this host a build that fails NU1301
needs `-p:RestoreConfigFile=<config clearing fallbackPackageFolders>` (`tasks/lessons.md`, 2026-11-19).
Each batch is one commit when commits are asked for (§8.1: none until then); messages follow §8.2.

| # | Batch | Files | Acceptance | Boundary (what must stay true) | Depends | Rules |
|---|---|---|---|---|---|---|
| B1 | Seams, no behaviour change — `add a tunnel-aware factory overload and a completion gate` | `Core/Data/TunnelEndpoint.cs`, `Core/Data/IDbProvider.cs`, `Sessions/ConnectionFactoryBuilder.cs`, `Sql/ISqlDialect.cs`, `Sql/CompletionEngine.cs`, `Core/Data/QueryResult.cs` (`Notice`), `SshTunnelTests` | SC17; every existing test unchanged | Core gains a record and a default member, no reference. No existing provider or fake changes behaviour. `RouteAsync`'s refusals stay where they are. | — (first; creates the seams) | §0.1 §2.1 §2.2 §1.12 |
| B2 | `SearchScan` and the two dialects — `add elasticsearch and opensearch dialects` | `Sql/SearchScan.cs`, `Sql/ElasticsearchDialect.cs`, `Sql/OpenSearchDialect.cs`, 4 test classes | SC2–SC5 (Sql half) | The guard only widens. No existing dialect or rule set changes. `Bearing.Sql` gains no reference. | B1 | §1.2 §1.11a §1.11b §2.2 §2.5 §9.5 |
| B3 | Transport — `add an http seam for the search engines` | `Search/ISearchApi.cs`, `Search/SearchConnectionFactory.cs`, `Search/SearchErrors.cs`, 3 test classes | SC6 | Validation is relaxed only in the TLS mode that says so. No redirects. No secret or `Authorization` value in a message or log. A refused setting fails the connect, not the constructor. | B1 | §1.1 §1.4 §1.12 §4.1 §5.3 |
| B4 | SigV4 — **only if Decision 2 is approved** — `sign opensearch requests with an aws profile` | `Search/AwsSigV4.cs`, `Aws/AwsProfileCredentials.cs`, `DynamoDb/DynamoDbConnectionFactory.cs`, `Search/SearchConnectionFactory.cs`, 4 test classes | SC7, SC8 | Credentials are asked from the SDK per request; never kept in a Bearing field, logged or shown. Sign-in errors come from the profile name alone. DynamoDB's sign-in is unchanged. | B3 | §0.3 §1.1 §1.9c |
| B5 | Protocol — `map search responses to result rows` | `Search/SearchBatch.cs`, `SearchProtocol.cs`, `SearchValues.cs`, `SearchContinuation.cs`, 4 test classes + `SearchScanParityTests` (in `Data.Tests`) | SC2, SC3 (Data half), SC9 | Reads are lossless. The continuation decodes only what this class wrote. `Data` does not reference `Sql`. A server warning goes to `Notice`, never `Message`. | B1, B2 | §1.3 §2.2 §5.1b §5.7 |
| B6 | Executor — `add the search executor` | `Search/SearchQueryExecutor.cs`, `SearchQueryExecutorTests` | SC10 | A continuation never re-sends the statement. `ExecuteAsync` returns failures, the stream throws them. The timeout is named as ours only when it is ours. | B3, B5 | §1.9a §5.7 §5.8 §4.4 |
| B7 | Metadata — `read indices, aliases and data streams` | `Search/SearchMappings.cs`, `Search/SearchMetadataReader.cs`, 2 test classes | SC11 | Absence stays typed (empty list vs refused read). No credential-bearing index or setting is read. Hidden indices are never listed. | B3 | §1.8 §5.6 §9.9 |
| B8 | Provider, registry, traits — `register elasticsearch and opensearch with their traits` | `Search/SearchProvider.cs`, `Data/ProviderRegistry.cs`, `Bearing.Data.csproj`, `Sessions/ProviderTraits.cs` (two arms + `AwsProfileNote`), 3 test classes | SC1 | A provider is never registered without its traits arm. Every flag is answered explicitly. No package is added. | B2, B4, B6, B7 | §1.9a §2.2 §2.4 §2.6 |
| B9 | Live plumbing — `add elasticsearch and opensearch live tests` | `build/test-db-search.sh`, `tests/Shared/SearchTestServer.cs`, `SearchLiveTests`, `Bearing.Data.Tests.csproj` (link line), `ci.yml` | SC12–SC16, 0 skipped with containers up | Live tests skip without a container, never fail without one. Fixtures are made over raw HTTP, uniquely named, removed. Images pinned, overridable by env var. | B8 | §4.2 §4.7 |
| B10 | App — `label the password box from the provider and cover the search engines in the editor` | `App/Connections/ConnectionFieldModel.cs`, `App/Views/ConnectionDialog.axaml(.cs)` (password label, AWS-profile hint from `ProviderTraits.AwsProfileNote`), `App/Results/ResultSetBuilder.cs` (`Notice` in the status line), 3 test classes | SC18, SC19 | No DB, connection or SQL logic in a view. The three god objects do not grow. Other engines' dialogs render as before. | B8 | §0.2 §0.4 §0.5 §2.3 §4.3 §4.5 |
| B11 | `bearing` command — `cover the search engines in the bearing command` | `SearchExposureTests`, `ConnectionListingTests`, `docs/CLI.md` | SC20 | `ForExternalHost`'s three forced settings are untouched. Refusals are logged. `Cli` never references `App`. | B8 | §1.11 §1.11a §1.11b §1.11d §1.11e |
| B12 | Docs and rules, full verification — `document elasticsearch and opensearch` | `README.md`, `CLAUDE.md`, `AGENTS.md`, `.claude/rules/{architecture,data-layer,security,testing}.md` | SC21, SC22 | Docs state only what was built and measured: never `Require` as verified, read-only as a boundary, or an AWS path as tested. | B9, B10, B11 | §1.1 §1.4 §1.9 |

## Wave schedule (readback of the edges)

`W0: B1 · W1: B2 B3 · W2: B4 B5 B7 · W3: B6 · W4: B8 · W5: B9 B10 B11 · W6: B12`

Execution is sequential in that order (B1 → B12) so every batch lands on a green tree; B3 and B4 both edit
`SearchConnectionFactory.cs` and its tests, which is why B4 depends on B3.

## Measurements that settle the spec's assumptions

| Assumption | Settled in | If it fails |
|---|---|---|
| OpenSearch returns rows for a non-pageable SQL shape sent with `fetch_size` | B9 (SC15) | B6 re-sends once without `fetch_size` on the pagination-unsupported error |
| Expired / closed cursor error is recognisable | B9 (SC14) | Any cursor-request failure gets the "may have expired" wording plus the server's message |
| `_mapping` with `expand_wildcards=open` covers data-stream backing indices | B9 (SC12) | B7 asks `GET /<streams>/_mapping` once more |
| Bare dotted field paths resolve in both SQL dialects | B9 (SC15) | The dialect quotes per segment |
| ES\|QL default-limit notice is a readable `Warning` header | B9 (SC14) | No notice is shown; the row count stands alone |
| OpenSearch zone-less timestamps are UTC | B9 (SC15) | The column stays text |
| AWS accepts the signature | **the user's test against a real domain** | Fix from the reported error; unit vectors stay as the regression net |

## After the batches

Full `dotnet build` + `dotnet test`; live suite with 0 skipped (or the gap reported); spec-drift check;
review (compliance, then test / architecture / silent-failure / context-miner); eyeball-QA list for the user.
