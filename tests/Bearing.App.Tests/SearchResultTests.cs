using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Data.Search;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What the App does with an Elasticsearch or OpenSearch result (SC19), with no server: the grid is locked with the
/// engine's own sentence, a SQL result that came back with a server cursor pages only through
/// <see cref="IQueryExecutor.ExecuteContinuationAsync"/>, manual commit is never in force, and a result's
/// <see cref="QueryResult.Notice"/> reaches the run's status line.
/// <para>
/// Each claim is driven by a trait or flag that already existed for DynamoDB (<see cref="DynamoDbResultTests"/>,
/// <see cref="ContinuationPagingTests"/>); these pin that the two search engines answer them the same way, so a
/// trait set wrongly in <see cref="ProviderTraits"/> or on <see cref="SearchProvider"/> shows up here rather than as
/// an editable grid or a re-run statement.
/// </para>
/// </summary>
public class SearchResultTests : IDisposable
{
    private const int Page = 10;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-search-results", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    public static TheoryData<string> Engines => new() { SearchProvider.ElasticsearchId, SearchProvider.OpenSearchId };

    // ---- the editing lock ----------------------------------------------------------------------------

    private const long LogsId = 1;

    /// <summary>One index whose fields the reader mapped. No search field is a key (the reader sets
    /// <c>IsPrimaryKey</c> false), so a key column is given here on purpose: the lock must be the traits' doing,
    /// not the resolver's "no key" — the same control <see cref="DynamoDbResultTests"/> uses.</summary>
    private static readonly SchemaSnapshot Logs = new(
        "localhost",
        [""],
        [new TableInfo(LogsId, "", "logs", RelationKind.Table)],
        [
            new ColumnInfo(LogsId, 1, "id", "keyword", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(LogsId, 2, "message", "text", NotNull: false, IsPrimaryKey: false),
        ],
        [],
        searchPath: [""]);

    private static QueryResult LogsResult()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "keyword", typeof(string), LogsId, 1),
            new ColumnDescriptor("message", "text", typeof(string), LogsId, 2),
        };
        return new QueryResult(columns, [new object?[] { "a", "hello" }], 1, TimeSpan.Zero, null, null, Truncated: false);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void A_result_is_locked_with_the_engines_own_sentence(string providerId)
    {
        var traits = ProviderTraits.For(providerId);

        var set = Assert.Single(ResultSetBuilder.BuildResultSets([LogsResult()], "select * from logs", Logs, traits));

        Assert.Null(set.EditTarget);
        Assert.False(set.IsEditable);
        Assert.NotNull(traits.GridEditUnavailable);
        Assert.Equal(traits.GridEditUnavailable, set.LockReason);
        var engine = providerId == SearchProvider.ElasticsearchId ? "Elasticsearch" : "OpenSearch";
        Assert.Equal($"Read-only — editing isn't available on {engine} connections yet.", set.LockReason);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void The_lock_holds_with_no_schema_snapshot(string providerId)
    {
        var traits = ProviderTraits.For(providerId);

        var set = Assert.Single(ResultSetBuilder.BuildResultSets([LogsResult()], "from logs", snapshot: null, traits));

        Assert.Equal(traits.GridEditUnavailable, set.LockReason);
        Assert.False(set.IsEditable);
    }

    // ---- paging by the server's cursor ----------------------------------------------------------------

    private sealed record Harness(
        ExecutionViewModel Exec,
        ResultSetViewModel Result,
        Func<string> Status);

    /// <summary>A tab on <paramref name="providerId"/> that has run one statement through
    /// <paramref name="executor"/>. The provider is the fake under the search engine's id, so the run takes the
    /// engine's real traits (<see cref="ProviderTraits.For(ConnectionInfo)"/> reads the id) while the rows are
    /// scripted.</summary>
    private async Task<Harness> RunOnce(string providerId, IQueryExecutor executor, string sql = "select * from logs")
    {
        Directory.CreateDirectory(_root);
        var settings = SettingsService.InMemory(new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            ResultPageSize = Page,
            ResultFetchAllMaxRows = AppSettings.Defaults.ResultFetchAllMaxRows,
        });
        var ctx = new WorkspaceContext(
            new FakeProvider { Id = providerId, Executor = executor, SupportsManualCommit = false },
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: settings);
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "search", ProviderId = providerId, Host = "localhost", Port = 9200 };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };

        var status = "";
        var tab = new EditorTabViewModel("logs") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = s => status = s;

        var exec = new ExecutionViewModel(ctx, new FakeDialogs());
        await exec.ExecuteAsync(sql);

        return new Harness(exec, tab.LastResult!, () => status);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_sql_result_with_a_cursor_pages_only_through_the_continuation(string providerId)
    {
        var executor = new ContinuationPagingTests.TokenExecutor { TotalRows = 25 };
        var h = await RunOnce(providerId, executor);

        Assert.Equal(Page, h.Result.Loaded);
        Assert.Equal("t1", h.Result.Continuation);
        Assert.True(h.Result.HasMore);
        Assert.True(h.Result.IsPageable);
        Assert.False(h.Result.CanCount);
        Assert.Equal(ProviderTraits.For(providerId).GridEditUnavailable, h.Result.LockReason);

        await h.Exec.LoadMoreAsync(h.Result);

        Assert.Equal(2 * Page, h.Result.Loaded);
        Assert.Equal(new[] { "t1" }, executor.Continued);
        Assert.Equal(1, executor.ExecuteCalls);   // the statement was sent once, never re-run
        Assert.Equal(0, executor.OffsetCalls);    // no ExecutePageAsync, no StreamRowsAsync

        Assert.True(await h.Exec.FetchAllAsync(h.Result));

        Assert.Equal(25, h.Result.Loaded);
        Assert.Equal(new[] { "t1", "t2" }, executor.Continued);
        Assert.False(h.Result.HasMore);
        Assert.Equal(1, executor.ExecuteCalls);
        Assert.Equal(0, executor.OffsetCalls);
        Assert.False(h.Result.CanCount);
    }

    // ---- manual commit is never in force ----------------------------------------------------------------

    /// <summary>Against the real registry, so the answer is <see cref="SearchProvider.SupportsManualCommit"/>'s and
    /// not a fake's: a hand-edited <c>ManualCommit: true</c> and a session override both come out off.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void Manual_commit_is_never_effective_whatever_the_record_or_the_override_says(string providerId)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new Bearing.Data.ProviderRegistry(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "search",
            ProviderId = providerId,
            Host = "localhost",
            Port = 9200,
            ManualCommit = true,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        Assert.False(ctx.SupportsManualCommit(conn));
        Assert.False(ctx.EffectiveConnection(tab)!.ManualCommit);

        ctx.CommitModes.Set(conn.Id, true);
        Assert.False(ctx.EffectiveConnection(tab)!.ManualCommit);
    }

    // ---- the server's notice in the status line ----------------------------------------------------------

    private const string Partial = "Elasticsearch marked this answer as partial (is_partial), so rows may be missing.";

    private static QueryResult Rows(string? notice = null, string? message = null, string? continuation = null)
        => new(
            [new ColumnDescriptor("n", "long", typeof(long))],
            [new object?[] { 1L }],
            1, TimeSpan.FromMilliseconds(5), message, null, Truncated: continuation is not null)
        {
            Notice = notice,
            Continuation = continuation,
        };

    [Fact]
    public void A_results_notice_follows_the_timing()
    {
        var status = ResultSetBuilder.DescribeResults([Rows(Partial)], TimeSpan.FromMilliseconds(88));

        Assert.Equal($"Done · 88 ms — {Partial}", status);
    }

    [Fact]
    public void A_run_without_a_notice_is_unchanged()
    {
        Assert.Equal("Done · 88 ms", ResultSetBuilder.DescribeResults([Rows()], TimeSpan.FromMilliseconds(88)));
        // Blank is no notice.
        Assert.Equal("Done · 88 ms", ResultSetBuilder.DescribeResults([Rows("   ")], TimeSpan.FromMilliseconds(88)));
        Assert.Equal("2 result sets · 88 ms",
            ResultSetBuilder.DescribeResults([Rows(), Rows()], TimeSpan.FromMilliseconds(88)));
    }

    [Fact]
    public void The_first_set_with_a_notice_speaks_for_the_run()
    {
        var status = ResultSetBuilder.DescribeResults(
            [Rows(), Rows("  first  "), Rows("second")], TimeSpan.FromMilliseconds(88));

        Assert.Equal("3 result sets · 88 ms — first", status);
    }

    /// <summary>Both sentences, Bearing's own first: the budget notice explains the page, the server's the answer.</summary>
    [Fact]
    public void A_budget_notice_and_a_server_notice_are_both_said_budget_first()
    {
        var budget = "This page stopped after 10 requests.";

        var status = ResultSetBuilder.DescribeResults(
            [Rows(Partial, message: budget, continuation: "t1")], TimeSpan.FromMilliseconds(88));

        Assert.Equal(
            $"Done · 88 ms — {budget} {ContinuationPaging.LoadMoreContinues} — {Partial}",
            status);
    }

    /// <summary>A failed run is described by its error; a notice on another set does not ride on it.</summary>
    [Fact]
    public void An_error_is_not_followed_by_a_notice()
    {
        var failed = new QueryResult([], [], 0, TimeSpan.Zero, null, new QueryError("boom", "parsing_exception", null), false);

        var status = ResultSetBuilder.DescribeResults([Rows(Partial), failed]);

        Assert.DoesNotContain(Partial, status);
        Assert.Contains("boom", status);
    }

    /// <summary>End to end through the run: the status bar the user reads carries the notice.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_run_status_carries_the_notice(string providerId)
    {
        var executor = new NoticeExecutor(Partial);
        var h = await RunOnce(providerId, executor, "from logs | limit 1");

        Assert.Contains($"— {Partial}", h.Status());
        Assert.StartsWith("search · Done · ", h.Status());
        Assert.Equal(1, h.Result.Loaded);
        Assert.Equal(0, executor.Streamed);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_run_without_a_notice_reads_as_before(string providerId)
    {
        var h = await RunOnce(providerId, new NoticeExecutor(null), "from logs | limit 1");

        Assert.Matches(@"^search · Done · \d+ ms$", h.Status());
    }

    /// <summary>One result with rows and the given notice, no cursor; every paging call is a failure of the test.</summary>
    private sealed class NoticeExecutor(string? notice) : IQueryExecutor
    {
        public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<QueryResult>>([Rows(notice) with { StatementIndex = 0 }]);

        public Task<QueryResult> ExecutePageAsync(string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
            => throw new InvalidOperationException("no offset page on a search engine");

        public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
            string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Streamed++;
            await Task.CompletedTask;
            yield break;
        }

        /// <summary>Calls to <see cref="StreamRowsAsync"/> — none expected; a run reads through ExecuteAsync.</summary>
        public int Streamed { get; private set; }

        public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
            => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
