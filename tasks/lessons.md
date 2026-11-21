# Lessons

Reusable corrections and gotchas captured during work.

## 2026-11-06 — inline results (AvaloniaEdit inline objects)

- **What happened:** A `Dispatcher.UIThread.Post` with no priority never ran in headless tests. **Rule:** pass
  `DispatcherPriority.Normal` (or higher) when the work must follow promptly. **Why:** `Default` is the lowest
  foreground priority, below `Loaded`, which is all `ShellHarness.Pump` drains — the app would run it late, the
  test never. **When:** any post whose effect a UI test asserts.
- **What happened:** Reading `Caret.VisualColumn` inside `Caret.PositionChanged` re-raised the event from inside
  itself (revalidation), and the nested call recorded the wrong "from" line. A passing run only appeared when
  debug output happened to read the column earlier. **Rule:** guard caret handlers against re-entrancy and
  capture "before" state before touching caret properties. **Why:** AvaloniaEdit caret getters have side
  effects. **When:** any `PositionChanged` handler that reads `VisualColumn`/`Position`.
- **What happened:** An inline-object row (TextEndOfLine + InlineObjectRun) is part of its line's visual line
  and has no caret stops, so Down snapped back to the line end forever. **Rule:** step over such rows using
  `Caret.DesiredXPos` (set only by Up/Down, assigned after the position — check it in a posted callback).
  **When:** any widget embedded between editor lines.
- **What happened:** The PostgreSQL split keeps an own-line comment followed by a blank line with the statement
  above, so `StatementSpan.TrimmedEnd` can sit on a comment line. **Rule:** for "where does this statement end
  on screen", ask `ISqlDialect.CodeEnd`. **When:** placing anything relative to a statement's end.
- **What happened:** "The buffer was replaced" was first inferred in the view (same tab, different text) and
  missed background-tab reloads. **Rule:** react where the replacement happens (`ScriptFileSync.Reload`,
  `LoadScriptIntoSelectedAsync`), not by diffing in `MainWindow`. **When:** per-tab state tied to buffer offsets.

## 2026-11-19 — DynamoDB provider (fourth engine)

- **What happened:** `dotnet build` failed NU1301 on a missing Visual Studio fallback package folder from the machine's
  global NuGet config; the repo's `nuget.config` clears sources but not fallback folders. **Rule:** on such a host pass
  `-p:RestoreConfigFile=<a config that clears <fallbackPackageFolders>>`; don't edit machine config. **When:** a red
  baseline that names a "local source" that doesn't exist.
- **What happened:** DynamoDB Local 3.1 rejected the placeholder access key `bearing-local` (hyphen) with "access key
  ID or security token is invalid"; unit tests only inspected the signed request and missed it. **Rule:** placeholder
  AWS keys are letters/digits only, and a sign-in path is proven by a live round trip, not a header assertion.
- **What happened:** an engine whose server identity lives in an `Options` entry (DynamoDB `Endpoint`) was invisible to
  `SameNetwork` / `SamePool`, so an endpoint-only edit kept the old schema and pool. **Rule:** any option that decides
  which server is reached belongs in both comparisons. **When:** adding a connection field outside Host/Port/Database/User.
- **What happened:** `ProviderTraits.For` falls back to Postgres for an unknown id, so registering a provider before its
  traits arm would silently pair it with the Postgres dialect. **Rule:** register a provider in the same commit as its
  traits arm.
- **What happened:** the App and the CLI each gathered scan facts; the copies drifted within a day (only one padded a
  SELECT the reduction skipped, so the App ran it unasked). **Rule:** a run/refuse decision both hosts need lives once
  in Sessions (§2.6), including its fail-closed padding.
- **What happened:** the spec assumed the PostgreSQL lexer reads PartiQL identically; seven divergent forms were found.
  **Rule:** decline divergent batches individually (widening only) rather than turning the guard off for the engine,
  which would empty the CLI allow-list.

## 2026-11-21 — Elasticsearch and OpenSearch engines (fifth and sixth)

- **What happened:** the spec was written from vendor docs, and the live containers contradicted it in five places
  (a bare index name with a dot does not parse on Elasticsearch SQL; OpenSearch answers anything it cannot page
  through a legacy engine that mislabels column types; an ES|QL answer can be HTTP 200 and partial; ES|QL block
  comments nest; PPL names integer types `int` / `bigint`). Eight batches were built on the cited behaviour before
  the live batch ran. **Rule:** for an engine reached over a wire protocol, stand the container up and measure the
  assumptions FIRST (a short probe batch), then write the protocol and dialect batches. **When:** any spec whose
  assumptions list cites docs for server behaviour.
- **What happened:** an error built by Bearing quoted the cell value that did not fit its column; through
  `bearing --out` that text reached the query log, where literal redaction does not apply. **Rule:** a message
  Bearing composes names the column, the type and the kind of value — never row data, a cursor or a credential.
  Server-authored error text is scrubbed and passed on; text we build must not need scrubbing. **When:** any new
  exception or `QueryError` in a provider.
- **What happened:** the test for "fall back to plain http only on a TLS handshake failure" passed with the filter
  removed (a closed port fails both attempts the same way). **Rule:** for a fail-closed branch, delete the branch
  once and watch the test go red before trusting it. **When:** tests of fallbacks, filters and refusals.
- **What happened:** the CLI was specified not to repeat server notices; a later decision put "this answer is
  partial" into the same notice, and the unattended host silently lost it (three reviewers raised it). **Rule:**
  when a field gains a meaning that marks an answer incomplete, re-check every host that drops the field.
  **When:** adding a case to `QueryResult.Notice` / `Message` / `Truncated`.
- **What happened:** `SamePool` (App) and `SameConnection` (Sessions) disagreed about options that decide sign-in,
  so a saved change kept the old sign-in on lease-based reads. **Rule:** an option that decides how a pool signs in
  goes in `ProviderTraits.PoolOptions`; one that decides which server is reached goes in `NetworkOptions`.
  **When:** adding a connection option to any engine.
- **What happened:** the SSH test bastion stopped answering after back-to-back full runs, and 8 live tunnel tests
  failed in `SshTunnels.ConnectAsync` with no SSH banner. **Rule:** `docker restart bearing-ssh-test`, then re-run
  `LiveSshTunnelTests`, before reading it as a code failure. **When:** a red full suite whose only failures are
  that class.
