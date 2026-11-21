using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bearing.Core.Data;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The Elasticsearch / OpenSearch executor against a hand-rolled fake API that records every request (§4.1), SC10:
/// a batch runs statement by statement to the endpoint each language selects; a cursor becomes a continuation and
/// continuing sends the cursor alone (the fake counts statement re-sends — there must be none); a rejected cursor is
/// said, never re-run; ES|QL, PPL and cursor-less SQL answers come back whole; a server warning reaches
/// <see cref="QueryResult.Notice"/>, never <see cref="QueryResult.Message"/>; the statement timeout is ours and
/// the caller's cancel is not. The live proof is <c>SearchLiveTests</c>.
/// </summary>
public class SearchQueryExecutorTests
{
    private const string EsSql = "/_sql?format=json";
    private const string EsQl = "/_query?format=json";
    private const string OsSql = "/_plugins/_sql?format=jdbc";
    private const string Ppl = "/_plugins/_ppl?format=jdbc";

    // ---- the batch -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mixed_elasticsearch_script_sends_each_statement_alone_to_its_languages_endpoint()
    {
        var api = new FakeSearchApi(r => r.Path == EsQl ? Piped(SearchEngine.Elasticsearch, 1) : SqlFirst(SearchEngine.Elasticsearch, 1));
        var script = "SELECT a FROM logs;\n-- the piped one\nFROM logs | LIMIT 5;\nselect b from \"t;x\";";

        var results = await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync(script, new QueryOptions { MaxRows = 50 }, CancellationToken.None);

        Assert.Equal([EsSql, EsQl, EsSql], api.Requests.Select(r => r.Path));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal(["SELECT a FROM logs", "FROM logs | LIMIT 5", "select b from \"t;x\""], api.Requests.Select(r => r.Query));
        Assert.Equal([0, 1, 2], results.Select(r => r.StatementIndex));
        Assert.All(results, r => Assert.True(r.Success));
        // The SQL endpoint is asked for the caller's page; the piped one carries the query alone.
        Assert.Equal(50, api.Requests[0].Field("fetch_size")!.Value.GetInt32());
        Assert.Null(api.Requests[1].Field("fetch_size"));
    }

    [Fact]
    public async Task A_mixed_opensearch_script_sends_ppl_and_sql_to_their_own_endpoints()
    {
        var api = new FakeSearchApi(r => r.Path == Ppl ? Piped(SearchEngine.OpenSearch, 1) : SqlFirst(SearchEngine.OpenSearch, 1));

        var results = await Executor(api, SearchEngine.OpenSearch)
            .ExecuteAsync("source=logs | head 5;\nSELECT * FROM logs;\n| makeresults count=1", new QueryOptions(), CancellationToken.None);

        Assert.Equal([Ppl, OsSql, Ppl], api.Requests.Select(r => r.Path));
        Assert.Equal(["source=logs | head 5", "SELECT * FROM logs", "| makeresults count=1"], api.Requests.Select(r => r.Query));
        Assert.Equal([0, 1, 2], results.Select(r => r.StatementIndex));
    }

    [Fact]
    public async Task A_failure_in_the_second_statement_is_its_result_and_the_third_is_never_sent()
    {
        var api = new FakeSearchApi(r => r.Query == "SELECT nope FROM logs"
            ? Error(400, """{"error":{"type":"verification_exception","reason":"line 1:8: Unknown column [nope]"},"status":400}""")
            : SqlFirst(SearchEngine.Elasticsearch, 2));

        var results = await Executor(api, SearchEngine.Elasticsearch).ExecuteAsync(
            "SELECT a FROM logs; SELECT nope FROM logs; SELECT c FROM logs", new QueryOptions(), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.Equal(2, results[0].Rows.Count);
        Assert.False(results[1].Success);
        Assert.Equal(1, results[1].StatementIndex);
        Assert.Empty(results[1].Columns);
        Assert.Equal(2, api.Requests.Count);
        Assert.DoesNotContain(api.Requests, r => r.Body.Contains("SELECT c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bare_semicolon_sends_nothing_but_keeps_its_statement_number()
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 1));

        var results = await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync("SELECT 1;;SELECT 2", new QueryOptions(), CancellationToken.None);

        Assert.Equal([0, 2], results.Select(r => r.StatementIndex));
        Assert.Equal(["SELECT 1", "SELECT 2"], api.Requests.Select(r => r.Query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  -- only a comment\n")]
    [InlineData(";")]
    public async Task A_script_with_no_statement_is_nothing_to_run_and_sends_nothing(string script)
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 1));

        var result = Assert.Single(await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync(script, new QueryOptions(), CancellationToken.None));

        Assert.Equal(SearchQueryExecutor.NothingToRun, result.Error!.Message);
        Assert.Empty(api.Requests);
    }

    // ---- cursor and continuation -----------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch))]
    [InlineData(nameof(SearchEngine.OpenSearch))]
    public async Task A_sql_cursor_continues_by_sending_the_cursor_alone_and_never_the_statement(string engineName)
    {
        var engine = Enum.Parse<SearchEngine>(engineName);
        const string statement = "SELECT n, name FROM logs WHERE n > 0";
        var path = engine == SearchEngine.Elasticsearch ? EsSql : OsSql;
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(engine, 2, cursor: "c1")
            : r.Cursor == "c1" ? SqlLater(engine, [[3, "\"c\""], [4, "\"d\""]], cursor: "c2") : SqlLater(engine, [[5, "\"e\""]], cursor: null));
        var executor = Executor(api, engine);

        var first = Assert.Single(await executor.ExecuteAsync(statement, new QueryOptions { MaxRows = 2 }, CancellationToken.None));

        Assert.True(first.Truncated);
        Assert.NotNull(first.Continuation);
        Assert.DoesNotContain("c1", first.Continuation!, StringComparison.Ordinal); // opaque, not the bare cursor
        Assert.Equal(2, api.Requests[0].Field("fetch_size")!.Value.GetInt32());

        var second = await executor.ExecuteContinuationAsync(first.Continuation, 2, CancellationToken.None);
        var third = await executor.ExecuteContinuationAsync(second.Continuation!, 2, CancellationToken.None);

        // Continuing sent the cursor alone, to the endpoint that issued it: 0 statement re-sends.
        Assert.Equal([path, path, path], api.Requests.Select(r => r.Path));
        // The body is JSON, so the statement is compared as the parsed query (its ">" travels escaped).
        Assert.Equal(1, api.Requests.Count(r => r.Query == statement));
        Assert.Equal(0, api.Requests.Skip(1).Count(r => r.Query is not null || r.Body.Contains("logs", StringComparison.Ordinal)));
        Assert.Equal(["""{"cursor":"c1"}""", """{"cursor":"c2"}"""], api.Requests.Skip(1).Select(r => r.Body));

        // The rows convert against the first page's layout.
        Assert.Equal(["n", "name"], second.Columns.Select(c => c.Name));
        Assert.Equal(typeof(long), second.Columns[0].ClrType);
        Assert.Equal([3L, 4L], second.Rows.Select(r => r[0]));
        Assert.Equal(["c", "d"], second.Rows.Select(r => r[1]));
        Assert.True(second.Truncated);
        Assert.Equal(0, second.StatementIndex);

        // The last page clears the continuation.
        Assert.Equal([5L], third.Rows.Select(r => r[0]));
        Assert.Null(third.Continuation);
        Assert.False(third.Truncated);
        Assert.Null(third.Message);
    }

    [Fact]
    public async Task A_rejected_cursor_says_it_was_not_rerun_with_the_servers_reason_and_sends_one_request()
    {
        const string statement = "SELECT n FROM logs";
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.Elasticsearch, 1, cursor: "c1secret")
            : Error(404, """{"error":{"type":"search_context_missing_exception","reason":"No search context found for id [c1secret]"},"status":404}"""));
        var executor = Executor(api, SearchEngine.Elasticsearch);
        var first = Assert.Single(await executor.ExecuteAsync(statement, new QueryOptions { MaxRows = 1 }, CancellationToken.None));
        api.Requests.Clear();

        var result = await executor.ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("Elasticsearch no longer accepts this result's cursor (HTTP 404)", result.Error!.Message);
        Assert.Contains("5 minutes", result.Error.Message);
        Assert.Contains("The statement was not run again", result.Error.Message);
        Assert.Contains("Elasticsearch said: No search context found for id [[cursor]]", result.Error.Message);
        Assert.DoesNotContain("c1secret", result.Error.Message); // the cursor is never shown (§1.3)
        Assert.Equal("search_context_missing_exception", result.Error.SqlState);
        Assert.Equal(0, result.StatementIndex);
        var sent = Assert.Single(api.Requests);
        Assert.Null(sent.Query);
        Assert.Equal("c1secret", sent.Cursor);
    }

    [Fact]
    public async Task An_opensearch_rejected_cursor_names_the_clusters_keep_alive()
    {
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.OpenSearch, 1, cursor: "c1")
            : Error(500, """{"error":{"type":"IllegalArgumentException","reason":"Invalid cursor"},"status":500}"""));
        var executor = Executor(api, SearchEngine.OpenSearch);
        var first = Assert.Single(await executor.ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));

        var result = await executor.ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.Contains("plugins.sql.cursor.keep_alive", result.Error!.Message);
        Assert.EndsWith("OpenSearch said: Invalid cursor", result.Error.Message);
        Assert.Equal(2, api.Requests.Count);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    [InlineData("search1.!!!")]
    public async Task A_foreign_or_damaged_token_is_an_error_and_sends_nothing(string token)
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 1));

        var result = await Executor(api, SearchEngine.Elasticsearch).ExecuteContinuationAsync(token, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new SearchContinuationException().Message, result.Error!.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_token_another_engines_connection_issued_is_refused_and_sends_nothing()
    {
        var openSearch = new FakeSearchApi(_ => SqlFirst(SearchEngine.OpenSearch, 1, cursor: "c1"));
        var first = Assert.Single(await Executor(openSearch, SearchEngine.OpenSearch)
            .ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));
        var elastic = new FakeSearchApi(_ => SqlLater(SearchEngine.Elasticsearch, [[2, "\"b\""]], null));

        var result = await Executor(elastic, SearchEngine.Elasticsearch)
            .ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(elastic.Requests);
    }

    // ---- whole answers ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM logs | LIMIT 25")]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=logs | head 25")]
    [InlineData(nameof(SearchEngine.OpenSearch), "SELECT n, count(*) FROM logs GROUP BY n")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "SELECT n FROM logs")]
    public async Task An_answer_without_a_cursor_is_returned_whole_even_past_max_rows(string engineName, string statement)
    {
        var engine = Enum.Parse<SearchEngine>(engineName);
        var api = new FakeSearchApi(r => r.Path is EsQl or Ppl ? Piped(engine, 25) : SqlFirst(engine, 25, cursor: null));

        var result = Assert.Single(await Executor(api, engine)
            .ExecuteAsync(statement, new QueryOptions { MaxRows = 10 }, CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(25, result.Rows.Count);
        Assert.Equal(25, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Null(result.Continuation);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task A_cursor_on_a_piped_answer_is_not_a_continuation()
    {
        // ES|QL has no paging; a "cursor" field there is not one.
        var api = new FakeSearchApi(_ => Json("""{"columns":[{"name":"n","type":"long"}],"values":[[1]],"cursor":"x"}"""));

        var result = Assert.Single(await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync("FROM logs", new QueryOptions { MaxRows = 1 }, CancellationToken.None));

        Assert.Null(result.Continuation);
        Assert.False(result.Truncated);
    }

    // ---- notices ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_warning_header_reaches_notice_and_never_message()
    {
        var api = new FakeSearchApi(_ => Json(
            """{"columns":[{"name":"n","type":"long"}],"values":[[1]]}""",
            "299 Elasticsearch-9.1.0-abc123 \"No limit defined, adding default limit of [1000]\""));

        var result = Assert.Single(await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync("FROM logs", new QueryOptions(), CancellationToken.None));

        Assert.Equal("No limit defined, adding default limit of [1000]", result.Notice);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task A_warning_on_a_continuing_sql_result_still_leaves_message_null()
    {
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.Elasticsearch, 1, cursor: "c1", warning: "299 Elasticsearch-9 \"first\"")
            : SqlLater(SearchEngine.Elasticsearch, [[2, "\"b\""]], null, warning: "299 Elasticsearch-9 \"later\""));
        var executor = Executor(api, SearchEngine.Elasticsearch);

        var first = Assert.Single(await executor.ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));
        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.Equal("first", first.Notice);
        Assert.Null(first.Message);
        Assert.Equal("later", second.Notice);
        Assert.Null(second.Message);
    }

    [Fact]
    public async Task No_warning_means_no_notice()
    {
        var api = new FakeSearchApi(_ => Piped(SearchEngine.OpenSearch, 2));

        var result = Assert.Single(await Executor(api, SearchEngine.OpenSearch)
            .ExecuteAsync("source=logs", new QueryOptions(), CancellationToken.None));

        Assert.Null(result.Notice);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task An_answer_with_no_columns_says_what_the_server_answered()
    {
        var api = new FakeSearchApi(_ => Json("""{"schema":[],"datarows":[]}"""));

        var result = Assert.Single(await Executor(api, SearchEngine.OpenSearch)
            .ExecuteAsync("DELETE FROM logs WHERE n = 1", new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success);
        Assert.Empty(result.Columns);
        Assert.Equal(SearchQueryExecutor.NoColumnsSentence(SearchEngine.OpenSearch, "SQL", 0), result.Message);
        Assert.Equal("SQL statement sent; OpenSearch answered with no columns and no rows.", result.Message);
    }

    // ---- values and errors -----------------------------------------------------------------------------

    [Fact]
    public async Task A_later_page_value_that_does_not_fit_fails_that_page_naming_the_column()
    {
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.Elasticsearch, 1, cursor: "c1")
            : SqlLater(SearchEngine.Elasticsearch, [["\"not a number\"", "\"b\""]], null));
        var executor = Executor(api, SearchEngine.Elasticsearch);
        var first = Assert.Single(await executor.ExecuteAsync("SELECT n, name FROM logs", new QueryOptions(), CancellationToken.None));

        var result = await executor.ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Column \"n\" (long)", result.Error!.Message);
        Assert.Contains("not a number", result.Error.Message);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM logs | KEEP nope", "ES|QL: ")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "SELECT nope FROM logs", "SQL: ")]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=logs | fields nope", "PPL: ")]
    [InlineData(nameof(SearchEngine.OpenSearch), "SELECT nope FROM logs", "SQL: ")]
    public async Task A_server_error_is_led_by_the_language_it_was_sent_as(string engineName, string statement, string lead)
    {
        var engine = Enum.Parse<SearchEngine>(engineName);
        var api = new FakeSearchApi(_ => Error(
            400, """{"error":{"type":"verification_exception","reason":"line 1:8: Unknown column [nope]"},"status":400}"""));

        var result = Assert.Single(await Executor(api, engine).ExecuteAsync(statement, new QueryOptions(), CancellationToken.None));

        Assert.StartsWith(lead + "line 1:8: Unknown column [nope]", result.Error!.Message);
        Assert.Equal(8, result.Error.Position);
        Assert.Equal(DbErrorKind.SyntaxOrShape, SearchErrors.ClassifyState(result.Error.SqlState));
    }

    [Fact]
    public async Task An_error_position_counts_from_the_start_of_the_script()
    {
        // The second statement starts at offset 10; the server's 1:18 is in the text that was sent.
        var api = new FakeSearchApi(r => r.Path == EsQl
            ? Error(400, """{"error":{"type":"verification_exception","reason":"line 1:18: Unknown column [nope]"},"status":400}""")
            : SqlFirst(SearchEngine.Elasticsearch, 1));

        var results = await Executor(api, SearchEngine.Elasticsearch)
            .ExecuteAsync("SELECT 1;\nFROM logs | KEEP nope", new QueryOptions(), CancellationToken.None);

        Assert.Equal(10 + 18, results[1].Error!.Position);
        Assert.Equal('n', "SELECT 1;\nFROM logs | KEEP nope"[results[1].Error!.Position!.Value - 1]);
    }

    // ---- timeout and cancel ----------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch))]
    [InlineData(nameof(SearchEngine.OpenSearch))]
    public async Task A_one_second_statement_timeout_cancels_the_request_with_the_timeout_sentence(string engineName)
    {
        var engine = Enum.Parse<SearchEngine>(engineName);
        var api = new FakeSearchApi(_ => SqlFirst(engine, 1)) { Delay = Timeout.InfiniteTimeSpan };

        var started = DateTime.UtcNow;
        var result = Assert.Single(await Executor(api, engine, timeoutSeconds: 1)
            .ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.Equal(SearchErrors.TimeoutSentence(engine, 1), result.Error!.Message);
        Assert.Equal(SearchErrors.TimeoutCode, result.Error.SqlState);
        Assert.Equal(DbErrorKind.Canceled, SearchErrors.ClassifyState(result.Error.SqlState));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.True(api.Canceled);
    }

    [Fact]
    public async Task A_continuation_is_held_to_the_statement_timeout_too()
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 1, cursor: "c1"));
        var executor = Executor(api, SearchEngine.Elasticsearch, timeoutSeconds: 1);
        var first = Assert.Single(await executor.ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));
        api.Delay = Timeout.InfiniteTimeSpan;

        var result = await executor.ExecuteContinuationAsync(first.Continuation!, 1, CancellationToken.None);

        Assert.Equal(SearchErrors.TimeoutCode, result.Error!.SqlState);
        Assert.Equal(0, result.StatementIndex);
    }

    [Fact]
    public async Task The_callers_own_cancel_is_thrown_as_a_cancel_not_worded_as_the_timeout()
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 1)) { Delay = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(api, SearchEngine.Elasticsearch, timeoutSeconds: 30)
            .ExecuteAsync("SELECT n FROM logs", new QueryOptions(), cts.Token));

        var error = SearchErrors.ToQueryError(thrown, SearchEngine.Elasticsearch, 30);
        Assert.Equal(SearchErrors.CanceledCode, error.SqlState);
        Assert.DoesNotContain("statement timeout", error.Message);
        Assert.Equal(DbErrorKind.Canceled, SearchErrors.Classify(thrown));
    }

    [Fact]
    public async Task A_servers_own_timeout_error_is_the_servers_not_ours()
    {
        var api = new FakeSearchApi(_ => Error(
            504, """{"error":{"type":"timeout_exception","reason":"Timed out waiting for the search"},"status":504}"""));

        var result = Assert.Single(await Executor(api, SearchEngine.Elasticsearch, timeoutSeconds: 30)
            .ExecuteAsync("SELECT n FROM logs", new QueryOptions(), CancellationToken.None));

        Assert.Equal("SQL: Timed out waiting for the search", result.Error!.Message);
        Assert.Equal("timeout_exception", result.Error.SqlState);
        Assert.NotEqual(SearchErrors.TimeoutCode, result.Error.SqlState);
    }

    // ---- streaming -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stream_follows_the_cursor_to_the_end_with_the_columns_on_every_batch()
    {
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.Elasticsearch, 3, cursor: "c1")
            : r.Cursor == "c1"
                ? SqlLater(SearchEngine.Elasticsearch, [[3, "\"d\""], [4, "\"e\""], [5, "\"f\""]], "c2")
                : SqlLater(SearchEngine.Elasticsearch, [[6, "\"g\""], [7, "\"h\""]], null));

        var batches = await Stream(Executor(api, SearchEngine.Elasticsearch), "SELECT n, name FROM logs",
            new QueryOptions { MaxRows = null, BatchRows = 4 });

        Assert.Equal([4, 4, 0], batches.Select(b => b.Rows.Count));
        Assert.All(batches, b => Assert.Equal(["n", "name"], b.Columns.Select(c => c.Name)));
        Assert.False(batches[^1].Truncated);
        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L, 6L, 7L], batches.SelectMany(b => b.Rows).Select(r => r[0]));
        Assert.Equal(1, api.Requests.Count(r => r.Query is not null));
        Assert.Equal(["c1", "c2"], api.Requests.Where(r => r.Cursor is not null).Select(r => r.Cursor));
    }

    [Fact]
    public async Task A_stream_holds_its_cap_exactly_and_says_truncated_on_the_tail()
    {
        var api = new FakeSearchApi(r => r.Query is not null
            ? SqlFirst(SearchEngine.Elasticsearch, 3, cursor: "c1")
            : SqlLater(SearchEngine.Elasticsearch, [[3, "\"d\""], [4, "\"e\""], [5, "\"f\""]], "c2"));

        var batches = await Stream(Executor(api, SearchEngine.Elasticsearch), "SELECT n, name FROM logs",
            new QueryOptions { MaxRows = 5, BatchRows = 4 });

        Assert.Equal(5, batches.Sum(b => b.Rows.Count));
        Assert.True(batches[^1].Truncated);
        Assert.All(batches.Take(batches.Count - 1), b => Assert.False(b.Truncated));
        Assert.Equal(2, api.Requests.Count); // the cap was reached; the cursor is left to expire
    }

    [Fact]
    public async Task A_stream_of_a_piped_answer_trims_to_its_cap()
    {
        var api = new FakeSearchApi(_ => Piped(SearchEngine.OpenSearch, 7));

        var batches = await Stream(Executor(api, SearchEngine.OpenSearch), "source=logs",
            new QueryOptions { MaxRows = 5, BatchRows = 10 });

        var tail = Assert.Single(batches);
        Assert.Equal(5, tail.Rows.Count);
        Assert.True(tail.Truncated);
    }

    [Fact]
    public async Task A_stream_of_an_empty_result_still_yields_a_tail_with_its_columns()
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.Elasticsearch, 0));

        var batches = await Stream(Executor(api, SearchEngine.Elasticsearch), "SELECT n, name FROM logs", new QueryOptions());

        var tail = Assert.Single(batches);
        Assert.Empty(tail.Rows);
        Assert.Equal(["n", "name"], tail.Columns.Select(c => c.Name));
        Assert.False(tail.Truncated);
    }

    [Fact]
    public async Task A_stream_throws_a_server_error_led_by_its_language()
    {
        var api = new FakeSearchApi(_ => Error(400, """{"error":{"type":"parsing_exception","reason":"line 1:1: bad"},"status":400}"""));

        var thrown = await Assert.ThrowsAsync<SearchServerException>(
            () => Stream(Executor(api, SearchEngine.Elasticsearch), "FROM logs |", new QueryOptions()));

        Assert.StartsWith("ES|QL: line 1:1: bad", thrown.Message);
    }

    [Fact]
    public async Task A_stream_throws_the_timeout_naming_it()
    {
        var api = new FakeSearchApi(_ => SqlFirst(SearchEngine.OpenSearch, 1)) { Delay = Timeout.InfiniteTimeSpan };

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => Stream(Executor(api, SearchEngine.OpenSearch, timeoutSeconds: 1), "SELECT n FROM logs", new QueryOptions()));

        Assert.Equal(SearchErrors.TimeoutSentence(SearchEngine.OpenSearch, 1), thrown.Message);
    }

    // ---- what these engines do not offer ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch))]
    [InlineData(nameof(SearchEngine.OpenSearch))]
    public async Task Offset_paging_counting_generated_writes_and_variables_are_not_available_and_send_nothing(string engineName)
    {
        var engine = Enum.Parse<SearchEngine>(engineName);
        var api = new FakeSearchApi(_ => SqlFirst(engine, 1));
        IQueryExecutor executor = Executor(api, engine);
        QueryParameter[] parameters = [new("id", "1", QueryParameterKind.Text)];

        var page = await executor.ExecutePageAsync("SELECT n FROM logs LIMIT 10", CancellationToken.None);
        var pageWithValues = await executor.ExecutePageAsync("SELECT n FROM logs WHERE n = @bearing_id", parameters, CancellationToken.None);
        var count = await executor.CountAsync("SELECT count(*) FROM logs", CancellationToken.None);
        var writes = await executor.ExecuteWriteAsync([new SqlWriteCommand("update logs set n = 1", [])], CancellationToken.None);
        var withVariables = Assert.Single(await executor.ExecuteAsync(
            "SELECT n FROM logs WHERE n = @bearing_id", new QueryOptions { Parameters = parameters }, CancellationToken.None));

        Assert.Equal(SearchQueryExecutor.OffsetPagingUnavailable(engine), page.Error!.Message);
        Assert.Equal(SearchQueryExecutor.OffsetPagingUnavailable(engine), pageWithValues.Error!.Message);
        Assert.Null(count);
        Assert.Equal(SearchQueryExecutor.GeneratedWritesUnavailable(engine), Assert.Single(writes).Error!.Message);
        Assert.Equal(SearchQueryExecutor.VariablesUnavailable(engine), withVariables.Error!.Message);
        Assert.Contains(SearchErrors.EngineName(engine), withVariables.Error.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => Stream(
            (SearchQueryExecutor)executor, "SELECT n FROM logs WHERE n = @bearing_id", new QueryOptions { Parameters = parameters }));
        Assert.Empty(api.Requests);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static SearchQueryExecutor Executor(FakeSearchApi api, SearchEngine engine, int timeoutSeconds = 0)
        => new(new SearchConnectionFactory(api, engine, new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "search",
            ProviderId = engine == SearchEngine.Elasticsearch ? "elasticsearch" : "opensearch",
            Host = "127.0.0.1",
            Port = 9200,
            StatementTimeoutSeconds = timeoutSeconds,
        }));

    private static async Task<List<RowBatch>> Stream(SearchQueryExecutor executor, string sql, QueryOptions options)
    {
        var batches = new List<RowBatch>();
        await foreach (var batch in executor.StreamRowsAsync(sql, options, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>A SQL first page with columns <c>n</c> (long) and <c>name</c> (keyword) and <paramref name="rows"/>
    /// rows numbered from 0, in the engine's own shape.</summary>
    private static SearchResponse SqlFirst(SearchEngine engine, int rows, string? cursor = null, string? warning = null)
    {
        var columns = engine == SearchEngine.Elasticsearch ? "columns" : "schema";
        var data = engine == SearchEngine.Elasticsearch ? "rows" : "datarows";
        var values = string.Join(",", Enumerable.Range(0, rows).Select(i => $"[{i},\"r{i}\"]"));
        var tail = cursor is null ? "" : $",\"cursor\":\"{cursor}\"";

        return Json(
            $$"""{"{{columns}}":[{"name":"n","type":"long"},{"name":"name","type":"keyword"}],"{{data}}":[{{values}}]{{tail}}}""",
            warning is null ? [] : [warning]);
    }

    /// <summary>A continuation page in the engine's shape: rows only, each value as raw JSON text.</summary>
    private static SearchResponse SqlLater(SearchEngine engine, object[][] rows, string? cursor, string? warning = null)
    {
        var data = engine == SearchEngine.Elasticsearch ? "rows" : "datarows";
        var values = string.Join(",", rows.Select(r => "[" + string.Join(",", r.Select(v => v.ToString())) + "]"));
        var tail = cursor is null ? "" : $",\"cursor\":\"{cursor}\"";

        return Json($$"""{"{{data}}":[{{values}}]{{tail}}}""", warning is null ? [] : [warning]);
    }

    /// <summary>An ES|QL or PPL answer with one long column <c>n</c> and <paramref name="rows"/> rows.</summary>
    private static SearchResponse Piped(SearchEngine engine, int rows)
    {
        var columns = engine == SearchEngine.Elasticsearch ? "columns" : "schema";
        var data = engine == SearchEngine.Elasticsearch ? "values" : "datarows";
        var values = string.Join(",", Enumerable.Range(0, rows).Select(i => $"[{i}]"));

        return Json($$"""{"{{columns}}":[{"name":"n","type":"long"}],"{{data}}":[{{values}}]}""");
    }

    private static SearchResponse Json(string body, params string[] warnings)
        => new(200, Encoding.UTF8.GetBytes(body), warnings);

    private static SearchResponse Error(int status, string body) => new(status, Encoding.UTF8.GetBytes(body), []);

    /// <summary>One request as the fake saw it: the method, the path and the body as text.</summary>
    private sealed record Sent(HttpMethod Method, string Path, string Body)
    {
        public JsonElement? Field(string name)
        {
            using var document = JsonDocument.Parse(Body);

            return document.RootElement.TryGetProperty(name, out var value) ? value.Clone() : null;
        }

        /// <summary>The <c>query</c> the body carries, or null for a cursor request.</summary>
        public string? Query => Field("query")?.GetString();

        /// <summary>The <c>cursor</c> the body carries, or null for a statement.</summary>
        public string? Cursor => Field("cursor")?.GetString();
    }

    /// <summary>
    /// The whole transport, as <see cref="ISearchApi"/>: records every request and answers with what the test's
    /// responder says. <see cref="Delay"/> holds each answer back until the request's token is cancelled (or the
    /// delay passes), and <see cref="Canceled"/> says whether that happened.
    /// </summary>
    private sealed class FakeSearchApi(Func<Sent, SearchResponse> respond) : ISearchApi
    {
        public List<Sent> Requests { get; } = [];

        public TimeSpan? Delay { get; set; }

        public bool Canceled { get; private set; }

        public async Task<SearchResponse> SendAsync(
            HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
        {
            var sent = new Sent(method, pathAndQuery, jsonBody is null ? "" : Encoding.UTF8.GetString(jsonBody));
            Requests.Add(sent);

            if (Delay is { } delay)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    Canceled = true;
                    throw;
                }
            }

            return respond(sent);
        }

        public void Dispose()
        {
        }
    }
}
