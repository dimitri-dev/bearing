# ⚙ Bearing

A fast desktop SQL query tool and script manager for PostgreSQL, with schema-aware
autocomplete as its headline feature — table/column/alias completion and foreign-key
smart-joins that go well beyond a plain keyword list.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/) (cross-platform desktop UI).

## Highlights

- **Schema-aware completion** — ANTLR4 PostgreSQL grammar + [antlr4-c3](https://github.com/mike-lischke/antlr4-c3)
  compute candidates at the caret: tables, columns, resolved aliases, and FK-derived `JOIN … ON …` predicates.
- **Projects as directories** — a shareable `project.json` (connections, no passwords) plus a `scripts/` folder
  of `.sql` files. Commit it to git and share it with the team; per-user session state stays local.
- **Inline result editing** — edit cells in a result grid and Bearing generates parameterized
  UPDATE/DELETE/INSERT keyed by the detected primary key, committed as one transaction.
- **FK navigation** — click a foreign-key cell to follow it to the referenced row, with a back stack.
- **Query history** — every execution is logged to a searchable local SQLite database (full-text over the SQL).
- **Connection environments** — tag connections (local / staging / production) with a color that threads
  through the whole UI so you always know what you're pointed at.
- **Customizable keybindings** and a command palette (`Ctrl+Shift+P`).
- **Amazon DynamoDB, read through PartiQL** — a fourth engine beside PostgreSQL, SQL Server and MySQL/MariaDB.
  See [Amazon DynamoDB](#amazon-dynamodb).
- **Elasticsearch and OpenSearch, query-only** — the fifth and sixth engines, each in its two languages:
  Elasticsearch SQL and ES|QL, OpenSearch SQL and PPL. See [Elasticsearch and OpenSearch](#elasticsearch-and-opensearch).

## Amazon DynamoDB

Connections of type **Amazon DynamoDB** are read-focused: write PartiQL, browse a region's tables and their
indexes, and read the results.

| | |
|---|---|
| **Connect** | *Region* (required), *AWS profile* and an optional *Endpoint*. A blank profile uses the AWS SDK's default credential chain; the endpoint is for DynamoDB Local or a VPC endpoint, and plain `http://` is accepted for a loopback host only. A loopback endpoint with no profile signs with placeholder keys, so DynamoDB Local needs no AWS setup. Region and profile are dropdowns you can also type into: the regions the AWS SDK knows, and the profiles in your AWS config file (`AWS_CONFIG_FILE` or `~/.aws/config` — never the credentials file); picking a profile fills an empty Region with its region. |
| **Sign-in** | The AWS SDK resolves the profile (shared config and credentials files, SSO, assumed roles) inside Bearing's process; Bearing's own code never reads, stores, prompts for or logs an AWS secret, and the keychain holds none. An expired SSO profile says to run `aws sso login --profile <name>`. |
| **Query** | PartiQL `SELECT` (and `INSERT` / `UPDATE` / `DELETE`, behind the usual write confirmation). There is no `LIMIT` / `OFFSET`: a result is read a page at a time and **Load more** / **Fetch all** continue from DynamoDB's own token, so a row already shown is never read twice. |
| **Full scans** | A `SELECT` that does not pin the partition key reads the whole table. Bearing asks before it runs one (**Confirm full table scans** on the connection, on by default) and again before Fetch all or an export. A `SELECT` with no `WHERE` at all (the tree's *Select top 100 rows*) reads about one page of items, so its first page runs without asking; Fetch all and export still ask, and the `bearing` command still refuses it. That check stops mistakes; it is not cost control and not a guarantee — the boundary is an IAM policy. |
| **Read-only** | Refused by Bearing before the statement is sent. DynamoDB has no read-only session, so what stops a write at the server is an IAM policy without write actions. |
| **Not available** | Grid editing, manual commit, query plans, the activity panel, SQL formatting, `:name` variables and `@set`, row counts, *Show definition* on a table, and the TLS and session-time-zone settings. |

DynamoDB declares only a table's key attributes, so expanding a table in the tree reads a sample of up to 20
items and lists the other attributes it found, marked *sampled* — other items may have other attributes.

The `bearing` command reads DynamoDB too, for keyed `SELECT`s only — see [docs/CLI.md](docs/CLI.md).

## Elasticsearch and OpenSearch

Connections of type **Elasticsearch** or **OpenSearch** are query-only: browse indices, aliases and data streams
with their mapped fields, and run statements in the engine's two languages. Bearing talks to both over plain HTTP
and JSON — no client library.

| | |
|---|---|
| **Connect** | *Host* (a name without a scheme) and *Port* (default 9200); the TLS mode decides between `https://` and `http://`. *Disable* is plain HTTP; *Require* encrypts but accepts any certificate, so it is not verified; *Verify CA* checks the chain against the system's trust store, *Verify Full* the host name too; *Prefer* tries HTTPS and falls back to HTTP only when the TLS handshake itself fails. Redirects are never followed. An SSH tunnel works, and the request still names the real host, so a certificate is checked against that name. |
| **Sign-in** | User and password (HTTP Basic), or on Elasticsearch an **API key** (*Authentication*: API key — the password box then takes the encoded key). Both are kept in the OS credential store like any password. A blank user and password send no credentials, for a cluster with security off. On OpenSearch an **AWS profile** signs every request with SigV4: *User* is the profile name (blank uses the AWS SDK's default chain), and the region comes from *AWS region* or an `amazonaws.com` / `on.aws` host name. The AWS SDK resolves the profile; Bearing's signer asks it for the credentials on each request and signs with them in memory, never storing, logging or showing them. A signed request is never sent over plain HTTP to a host other than this machine. |
| **Query** | The language is read from each statement's first word — **Elasticsearch SQL** or **ES\|QL** on Elasticsearch, **OpenSearch SQL** or **PPL** on OpenSearch — so one script can mix them, and each statement comes back as its own result. A server error says which language the statement was sent as. |
| **Paging** | A SQL result is read a page at a time from the server's cursor: **Load more** / **Fetch all** send the cursor alone, never the statement again. Elasticsearch keeps a cursor for 5 minutes between pages; OpenSearch for as long as the cluster's `plugins.sql.cursor.keep_alive` says. OpenSearch pages a plain `SELECT` only — any other shape (an aggregate, a `LIMIT`, a `DISTINCT`) comes back whole, up to the cluster's `plugins.query.size_limit`. ES\|QL and PPL have no paging: every row the server returned is shown (ES\|QL adds `LIMIT 1000` to a statement without one). A warning the server sends with a result — such as that default limit, or an ES\|QL answer marked partial — is shown in the status line. |
| **Values** | Numbers, booleans, text, bytes and dates (shown in the display zone, like `timestamptz`) as such; objects, arrays and geo values as JSON text. A value is never altered to fit its column: one that does not fit on a later page fails that page, naming the column. |
| **Read-only** | Refused by Bearing before the statement is sent. Neither engine has a read-only session, so what stops a write at the server is a role or API key — or, with AWS sign-in, an IAM policy — without write privileges. A statement whose first word is not a known read, and an OpenSearch PPL statement using `ml`, `kmeans` or `ad` (a machine-learning job can store a model), is treated as a possible write. |
| **Not available** | Grid editing, manual commit, query plans, the activity panel, SQL formatting, `:name` variables and `@set`, row counts, table DDL and the session-time-zone setting. Completion offers index and field names in SQL statements only — nothing inside ES\|QL or PPL — and its keywords are PostgreSQL's (Elasticsearch) or MySQL's (OpenSearch). *Toggle comment* writes `--`, which ES\|QL and PPL do not read as a comment. |

The tree shows one database named after the host: indices and data streams as tables, aliases as views (*Show
definition* lists the indices behind one), and each one's mapped fields with dotted names (`user.name`,
`title.keyword`). Indices whose names begin with `.` — hidden and system indices, data-stream backing indices —
are not listed, but can still be queried by name. Elasticsearch needs a name with a dot or a dash quoted whole
(`"logs-2026.11.21"`, `"user.name"`), and Bearing writes it so; OpenSearch reads dotted names bare.

Tested against Elasticsearch 9.5.6 and OpenSearch 3.10.0 running in containers. **Not tested** against an Amazon
OpenSearch Service domain, OpenSearch Serverless or Elastic Cloud.

The `bearing` command reads both, for reads only — see [docs/CLI.md](docs/CLI.md).

## Project layout

The solution (`Bearing.slnx`) is layered so the SQL engine is testable in isolation from the UI:

| Project | Responsibility | External deps |
|---|---|---|
| `Bearing.Core` | Domain vocabulary + provider/persistence interfaces | none |
| `Bearing.Sql` | SQL parsing + completion (ANTLR grammar, antlr4-c3, DML generation; a hand-written scanner for the Elasticsearch and OpenSearch languages) | ANTLR runtime |
| `Bearing.Data` | Implementations of the provider interfaces: PostgreSQL, SQL Server, MySQL/MariaDB, Amazon DynamoDB, Elasticsearch and OpenSearch (the last two over plain HTTP) | Npgsql, Microsoft.Data.SqlClient, MySqlConnector, AWS SDK for .NET |
| `Bearing.Persistence` | Project files, session state, query log (SQLite), secret store | Microsoft.Data.Sqlite |
| `Bearing.Updates` | Release feed + self-update (installers, delta updates) | Velopack |
| `Bearing.App` | Avalonia UI — views, view models, input pipeline | Avalonia |
| `Bearing.Desktop` | Thin `WinExe` entry point (kept separate so `App` stays test-referenceable) | Avalonia.Desktop |

`vendor/` holds the antlr4-c3 C# port and the grammars-v4 PostgreSQL grammar base classes; the parser is
generated at build time by `Antlr4BuildTasks` (which downloads a JRE + the ANTLR tool jar on first build).

## Install

Packaged builds carry their own updater: the app checks once per launch, downloads a new version in the
background, and offers a restart. Nothing is installed while you work.

| Platform | Download | Notes |
|---|---|---|
| Windows | `BearingSql-win-Setup.exe` | Per-user install to `%LOCALAPPDATA%\BearingSql`, Start Menu entry. No admin needed. |
| Linux | `BearingSql.AppImage` | `chmod +x` and run it. Updates itself in place. |
| macOS | `brew install --cask moberghr/bearing/bearing`, then `xattr -dr com.apple.quarantine /Applications/Bearing.app` | Apple Silicon. Or `BearingSql-osx-Setup.pkg` by hand. Updates itself in place, so `brew upgrade` is not needed. |

Releases are on the repository's [Releases page](https://github.com/moberghr/bearing/releases). Nothing
needs configuring for updates to work; the installers are unsigned, so Windows SmartScreen warns on first
run and macOS needs the quarantine flag cleared — which is what the `xattr` step does, and why it follows
the install ([packaging/homebrew](packaging/homebrew/README.md)).

`build/release.sh` remains as the no-updater alternative: a single-file `.tar.gz` (Linux, with a per-user
installer and `.desktop` entry) or `.zip` (Windows, with PowerShell install/uninstall scripts). Updating one
of those means downloading the next one. Cutting a release is `build/velopack.sh` — see
[docs/RELEASING.md](docs/RELEASING.md).

## Build & run

```bash
dotnet build
dotnet run --project src/Bearing.Desktop
```

On startup Bearing reopens the project you last used. On first run (or if that project is gone) it opens a
default project at `projects/default` inside the app data directory (see
[Where your data lives](#where-your-data-lives)) and seeds a demo connection pointing at a local
`pagila` database.

## Tests

```bash
dotnet test
```

Pure unit tests (SQL/completion, persistence, formatting) run with no external services. The `Bearing.Data`
integration tests need a live PostgreSQL loaded with [pagila](https://github.com/devrimgunduz/pagila); they
are `SkippableFact` and silently skip when no database is reachable. One script provisions one:

```bash
./build/test-db.sh          # start it, load pagila, verify (idempotent)
dotnet test                 # no env vars needed — the defaults point at it
./build/test-db.sh stop     # remove the container
```

It creates the `squirrel-pg-test` container on **55434** with pagila loaded, which is exactly what
`tests/Shared/PgTestServer.cs` defaults to. Deliberately not 5434: that port sits in the range other developer
tooling claims, and on one machine here it was an SSM tunnel to a *real remote* database that these defaults
reached and were refused by. The script refuses to bind over a listener it did not create, and the tests that
run DDL check for a marker row it writes before doing so.

The tests read `BEARING_TEST_PG_*` env vars (host/port/db/user/password) if you would rather point them
somewhere else.

The other engines' live suites skip the same way and have a script each: `./build/test-db-mssql.sh` (SQL
Server), `./build/test-db-mysql.sh` (MariaDB and MySQL) and `./build/test-db-dynamodb.sh` (DynamoDB Local, the
`bearing-dynamodb-test` container from `amazon/dynamodb-local` on **18000**, in memory, with no credentials to
set up) and `./build/test-db-search.sh` (Elasticsearch on **19200** and OpenSearch on **19201**, security off, plus
a secured Elasticsearch on **19202** for the sign-in tests). `BEARING_TEST_DYNAMODB_ENDPOINT` points the DynamoDB
tests somewhere else, and `BEARING_TEST_ELASTICSEARCH_ENDPOINT` / `BEARING_TEST_OPENSEARCH_ENDPOINT` the search
ones. A skip is not a pass — run the containers before trusting a green run.

The default password is `squirrel` — a throwaway dev credential for the local test container, nothing to do
with the app. Override it, and every other default, with `BEARING_TEST_PG_*` (`tests/Shared/PgTestServer.cs`).

## Where your data lives

Bearing separates data by *shareability*:

| Tier | Location | Contents | Shared? |
|---|---|---|---|
| **Project** | `<project>/project.json` + `scripts/*.sql` | Connection settings (**no passwords**), SQL scripts | Yes — meant to be committed |
| **Session** | `<project>/.bearing/session.json` | Open tabs, caret, active connection, pane layout | No — gitignored |
| **App-global — data** | platform data dir (below) | `query-log.sqlite`, default project | No |
| **App-global — config** | platform config dir (below) | recent projects, `keybindings.json`, `settings.json` | No |

The two app-global directories follow each platform's own convention:

| Platform | Config dir | Data dir |
|---|---|---|
| Linux | `$XDG_CONFIG_HOME/bearing` (`~/.config/bearing`) | `$XDG_DATA_HOME/bearing` (`~/.local/share/bearing`) |
| Windows | `%APPDATA%\bearing` (roaming) | `%LOCALAPPDATA%\bearing` (local — the query log and secrets must not sync) |
| macOS | `~/Library/Application Support/bearing` | `~/Library/Application Support/bearing` |

`XDG_CONFIG_HOME` / `XDG_DATA_HOME` take precedence on every platform when set, so redirecting state for
tests or a portable install works the same way everywhere.

### Profiles — isolating dev from real data

Set `BEARING_PROFILE=<name>` and the app-global directory name becomes `bearing-<name>` (e.g.
`~/.config/bearing-dev`, `~/.local/share/bearing-dev`), plus a matching keychain namespace. This keeps
one instance's connections, history, and secrets fully separate from another's.

Running from source (`dotnet run`) automatically uses the **`dev`** profile via
`src/Bearing.Desktop/Properties/launchSettings.json`, so development never touches the installed app's real
projects and settings. The published/installed binary has no profile and uses the plain `bearing` dirs.

### Secrets

Connection passwords are **never** written to `project.json`. They are stored keyed by the connection's GUID
(which does travel with the project) so a shared project prompts each user for their own password.

- **Preferred:** the OS credential store, one per platform — the freedesktop Secret Service via `secret-tool`
  (libsecret) on **Linux**, the Credential Manager on **Windows** (visible under Control Panel ▸ Credential
  Manager ▸ Windows Credentials), and a login-keychain generic password on **macOS** (visible in Keychain
  Access). Which one you get is decided at startup by actually storing and reading back a throwaway secret, so
  an absent helper or a locked keyring falls back rather than failing later when you connect.
  *`dotnet test` on each platform runs the full store contract (`PlatformKeychainTests`).*
- **No credential store reachable?** Then the password is **not saved at all** — the connection keeps it in
  memory for the session and asks again next time. There is no on-disk fallback and nothing to opt into: the
  status bar and the connection dialog say so, and the dialog shows what the credential store actually
  reported (a locked keyring and a missing helper want different fixes). Because the check can fail simply by
  running before the keyring is serving, it is re-run every time the connection dialog opens — an upgrade
  only, so a working keychain is never dropped mid-session.

### Query log

Every query you run is recorded in `query-log.sqlite` (append-only, full-text searchable). History is pruned to
a configurable retention window (default 180 days) — see `settings.json`.
