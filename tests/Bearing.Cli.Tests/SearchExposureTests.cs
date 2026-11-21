using System.Runtime.CompilerServices;
using Bearing.Cli.Tools;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Data.Search;
using Bearing.Persistence;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// What the <c>bearing</c> command does with an exposed <b>Elasticsearch</b> or <b>OpenSearch</b> connection
/// (SC20, Behaviour 11): the engine's allow-list runs, a write, a denied pipe command and a dot-prefixed index
/// are refused in the policy's own words and logged, an export follows a SQL cursor to the end, and
/// <c>--max-rows</c> trims an ES|QL or PPL answer the server sent whole.
/// <para>
/// No server: the real host, the real session manager and each engine's real dialect and traits (chosen by the
/// provider id), over a hand-rolled provider whose executor answers SQL a page at a time by continuation and a
/// piped statement whole, and counts every call (§4.1). <c>src/Bearing.Cli</c> has no search-specific code —
/// these tests are the proof that the dialect- and trait-driven paths hold for these engines. For a refusal the
/// executor's call count is the assertion that matters: a refused statement never reaches it.
/// </para>
/// </summary>
public class SearchExposureTests : IAsyncLifetime
{
    private const string Es = SearchProvider.ElasticsearchId;
    private const string Os = SearchProvider.OpenSearchId;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-search-exposure", Guid.NewGuid().ToString("N"));

    private readonly RecordingQueryLog _log = new();
    private FakeSearchProvider? _provider;
    private ConnectionSessionManager? _sessions;

    private FakeSearchProvider Provider => _provider ?? throw new InvalidOperationException("No host yet.");
    private FakeSearchExecutor Executor => Provider.Executor;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_sessions is not null) await _sessions.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---- listing and the catalog ------------------------------------------------------------------

    [Theory]
    [InlineData(Es, "Elasticsearch")]
    [InlineData(Os, "OpenSearch")]
    public async Task An_exposed_connection_is_listed_as_available_under_its_engine(string engine, string displayName)
    {
        var host = await HostAsync(engine);

        var listed = Assert.Single(((ConnectionsResponse)await host.ListConnectionsAsync(CancellationToken.None))
            .Connections);

        Assert.Equal(NameOf(engine), listed.Name);
        Assert.Equal(displayName, listed.Engine);
        Assert.True(listed.Available);
        Assert.Null(listed.UnavailableReason);
    }

    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Tables_lists_indices_and_aliases_from_the_catalog_and_runs_no_statement(string engine)
    {
        var host = await HostAsync(engine);

        var tables = (TablesResponse)await host.ListTablesAsync(NameOf(engine), null, CancellationToken.None);

        Assert.Equal(
            ["logs-2026.11.21:table", "orders:table", "recent-orders:view"],
            tables.Tables.Select(t => $"{t.Name}:{t.Kind}"));
        Assert.Equal(0, Executor.Calls);
        Assert.Empty(_log.Entries);   // catalog reads are not the record of what someone ran (§1.11d)
    }

    /// <summary>An index name holds dashes and dots freely; <c>describe</c> takes the whole name when the
    /// schema-qualified reading names nothing (there are no schemas here).</summary>
    [Theory]
    [InlineData(Es, "logs-2026.11.21")]
    [InlineData(Es, "\"logs-2026.11.21\"")]
    [InlineData(Os, "logs-2026.11.21")]
    [InlineData(Os, "  logs-2026.11.21 ")]
    public async Task Describe_finds_an_index_whose_name_holds_a_dash_and_dots(string engine, string asked)
    {
        var host = await HostAsync(engine);

        var table = (TableResponse)await host.DescribeTableAsync(NameOf(engine), asked, CancellationToken.None);

        Assert.Equal(FakeSearchProvider.DottedIndex, table.Name);
        Assert.Equal(["@timestamp", "message"], table.Columns.Select(c => c.Name));
        Assert.Equal(0, Executor.Calls);
    }

    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Describe_lists_an_alias_with_its_dotted_field_paths(string engine)
    {
        var host = await HostAsync(engine);

        var table = (TableResponse)await host.DescribeTableAsync(NameOf(engine), "recent-orders", CancellationToken.None);

        Assert.Equal("view", table.Kind);
        Assert.Equal(["id", "customer.name", "total"], table.Columns.Select(c => c.Name));
    }

    // ---- the allow-lists run ---------------------------------------------------------------------

    [Theory]
    [InlineData(Es, "select * from orders")]
    [InlineData(Es, "show tables")]
    [InlineData(Es, "describe \"orders\"")]
    [InlineData(Es, "from orders | limit 5")]
    [InlineData(Es, "row a = 1")]
    [InlineData(Es, "ts orders | limit 5")]
    [InlineData(Es, "desc orders")]
    [InlineData(Os, "select * from orders")]
    [InlineData(Os, "index=orders | head 5")]
    [InlineData(Os, "show tables like '%'")]
    [InlineData(Os, "describe tables like 'orders'")]
    [InlineData(Os, "source=orders | head 5")]
    [InlineData(Os, "search source=orders")]
    [InlineData(Os, "source = orders")]
    public async Task A_statement_on_the_engines_allow_list_runs_and_is_logged(string engine, string sql)
    {
        var host = await HostAsync(engine);

        var response = (QueryResponse)await host.QueryAsync(new RunRequest(NameOf(engine), sql), CancellationToken.None);

        Assert.Single(response.Results);
        Assert.Equal(1, Executor.Executes);
        Assert.Equal([sql], Executor.Sent);
        var entry = Assert.Single(_log.Entries);
        Assert.True(entry.Success);
        Assert.Equal(sql, entry.SqlText);
        Assert.Equal(QueryOrigin.Cli, entry.Origin);
    }

    /// <summary>A dot that ends a path is a field, not an index, and a string in a <c>WHERE</c> names no
    /// source: both are near-misses of the dot-index refusal and must run.</summary>
    [Theory]
    [InlineData(Es, "select a.b from orders")]
    [InlineData(Es, "select * from orders where name = '.x'")]
    [InlineData(Es, "select \"customer.name\" from orders")]
    [InlineData(Os, "select a.b from orders")]
    [InlineData(Os, "select * from orders where name = '.x'")]
    [InlineData(Os, "source=orders | where name = '.x'")]
    public async Task A_dotted_field_or_a_dotted_string_is_not_a_dot_index_and_runs(string engine, string sql)
    {
        var host = await HostAsync(engine);

        await host.QueryAsync(new RunRequest(NameOf(engine), sql), CancellationToken.None);

        Assert.Equal(1, Executor.Executes);
        Assert.True(Assert.Single(_log.Entries).Success);
    }

    /// <summary>A doubled <c>;</c> leaves an empty statement behind the read; it is skipped, not refused as
    /// "no statement" (decision D5), and the run goes out once.</summary>
    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task A_read_followed_by_a_doubled_semicolon_runs(string engine)
    {
        var host = await HostAsync(engine);

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), "select 1;;"), CancellationToken.None);

        Assert.Single(response.Results);
        Assert.Equal(["select 1;;"], Executor.Sent);
        Assert.True(Assert.Single(_log.Entries).Success);
    }

    /// <summary>The language is read from each statement's own first word, so one script can hold both; each
    /// statement is judged alone and comes back as its own result set.</summary>
    [Theory]
    [InlineData(Es, "select * from orders; from orders | limit 5")]
    [InlineData(Os, "select * from orders; source=orders | head 5")]
    public async Task A_script_mixing_sql_and_a_piped_statement_runs_each_as_its_own_result(string engine, string sql)
    {
        var host = await HostAsync(engine);

        var response = (QueryResponse)await host.QueryAsync(new RunRequest(NameOf(engine), sql), CancellationToken.None);

        Assert.Equal(2, response.Results.Count);
        Assert.Equal(FakeSearchExecutor.PageSize, response.Results[0].RowCount);   // the SQL statement's first page
        Assert.True(response.Results[0].Truncated);
        Assert.Equal(FakeSearchExecutor.WholeRows, response.Results[1].RowCount);  // the piped answer, whole
        Assert.False(response.Results[1].Truncated);
        Assert.Equal([sql], Executor.Sent);
    }

    // ---- refusals: logged, and never sent ----------------------------------------------------------

    [Fact]
    public async Task An_opensearch_delete_is_refused_as_a_write()
    {
        const string sql = "delete from orders where id = 1";

        var failure = await RefusedAsync(Os, sql);

        Assert.Equal(
            "'agent-opensearch' is exposed to external tools for reads only, so DELETE will not run. Its owner can "
            + "widen that in Bearing, or run the statement there themselves.",
            failure);
    }

    [Fact]
    public async Task An_esql_into_command_is_refused_as_a_write()
    {
        var failure = await RefusedAsync(Es, "from orders | into copy");

        Assert.Equal(
            "'agent-elasticsearch' is exposed to external tools for reads only, so INTO will not run. Its owner "
            + "can widen that in Bearing, or run the statement there themselves.",
            failure);
    }

    /// <summary>An unknown first word is not a read — which is all Bearing knows about it. The sentence lists
    /// the engine's reads and claims no write it never found (§1.1). A statement led by PPL's <c>rest</c> is
    /// one of these: the allow-list refuses it before the dialect's own <c>rest</c> sentence is asked.</summary>
    [Theory]
    [InlineData(Es, "foo bar", "'FOO' is not a read. A connection exposed to external tools runs only DESC, "
        + "DESCRIBE, FROM, ROW, SELECT, SHOW, TS — anything else is refused, including statements that would "
        + "change how this session behaves.")]
    [InlineData(Os, "foo bar", "'FOO' is not a read. A connection exposed to external tools runs only DESCRIBE, "
        + "INDEX, SEARCH, SELECT, SHOW, SOURCE — anything else is refused, including statements that would "
        + "change how this session behaves.")]
    [InlineData(Os, "rest get /_cluster/settings", "'REST' is not a read. A connection exposed to external tools "
        + "runs only DESCRIBE, INDEX, SEARCH, SELECT, SHOW, SOURCE — anything else is refused, including statements "
        + "that would change how this session behaves.")]
    public async Task An_unknown_first_word_is_refused_as_not_a_read_without_claiming_a_write(
        string engine, string sql, string sentence)
    {
        var failure = await RefusedAsync(engine, sql);

        Assert.Equal($"{NameOf(engine)}: {sentence}", failure);
        Assert.DoesNotContain("writes", failure);
        Assert.DoesNotContain("will not run", failure);
    }

    [Theory]
    [InlineData(Es, "from orders | completion prompt", "'COMPLETION' is not available through an exposed "
        + "connection: it sends rows to an inference endpoint, outside the data.")]
    [InlineData(Es, "from orders | rerank \"q\" on message", "'RERANK' is not available through an exposed "
        + "connection: it may call an inference endpoint, outside the data.")]
    [InlineData(Os, "source=orders | ml action='train' algorithm='rcf'", "'ml' is not available through an "
        + "exposed connection: it runs a machine-learning job on the cluster.")]
    [InlineData(Os, "source=orders | kmeans centroids=3", "'kmeans' is not available through an exposed "
        + "connection: it runs a machine-learning job on the cluster.")]
    [InlineData(Os, "source=orders | ad time_field='ts'", "'ad' is not available through an exposed "
        + "connection: it runs a machine-learning job on the cluster.")]
    [InlineData(Os, "source=orders | rest", "'rest' is not available through an exposed connection: it reads "
        + "the cluster's management endpoints rather than an index.")]
    public async Task A_denied_pipe_command_is_refused_by_name(string engine, string sql, string sentence)
    {
        var failure = await RefusedAsync(engine, sql);

        Assert.Equal(
            $"{NameOf(engine)}: {sentence} This stops accidents and is not a boundary — a role without the "
            + "privilege is.",
            failure);
    }

    [Theory]
    [InlineData(Es, "select * from \".security\"", ".security")]
    [InlineData(Es, "from .kibana | limit 1", ".kibana")]
    [InlineData(Os, "select * from .opendistro_security", ".opendistro_security")]
    [InlineData(Os, "source=.opendistro_security", ".opendistro_security")]
    public async Task A_statement_naming_a_dot_prefixed_index_is_refused(string engine, string sql, string index)
    {
        var failure = await RefusedAsync(engine, sql);

        Assert.Equal(
            $"{NameOf(engine)}: '{index}' is not readable through an exposed connection: a name beginning with '.' "
            + "is, by convention, a hidden or system index. This stops accidents and is not a boundary — a role "
            + "without read access to it is.",
            failure);
    }

    /// <summary>One refused statement refuses the whole script — wherever it stands in it — before anything is
    /// sent, so the allowed statement beside it does not run either.</summary>
    [Theory]
    [InlineData(Es, "select * from orders; from orders | completion prompt", "'COMPLETION'")]
    [InlineData(Es, "from .kibana | limit 1; select * from orders", "'.kibana'")]
    [InlineData(Os, "select * from orders; source=orders | kmeans centroids=3", "'kmeans'")]
    [InlineData(Os, "source=orders | head 5; delete from orders", "DELETE")]
    public async Task A_script_with_one_refused_statement_sends_nothing(string engine, string sql, string named)
    {
        var failure = await RefusedAsync(engine, sql);

        Assert.Contains(named, failure);
    }

    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Explain_is_refused_as_not_served_and_nothing_is_sent(string engine)
    {
        var host = await HostAsync(engine);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.ExplainAsync(
            new RunRequest(NameOf(engine), "select * from orders"), CancellationToken.None));

        Assert.Equal(
            $"'{NameOf(engine)}' does not serve query plans through bearing — its engine reports them in a form this "
            + "command cannot read. Run the query itself, or ask for the plan in Bearing.",
            failure.Message);
        Assert.Equal(0, Executor.Calls);
    }

    // ---- paging: the first page, the cursor followed, the cap -----------------------------------------

    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task A_sql_query_returns_the_first_page_and_says_it_was_cut_short(string engine)
    {
        var host = await HostAsync(engine);

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), "select * from orders"), CancellationToken.None);

        var set = Assert.Single(response.Results);
        Assert.Equal(FakeSearchExecutor.PageSize, set.RowCount);
        Assert.True(set.Truncated);
        Assert.Null(set.StoppedBy);
        Assert.Equal(0, Executor.Continuations);   // a plain query is the first page, not the read
    }

    [Theory]
    [InlineData(Es, "orders.csv")]
    [InlineData(Es, "orders.xlsx")]
    [InlineData(Os, "orders.csv")]
    [InlineData(Os, "orders.xlsx")]
    public async Task An_export_of_a_sql_result_follows_the_cursor_to_the_last_row(string engine, string file)
    {
        var host = await HostAsync(engine);
        var path = Path.Combine(_root, file);

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), "select * from orders") { OutPath = path }, CancellationToken.None);

        Assert.Equal(FakeSearchExecutor.Items, export.Rows);
        Assert.False(export.Truncated);
        Assert.True(File.Exists(path));
        Assert.Equal(1, Executor.Executes);
        Assert.Equal(2, Executor.Continuations);   // three pages: the first, then two by cursor
        Assert.Equal(0, Executor.Streams);
        Assert.Equal(FakeSearchExecutor.Items, Assert.Single(_log.Entries).RowCount);

        if (file.EndsWith(".csv", StringComparison.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            Assert.Equal(FakeSearchExecutor.Items + 1, lines.Length);   // the header and every row
            Assert.Equal(FakeSearchExecutor.Items, lines.Skip(1).Distinct().Count());
        }
    }

    [Theory]
    [InlineData(Es, "orders.csv")]
    [InlineData(Os, "orders.xlsx")]
    public async Task An_export_capped_by_max_rows_stops_there_and_says_it_was_cut_short(string engine, string file)
    {
        var host = await HostAsync(engine);
        var path = Path.Combine(_root, file);

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), "select * from orders") { OutPath = path, MaxRows = 15 },
            CancellationToken.None);

        Assert.Equal(15, export.Rows);
        Assert.True(export.Truncated);
        Assert.Equal([15], Executor.ExecuteAsks);
        Assert.Equal([5], Executor.ContinuationAsks);
    }

    /// <summary>The executor never pages ES|QL or PPL: the server's answer comes back whole, whatever
    /// <c>--max-rows</c> said. The command holds the caller's cap itself and says the result was cut.</summary>
    [Theory]
    [InlineData(Es, "from orders | limit 50")]
    [InlineData(Os, "source=orders | head 50")]
    public async Task Max_rows_trims_a_piped_answer_the_server_returned_whole(string engine, string sql)
    {
        var host = await HostAsync(engine);

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), sql) { MaxRows = 3 }, CancellationToken.None);

        var set = Assert.Single(response.Results);
        Assert.Equal(3, set.RowCount);
        Assert.Equal(3, set.Rows.Count);
        Assert.True(set.Truncated);
        Assert.Equal([3], Executor.ExecuteAsks);
        Assert.Equal(0, Executor.Continuations);
    }

    [Theory]
    [InlineData(Es, "from orders | limit 50", "whole.csv")]
    [InlineData(Es, "from orders | limit 50", "whole.xlsx")]
    [InlineData(Os, "source=orders | head 50", "whole.csv")]
    public async Task Max_rows_trims_a_piped_answer_in_an_export_too(string engine, string sql, string file)
    {
        var host = await HostAsync(engine);
        var path = Path.Combine(_root, file);

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), sql) { OutPath = path, MaxRows = 3 }, CancellationToken.None);

        Assert.Equal(3, export.Rows);
        Assert.True(export.Truncated);
    }

    [Theory]
    [InlineData(Es, "from orders | limit 50")]
    [InlineData(Os, "source=orders | head 50")]
    public async Task An_export_of_a_piped_answer_writes_every_row_it_returned(string engine, string sql)
    {
        var host = await HostAsync(engine);
        var path = Path.Combine(_root, "whole.csv");

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(NameOf(engine), sql) { OutPath = path }, CancellationToken.None);

        Assert.Equal(FakeSearchExecutor.WholeRows, export.Rows);
        Assert.False(export.Truncated);
        Assert.Equal(0, Executor.Continuations);
    }

    /// <summary>The App's status line repeats a server warning (<see cref="QueryResult.Notice"/>); the command
    /// does not — neither as the result's message nor anywhere in what it prints.</summary>
    [Fact]
    public async Task A_server_notice_is_not_repeated_by_the_command()
    {
        var host = await HostAsync(Es);
        Executor.Notice = FakeSearchExecutor.DefaultLimitNotice;

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(NameOf(Es), "from orders"), CancellationToken.None);

        Assert.Null(Assert.Single(response.Results).Message);
        Assert.DoesNotContain("No limit defined", CliRunner.Serialize(response));
        Assert.DoesNotContain("No limit defined", TextTable.Render(response));
    }

    // ---- the forced settings ------------------------------------------------------------------------

    /// <summary><see cref="ExternalAccessPolicy.ForExternalHost"/>'s three settings reach the session these
    /// engines open, whatever the saved record says: read-only on, manual commit off, and a timeout — the preset
    /// when none was set.</summary>
    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task The_session_is_opened_read_only_without_manual_commit_and_with_the_preset_timeout(string engine)
    {
        var host = await HostAsync(engine, c => c with { ReadOnly = false, ManualCommit = true, StatementTimeoutSeconds = SessionPolicy.NoTimeout });

        await host.QueryAsync(new RunRequest(NameOf(engine), "select * from orders"), CancellationToken.None);

        var opened = Assert.Single(Provider.Opened);
        Assert.True(opened.ReadOnly);
        Assert.False(opened.ManualCommit);
        Assert.Equal(SessionPolicy.PresetTimeoutSeconds, opened.StatementTimeoutSeconds);
    }

    [Theory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task A_timeout_the_owner_set_is_kept(string engine)
    {
        var host = await HostAsync(engine, c => c with { StatementTimeoutSeconds = 120 });

        await host.QueryAsync(new RunRequest(NameOf(engine), "select * from orders"), CancellationToken.None);

        var opened = Assert.Single(Provider.Opened);
        Assert.True(opened.ReadOnly);
        Assert.Equal(120, opened.StatementTimeoutSeconds);
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    private static string NameOf(string engine) => $"agent-{engine}";

    /// <summary>Run <paramref name="sql"/>, expect a refusal, and assert it was logged as one and nothing was
    /// sent. Returns the refusal's sentence.</summary>
    private async Task<string> RefusedAsync(string engine, string sql)
    {
        var host = await HostAsync(engine);

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(NameOf(engine), sql), CancellationToken.None));

        var entry = Assert.Single(_log.Entries);
        Assert.False(entry.Success);
        Assert.Equal(sql, entry.SqlText);
        Assert.Equal(failure.Message, entry.ErrorMessage);
        Assert.Equal(QueryOrigin.Cli, entry.Origin);
        Assert.Equal(0, Executor.Calls);
        Assert.Empty(Provider.Opened);   // refused before a connection was even opened

        return failure.Message;
    }

    private async Task<BearingHost> HostAsync(string engine, Func<ConnectionInfo, ConnectionInfo>? edit = null)
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, "Agent", CancellationToken.None);

        var connection = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = NameOf(engine),
            ProviderId = engine,
            Host = "search.internal.example",
            Port = SearchConnectionFactory.DefaultPort,
            Database = "search.internal.example",
            User = "reader",
            ExternalAccess = ExternalAccess.ReadOnly,
        };
        project.Manifest.Connections.Add(edit is null ? connection : edit(connection));
        await store.SaveAsync(project, CancellationToken.None);

        _provider = new FakeSearchProvider(engine);
        _sessions = new ConnectionSessionManager(
            _provider, () => null, idleTimeout: null, clock: null, runSweepTimer: false);

        return new BearingHost(store, directory, _provider, _sessions, _log);
    }
}

/// <summary>
/// A provider registered under a search engine's id — so the host takes that engine's traits and dialect —
/// over a catalog shaped the way <c>SearchMetadataReader</c> builds one: every relation in schema <c>""</c>,
/// indices as tables and an alias as a view, fields flattened to dotted paths, no keys. Records the
/// <see cref="ConnectionInfo"/> each factory is built from, so a test can see the settings the session got.
/// </summary>
internal sealed class FakeSearchProvider(string id) : IDbProvider, IProviderRegistry
{
    /// <summary>An index whose name holds a dash and dots — ordinary for a dated index.</summary>
    public const string DottedIndex = "logs-2026.11.21";

    private const long LogsId = 1;
    private const long OrdersId = 2;
    private const long RecentId = 3;

    public FakeSearchExecutor Executor { get; } = new(ProviderTraits.For(id).Dialect);

    /// <summary>The connection each factory was built from, in order.</summary>
    public List<ConnectionInfo> Opened { get; } = [];

    public string Id => id;
    public string DisplayName => id == SearchProvider.OpenSearchId ? "OpenSearch" : "Elasticsearch";
    public IReadOnlyList<ConnectionField> ConnectionFields => [];
    public bool EnforcesReadOnlyOnServer => false;
    public bool SupportsSessionTimeZone => false;
    public bool SupportsIntegratedAuth => false;
    public bool SupportsEntraToken => false;
    public bool SupportsPasswordCredentials => true;
    public bool SupportsAwsProfile => id == SearchProvider.OpenSearchId;
    public bool SupportsManualCommit => false;
    public bool SupportsTlsModes => true;
    public bool SupportsServerActivity => false;

    public IDbProvider Get(string providerId) => this;
    public IReadOnlyCollection<IDbProvider> All => [this];

    public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
    public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
    {
        Opened.Add(info);

        return new Factory();
    }

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new Metadata();
    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => Executor;

    public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory)
        => throw new NotSupportedException("No transactions on this engine.");

    public IServerActivity CreateServerActivity(IDbConnectionFactory factory) => new NoActivity();

    private static readonly SchemaSnapshot Snapshot = new(
        "search.internal.example",
        [""],
        [
            new TableInfo(LogsId, "", DottedIndex, RelationKind.Table),
            new TableInfo(OrdersId, "", "orders", RelationKind.Table),
            new TableInfo(RecentId, "", "recent-orders", RelationKind.View),
        ],
        [
            new ColumnInfo(LogsId, 1, "@timestamp", "date", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(LogsId, 2, "message", "text", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(OrdersId, 1, "id", "long", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(OrdersId, 2, "customer.name", "keyword", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(OrdersId, 3, "total", "double", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(RecentId, 1, "id", "long", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(RecentId, 2, "customer.name", "keyword", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(RecentId, 3, "total", "double", NotNull: false, IsPrimaryKey: false),
        ],
        [],
        searchPath: [""]);

    private sealed class Factory : IDbConnectionFactory
    {
        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Metadata : IMetadataReader
    {
        public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["search.internal.example"]);

        public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
            => Task.FromResult<ISchemaSnapshot>(Snapshot);

        public Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
            => Task.FromResult(TableDetails.Empty);

        public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoutineInfo>>([]);

        public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct) => Task.FromResult("");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelationSize>>([]);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct) => Task.FromResult("");

        public Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
            => Task.FromResult(DatabaseObjectKinds.Empty);

        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

        public Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
            => Task.FromResult(RoleGrants.NotVisible);

        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);
    }

    private sealed class NoActivity : IServerActivity
    {
        public Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> CancelBackendAsync(int pid, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>
/// The real search executor's contract, without a server: the script is split by the engine's own dialect and
/// each statement answers in its own language's shape. A SQL statement pages a fixed table of
/// <see cref="Items"/> rows, <see cref="PageSize"/> at most per call and never more than the cap asked, with a
/// cursor while rows remain; a piped statement (ES|QL, PPL) comes back whole — <see cref="WholeRows"/> rows,
/// no cursor, not truncated, whatever the cap — and an empty statement (a doubled <c>;</c>) answers nothing.
/// Every call is counted, so a refusal can assert that nothing was sent.
/// </summary>
internal sealed class FakeSearchExecutor(ISqlDialect dialect) : IQueryExecutor
{
    public const int Items = 25;
    public const int PageSize = 10;
    public const int WholeRows = 8;

    /// <summary>The warning Elasticsearch sends with an ES|QL statement that has no <c>LIMIT</c>.</summary>
    public const string DefaultLimitNotice = "No limit defined, adding default limit of [1000]";

    private static readonly IReadOnlyList<ColumnDescriptor> Columns =
    [
        new("id", "long", typeof(long)),
        new("customer.name", "keyword", typeof(string)),
    ];

    public int Executes { get; private set; }
    public int Continuations { get; private set; }
    public int Streams { get; private set; }
    public int Others { get; private set; }
    public int Calls => Executes + Continuations + Streams + Others;

    /// <summary>The script each first-page call was sent, in order.</summary>
    public List<string> Sent { get; } = [];

    /// <summary>The row cap each first-page call was given, in order (null = none).</summary>
    public List<int?> ExecuteAsks { get; } = [];

    /// <summary>The row count each continuation call asked for, in order.</summary>
    public List<int> ContinuationAsks { get; } = [];

    /// <summary>Set to have every piped answer carry this server warning as its <see cref="QueryResult.Notice"/>.</summary>
    public string? Notice { get; set; }

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        Executes++;
        Sent.Add(sql);
        ExecuteAsks.Add(options.MaxRows);

        var results = new List<QueryResult>();
        var index = 0;
        foreach (var span in dialect.SplitStatements(sql))
        {
            var text = span.Text.Trim().TrimEnd(';').Trim();
            if (text.Length > 0)
            {
                results.Add(IsPiped(text) ? Whole(index) : Page(0, options.MaxRows ?? PageSize, index));
            }

            index++;
        }

        return Task.FromResult<IReadOnlyList<QueryResult>>(results);
    }

    public Task<QueryResult> ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken ct)
    {
        Continuations++;
        ContinuationAsks.Add(maxRows);

        return Task.FromResult(Page(int.Parse(continuation["cursor:".Length..]), maxRows, statementIndex: 0));
    }

    public IAsyncEnumerable<RowBatch> StreamRowsAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        Streams++;

        return Empty(ct);
    }

    public Task<QueryResult> ExecutePageAsync(string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        Others++;

        throw new NotSupportedException();
    }

    public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        Others++;

        return Task.FromResult<long?>(null);
    }

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        Others++;

        throw new NotSupportedException();
    }

    /// <summary>Piped by the engine's own reading of the statement — a top-level <c>|</c> or a piped lead word
    /// (<c>FROM</c>, <c>ROW</c>, <c>source=</c>, <c>search</c>).</summary>
    private bool IsPiped(string text)
    {
        var engine = dialect.Id == SearchProvider.OpenSearchId ? SearchEngine.OpenSearch : SearchEngine.Elasticsearch;

        return SearchScan.Statements(engine, text).FirstOrDefault()?.Language == SearchLanguage.Piped;
    }

    private QueryResult Whole(int statementIndex)
    {
        var rows = Enumerable.Range(0, WholeRows)
            .Select(i => new object?[] { (long)i, $"whole-{i:D3}" })
            .ToList();

        return new QueryResult(Columns, rows, rows.Count, TimeSpan.FromMilliseconds(1), null, null, Truncated: false)
        {
            StatementIndex = statementIndex,
            Notice = Notice,
        };
    }

    private static QueryResult Page(int offset, int maxRows, int statementIndex)
    {
        var take = Math.Min(Math.Min(Math.Max(1, maxRows), PageSize), Items - offset);
        var rows = Enumerable.Range(offset, take)
            .Select(i => new object?[] { (long)i, $"customer-{i:D3}" })
            .ToList();
        var end = offset + take;

        return new QueryResult(Columns, rows, rows.Count, TimeSpan.FromMilliseconds(1), null, null, Truncated: end < Items)
        {
            StatementIndex = statementIndex,
            Continuation = end < Items ? $"cursor:{end}" : null,
        };
    }

    private static async IAsyncEnumerable<RowBatch> Empty([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;

        yield break;
    }
}
