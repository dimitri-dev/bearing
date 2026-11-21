using System.Text.Json;
using System.Text.RegularExpressions;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.Search;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The Elasticsearch and OpenSearch providers against real servers (<c>./build/test-db-search.sh</c>): the live
/// half of SC12–SC16, through the real <see cref="SearchProvider"/> — its connection factory, metadata reader and
/// executor. Every test is a <see cref="SkippableTheoryAttribute"/> over both engines wherever the behaviour exists
/// on both (§4.2), and skips without its container — a skip is not a pass.
/// <para>
/// <b>The measurements.</b> The spec built on cited, unmeasured facts about the two servers; the tests under
/// "measured" pin what the servers actually do — over raw HTTP where the fact is the server's, through the provider
/// where it is Bearing's. Each says what it settles in its summary.
/// </para>
/// <para>
/// Fixtures are made over raw HTTP with <see cref="SearchFixtures"/> (never the provider under test), uniquely
/// named and removed when the test is done (§4.7).
/// </para>
/// </summary>
public class SearchLiveTests
{
    private const string Es = SearchProvider.ElasticsearchId;
    private const string Os = SearchProvider.OpenSearchId;

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A mapping with an object (<c>user.name</c>), a multi-field (<c>title.keyword</c>) and plain fields.</summary>
    private const string SchemaProperties =
        """{"id":{"type":"keyword"},"n":{"type":"long"},"user":{"properties":{"name":{"type":"keyword"}}},"title":{"type":"text","fields":{"keyword":{"type":"keyword"}}}}""";

    private const string StreamProperties = """{"@timestamp":{"type":"date"},"msg":{"type":"keyword"}}""";

    private const string ValueProperties =
        """{"l":{"type":"long"},"d":{"type":"double"},"b":{"type":"boolean"},"k":{"type":"keyword"},"ts":{"type":"date"},"ip":{"type":"ip"},"o":{"properties":{"x":{"type":"keyword"},"y":{"type":"long"}}},"arr":{"type":"long"}}""";

    /// <summary>A long past 2^53 (a double would round it), a timestamp written with a +02:00 offset (the instant
    /// is 10:20:30.123 UTC), an object and an array.</summary>
    private const string ValueDocument =
        """{"l":9007199254740993,"d":1.5,"b":true,"k":"kw","ts":"2024-03-01T12:20:30.123+02:00","ip":"10.1.2.3","o":{"x":"ox","y":5},"arr":[1,2,3]}""";

    private static readonly DateTime ValueInstant = new(2024, 3, 1, 10, 20, 30, 123, DateTimeKind.Utc);

    // ---- SC12: schema ----------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task The_snapshot_lists_an_index_with_dotted_fields_an_alias_as_a_view_and_a_data_stream(string id)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await fx.CreateIndexAsync("schema", SchemaProperties);
        await fx.BulkAsync(index,
        [
            """{"id":"a","n":1,"user":{"name":"x"},"title":"one"}""",
            """{"id":"b","n":2,"user":{"name":"y"},"title":"two"}""",
            """{"id":"c","n":3,"user":{"name":"z"},"title":"three"}""",
        ]);
        var alias = await fx.CreateAliasAsync(index, "alias");
        var stream = await fx.CreateDataStreamAsync("stream", StreamProperties);
        await fx.BulkAsync(stream, ["""{"@timestamp":"2024-01-01T00:00:00Z","msg":"m"}"""], create: true);

        var provider = SearchTestServer.Provider(id);
        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var reader = provider.CreateMetadataReader(factory);
        var database = Assert.Single(await reader.GetDatabasesAsync(None));
        var snapshot = await reader.LoadSnapshotAsync(database, None);

        var table = Assert.Single(snapshot.Tables, t => t.Name == index);
        Assert.Equal(RelationKind.Table, table.Kind);
        Assert.Equal("", table.Schema);
        var columns = snapshot.ColumnsOf(table.Id).Select(c => (c.Name, c.DataType)).ToList();
        Assert.Contains(("id", "keyword"), columns);
        Assert.Contains(("n", "long"), columns);
        Assert.Contains(("user.name", "keyword"), columns);
        Assert.Contains(("title", "text"), columns);
        Assert.Contains(("title.keyword", "keyword"), columns);
        Assert.DoesNotContain(columns, c => c.Name == "user");

        var view = Assert.Single(snapshot.Tables, t => t.Name == alias);
        Assert.Equal(RelationKind.View, view.Kind);
        Assert.Contains(("user.name", "keyword"), snapshot.ColumnsOf(view.Id).Select(c => (c.Name, c.DataType)));
        var definition = await reader.GetViewDefinitionAsync(view.Id, None);
        Assert.Contains($"-- alias \"{alias}\" points at: {index}", definition);

        // Measurement 3 settled it: the first mapping call already holds the stream's backing index.
        var streamTable = Assert.Single(snapshot.Tables, t => t.Name == stream);
        Assert.Equal(RelationKind.Table, streamTable.Kind);
        var streamColumns = snapshot.ColumnsOf(streamTable.Id).Select(c => (c.Name, c.DataType)).ToList();
        Assert.Contains(("@timestamp", "date"), streamColumns);
        Assert.Contains(("msg", "keyword"), streamColumns);

        // Hidden: the stream's backing index and every dot-named system index.
        Assert.DoesNotContain(snapshot.Tables, t => t.Name.StartsWith('.'));

        var sizes = await reader.GetRelationSizesAsync(None);
        var size = Assert.Single(sizes, s => s.TableId == table.Id);
        Assert.Equal(3, size.EstimatedRows);
        Assert.True(size.TotalBytes > 0);
        Assert.DoesNotContain(sizes, s => s.TableId == view.Id || s.TableId == streamTable.Id);
    }

    // ---- SC13: paging ----------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Sql_over_250_documents_pages_100_100_50_from_the_servers_cursor(string id)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 250);

        var provider = SearchTestServer.Provider(id);
        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var executor = provider.CreateQueryExecutor(factory);

        var first = Assert.Single(await executor.ExecuteAsync(
            $"select id from {index}", new QueryOptions { MaxRows = 100 }, None));
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal(100, first.Rows.Count);
        Assert.True(first.Truncated);
        Assert.NotNull(first.Continuation);

        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 100, None);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(100, second.Rows.Count);
        Assert.NotNull(second.Continuation);

        var third = await executor.ExecuteContinuationAsync(second.Continuation!, 100, None);
        Assert.True(third.Success, third.Error?.Message);
        Assert.Equal(50, third.Rows.Count);
        Assert.Null(third.Continuation);
        Assert.False(third.Truncated);

        var ids = first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => (string)r[0]!).ToList();
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Equal(first.Columns, third.Columns);
    }

    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Streaming_follows_the_cursor_and_stops_at_the_cap(string id)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 250);

        var provider = SearchTestServer.Provider(id);
        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var executor = provider.CreateQueryExecutor(factory);

        var capped = await CollectAsync(executor.StreamRowsAsync(
            $"select id from {index}", new QueryOptions { MaxRows = 180, BatchRows = 100 }, None));
        Assert.Equal(180, capped.Sum(b => b.Rows.Count));
        Assert.True(capped[^1].Truncated);
        Assert.Equal(180, capped.SelectMany(b => b.Rows).Select(r => (string)r[0]!).Distinct().Count());

        var whole = await CollectAsync(executor.StreamRowsAsync(
            $"select id from {index}", new QueryOptions { MaxRows = null, BatchRows = 100 }, None));
        Assert.Equal(250, whole.SelectMany(b => b.Rows).Select(r => (string)r[0]!).Distinct().Count());
        Assert.False(whole[^1].Truncated);
    }

    // ---- SC14: piped languages and expiry --------------------------------------------------------------

    [SkippableTheory]
    [InlineData(Es, "FROM {0} | KEEP id, n | LIMIT 5")]
    [InlineData(Os, "source={0} | fields id, n | head 5")]
    public async Task The_piped_language_returns_typed_rows(string id, string query)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 20);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(id), string.Format(query, index), maxRows: 2));

        Assert.True(result.Success, result.Error?.Message);
        // Returned whole: the page size is not applied to an answer that cannot continue.
        Assert.Equal(5, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.Null(result.Continuation);
        Assert.Equal(typeof(string), result.Columns.Single(c => c.Name == "id").ClrType);
        Assert.Equal(typeof(long), result.Columns.Single(c => c.Name == "n").ClrType);
        Assert.All(result.Rows, row => Assert.IsType<long>(row[1]));
    }

    [SkippableFact]
    public async Task Esql_without_a_limit_returns_its_default_1000_rows_and_says_so_in_the_notice()
    {
        await SearchTestServer.RequireAsync(Es);
        await using var fx = SearchTestServer.Fixtures(Es);
        var index = await PagingIndexAsync(fx, 1100);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Es), $"FROM {index} | KEEP id", maxRows: 100));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1000, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.Equal("No limit defined, adding default limit of [1000]", result.Notice);
        Assert.Null(result.Message);
    }

    [SkippableTheory]
    [InlineData(Es, "search_phase_execution_exception")]
    [InlineData(Os, "SearchPhaseExecutionException")]
    public async Task A_cursor_closed_on_the_server_continues_to_the_expired_sentence_and_no_rows(string id, string type)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 5);

        var provider = SearchTestServer.Provider(id);
        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var executor = provider.CreateQueryExecutor(factory);
        var first = Assert.Single(await executor.ExecuteAsync($"select id from {index}", new QueryOptions { MaxRows = 2 }, None));
        Assert.NotNull(first.Continuation);

        var cursor = SearchContinuation.Decode(first.Continuation!).Cursor;
        var closed = await fx.CloseSqlCursorAsync(id, cursor);
        Assert.Equal(200, closed.StatusCode);
        using (var json = closed.Json())
        {
            Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
        }

        var next = await executor.ExecuteContinuationAsync(first.Continuation!, 2, None);

        Assert.False(next.Success);
        Assert.Empty(next.Rows);
        Assert.Null(next.Continuation);
        Assert.StartsWith(
            $"{provider.DisplayName} no longer accepts this result's cursor (HTTP 404), so no more rows were read.",
            next.Error!.Message);
        Assert.Contains("The statement was not run again", next.Error.Message);
        Assert.DoesNotContain(cursor, next.Error.Message);
        Assert.Equal(type, next.Error.SqlState);
        // Decision D4: Elasticsearch's reason is a generic "all shards failed"; the root cause it carries says what
        // failed, and now follows it. OpenSearch names the missing search context in its details.
        Assert.Contains(
            id == Es ? "all shards failed (No search context found for id" : "SearchContextMissingException",
            next.Error.Message);
    }

    // ---- SC15: values and shapes -----------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(Es, "select l, d, b, k, ts, ip, o.x from {0}")]
    [InlineData(Os, "select l, d, b, k, ts, ip, o.x from {0}")]
    public async Task Sql_values_round_trip_with_their_types(string id, string query)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await ValueIndexAsync(fx);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(id), string.Format(query, index)));

        Assert.True(result.Success, result.Error?.Message);
        var row = Assert.Single(result.Rows);
        Assert.Equal(
            [typeof(long), typeof(double), typeof(bool), typeof(string), typeof(DateTime), typeof(string), typeof(string)],
            result.Columns.Select(c => c.ClrType));
        Assert.Equal(9007199254740993L, row[0]);
        Assert.Equal(1.5, row[1]);
        Assert.Equal(true, row[2]);
        Assert.Equal("kw", row[3]);
        // Measurement 6: OpenSearch's zone-less "yyyy-MM-dd HH:mm:ss.fff" is the UTC instant that was indexed.
        Assert.Equal(ValueInstant, row[4]);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)row[4]!).Kind);
        Assert.Equal("10.1.2.3", row[5]);
        Assert.Equal("ox", row[6]);
    }

    [SkippableTheory]
    [InlineData(Es, "FROM {0} | KEEP l, d, b, k, ts, ip, o.x, arr | LIMIT 5")]
    [InlineData(Os, "source={0} | fields l, d, b, k, ts, ip, o, arr | head 5")]
    public async Task Piped_values_round_trip_including_objects_and_arrays(string id, string query)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await ValueIndexAsync(fx);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(id), string.Format(query, index)));

        Assert.True(result.Success, result.Error?.Message);
        var row = Assert.Single(result.Rows);
        Assert.Equal(
            [typeof(long), typeof(double), typeof(bool), typeof(string), typeof(DateTime), typeof(string), typeof(string), typeof(string)],
            result.Columns.Select(c => c.ClrType));
        Assert.Equal(9007199254740993L, row[0]);
        Assert.Equal(1.5, row[1]);
        Assert.Equal(true, row[2]);
        Assert.Equal("kw", row[3]);
        Assert.Equal(ValueInstant, row[4]);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)row[4]!).Kind);
        Assert.Equal("10.1.2.3", row[5]);
        // ES|QL has no object column (only its leaves); PPL returns the object, kept as JSON text.
        Assert.Equal(id == Es ? "ox" : """{"x":"ox","y":5}""", row[6]);
        // A multi-valued field: an array where the declared type is long — the column turns to JSON text.
        Assert.Equal("[1,2,3]", row[7]);
    }

    [SkippableFact]
    public async Task Measured_8_elasticsearch_sql_refuses_an_array_field_and_the_refusal_is_shown_as_it_is()
    {
        await SearchTestServer.RequireAsync(Es);
        await using var fx = SearchTestServer.Fixtures(Es);
        var index = await ValueIndexAsync(fx);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Es), $"select arr from {index}"));

        Assert.False(result.Success);
        Assert.Equal("SQL: Arrays (returned by [arr]) are not supported", result.Error!.Message);
        Assert.Equal("invalid_argument_exception", result.Error.SqlState);
    }

    [SkippableTheory]
    [InlineData(Es, "select l from {0}; FROM {0} | KEEP k | LIMIT 1; select ip from {0}")]
    [InlineData(Os, "select l from {0}; source={0} | fields k | head 1; select ip from {0}")]
    public async Task A_script_mixing_sql_and_the_piped_language_returns_its_results_in_order(string id, string script)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await ValueIndexAsync(fx);

        var results = await RunAsync(SearchTestServer.Info(id), string.Format(script, index));

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
        Assert.Equal([0, 1, 2], results.Select(r => r.StatementIndex));
        Assert.Equal(["l", "k", "ip"], results.Select(r => r.Columns.Single().Name));
        Assert.Equal([(object?)9007199254740993L, "kw", "10.1.2.3"], results.Select(r => Assert.Single(r.Rows)[0]));
    }

    // ---- SC16: sign-in and TLS -------------------------------------------------------------------------

    [SkippableFact]
    public async Task Basic_sign_in_works_on_the_secured_node_under_tls_mode_require()
    {
        await SearchTestServer.RequireSecureAsync();
        var info = SearchTestServer.SecureInfo(TlsMode.Require);
        await using var factory = SearchTestServer.Factory(info, SearchTestServer.SecurePassword);

        Assert.True(await factory.TestConnectionAsync(None));
        var search = (SearchConnectionFactory)factory;
        Assert.Equal(SearchAuthMethod.Basic, search.AuthMethod);
        Assert.Equal(Uri.UriSchemeHttps, search.BaseUri!.Scheme);

        var result = Assert.Single(await SearchTestServer.Provider(Es).CreateQueryExecutor(factory)
            .ExecuteAsync("SELECT 1 AS one", new QueryOptions(), None));
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, Convert.ToInt32(Assert.Single(result.Rows)[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task Verify_full_refuses_the_secured_nodes_self_signed_certificate()
    {
        await SearchTestServer.RequireSecureAsync();
        await using var factory = SearchTestServer.Factory(SearchTestServer.SecureInfo(TlsMode.VerifyFull), SearchTestServer.SecurePassword);

        var refused = await Assert.ThrowsAsync<SearchTransportException>(() => factory.TestConnectionAsync(None));

        Assert.DoesNotContain(SearchTestServer.SecurePassword, refused.Message);
        Assert.NotEqual(DbErrorKind.Authentication, SearchTestServer.Provider(Es).ClassifyException(refused));
    }

    [SkippableFact]
    public async Task A_wrong_password_fails_the_sign_in_and_classifies_as_authentication()
    {
        await SearchTestServer.RequireSecureAsync();
        const string wrong = "not-the-password-1";
        var provider = SearchTestServer.Provider(Es);
        await using var factory = SearchTestServer.Factory(SearchTestServer.SecureInfo(TlsMode.Require), wrong);

        var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(None));
        Assert.Equal(DbErrorKind.Authentication, provider.ClassifyException(refused));
        Assert.DoesNotContain(wrong, refused.Message);

        var result = Assert.Single(await provider.CreateQueryExecutor(factory).ExecuteAsync("SELECT 1", new QueryOptions(), None));
        Assert.False(result.Success);
        Assert.Equal(DbErrorKind.Authentication, provider.Classify(result.Error!));
        Assert.DoesNotContain(wrong, result.Error!.Message);
    }

    [SkippableFact]
    public async Task An_api_key_created_on_the_server_signs_in_with_authentication_api_key()
    {
        await SearchTestServer.RequireSecureAsync();
        await using var fx = SearchTestServer.SecureFixtures();
        var key = await fx.CreateApiKeyAsync("key");
        var info = SearchTestServer.SecureInfo(TlsMode.Require, SearchConnectionFactory.AuthenticationApiKey, user: "");
        await using var factory = SearchTestServer.Factory(info, key);

        Assert.True(await factory.TestConnectionAsync(None));
        Assert.Equal(SearchAuthMethod.ApiKey, ((SearchConnectionFactory)factory).AuthMethod);
        var result = Assert.Single(await SearchTestServer.Provider(Es).CreateQueryExecutor(factory)
            .ExecuteAsync("SELECT 1 AS one", new QueryOptions(), None));
        Assert.True(result.Success, result.Error?.Message);

        // And a key the server does not know is a sign-in failure, not a privilege.
        await using var stale = SearchTestServer.Factory(info, Convert.ToBase64String("bogus-id:bogus-key"u8));
        var refused = await Assert.ThrowsAsync<SearchSignInException>(() => stale.TestConnectionAsync(None));
        Assert.Equal(SearchAuthMethod.ApiKey, refused.Method);
    }

    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task On_a_plain_http_server_prefer_falls_back_to_http_and_require_fails(string id)
    {
        await SearchTestServer.RequireAsync(id);

        await using (var prefer = SearchTestServer.Factory(SearchTestServer.Info(id, TlsMode.Prefer)))
        {
            Assert.True(await prefer.TestConnectionAsync(None));
            Assert.Equal(Uri.UriSchemeHttp, ((SearchConnectionFactory)prefer).BaseUri!.Scheme);
        }

        await using var require = SearchTestServer.Factory(SearchTestServer.Info(id, TlsMode.Require));
        await Assert.ThrowsAsync<SearchTransportException>(() => require.TestConnectionAsync(None));
    }

    [SkippableFact]
    public async Task On_the_secured_node_prefer_stays_on_https()
    {
        await SearchTestServer.RequireSecureAsync();
        await using var factory = SearchTestServer.Factory(SearchTestServer.SecureInfo(TlsMode.Prefer), SearchTestServer.SecurePassword);

        Assert.True(await factory.TestConnectionAsync(None));
        Assert.Equal(Uri.UriSchemeHttps, ((SearchConnectionFactory)factory).BaseUri!.Scheme);
    }

    // ---- measured: the spec's assumptions --------------------------------------------------------------

    /// <summary>Measurement 1. OpenSearch sends a statement it cannot page to its legacy engine when the request
    /// has <c>fetch_size</c>: the rows come back whole with no cursor — but that engine labels a <c>keyword</c>
    /// group key and the count as <c>double</c> (without <c>fetch_size</c> they are <c>keyword</c> / <c>long</c>).
    /// Bearing keeps every value: the key column, whose strings do not fit a double, reads as text.</summary>
    [SkippableFact]
    public async Task Measured_1_an_opensearch_aggregate_is_sent_without_fetch_size_and_arrives_with_its_real_types()
    {
        await SearchTestServer.RequireAsync(Os);
        await using var fx = SearchTestServer.Fixtures(Os);
        var index = await fx.CreateIndexAsync("agg", """{"g":{"type":"keyword"},"n":{"type":"long"}}""");
        await fx.BulkAsync(index, ["""{"g":"a","n":1}""", """{"g":"b","n":2}""", """{"g":"a","n":3}"""]);

        var raw = await fx.SendAsync(HttpMethod.Post, "/_plugins/_sql",
            $$"""{"query":"select g, count(*) from {{index}} group by g","fetch_size":100}""");
        var unpaged = await fx.SendAsync(HttpMethod.Post, "/_plugins/_sql",
            $$"""{"query":"select g, count(*) from {{index}} group by g"}""");
        Assert.Equal(200, raw.StatusCode);
        Assert.Equal(["double", "double"], SchemaTypes(raw));
        Assert.Equal(["keyword", "long"], SchemaTypes(unpaged));
        Assert.False(HasProperty(raw, "cursor"));

        // Decision D2: Bearing sends fetch_size to OpenSearch SQL only for a plain select, so the aggregate is
        // answered by the main engine: the keyword key as keyword (text), count(*) as long.
        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Os),
            $"select g, count(*) from {index} group by g order by g", maxRows: 100));
        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Continuation);
        Assert.False(result.Truncated);
        Assert.Equal(["keyword", "long"], result.Columns.Select(c => c.DataTypeName));
        Assert.Equal([typeof(string), typeof(long)], result.Columns.Select(c => c.ClrType));
        Assert.Equal(["a", "b"], result.Rows.Select(r => r[0]));
        Assert.Equal([2L, 1L], result.Rows.Select(r => r[1]));

        // A LIMIT is not a plain select either: sent without fetch_size, it comes back whole with the main
        // engine's types (with fetch_size it went to the legacy engine).
        var limited = Assert.Single(await RunAsync(SearchTestServer.Info(Os), $"select n from {index} limit 2", maxRows: 1));
        Assert.True(limited.Success, limited.Error?.Message);
        Assert.Equal(2, limited.Rows.Count);
        Assert.Null(limited.Continuation);
        Assert.False(limited.Truncated);
        Assert.Equal("long", Assert.Single(limited.Columns).DataTypeName);
    }

    /// <summary>
    /// The cost of decision D2, measured: a statement that is not a plain select is sent to OpenSearch SQL without
    /// <c>fetch_size</c>, so it is never paged. The answer arrives whole, with no cursor, holding at most the
    /// cluster's <c>plugins.query.size_limit</c> rows (read from the node's settings here, not assumed).
    /// </summary>
    [SkippableFact]
    public async Task Measured_14_an_opensearch_statement_that_is_not_a_plain_select_arrives_whole_up_to_the_size_limit()
    {
        await SearchTestServer.RequireAsync(Os);
        await using var fx = SearchTestServer.Fixtures(Os);
        var index = await PagingIndexAsync(fx, 250);

        var settings = await fx.SendAsync(
            HttpMethod.Get, "/_cluster/settings?include_defaults=true&flat_settings=true", null);
        int sizeLimit;
        using (var json = settings.Json())
        {
            var root = json.RootElement;
            var value = root.GetProperty("persistent").TryGetProperty("plugins.query.size_limit", out var p) ? p
                : root.GetProperty("transient").TryGetProperty("plugins.query.size_limit", out var t) ? t
                : root.GetProperty("defaults").GetProperty("plugins.query.size_limit");
            sizeLimit = int.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var statement in new[] { $"select id from {index} limit 250", $"select distinct id from {index}" })
        {
            var result = Assert.Single(await RunAsync(SearchTestServer.Info(Os), statement, maxRows: 100));
            Assert.True(result.Success, $"{statement}: {result.Error?.Message}");
            Assert.Null(result.Continuation);
            Assert.False(result.Truncated);
            Assert.Equal(Math.Min(250, sizeLimit), result.Rows.Count);
        }
    }

    /// <summary>Measurement 2. The exact errors for a closed and for an unknown cursor. A closed one is HTTP 404 on
    /// both (Elasticsearch: <c>search_phase_execution_exception</c> "all shards failed" with root cause
    /// <c>search_context_missing_exception</c>; OpenSearch: <c>SearchPhaseExecutionException</c> whose details name
    /// <c>SearchContextMissingException</c>). An unknown one is HTTP 400 <c>illegal_argument_exception</c> on
    /// Elasticsearch and HTTP 500 <c>UnsupportedOperationException</c> "Unsupported cursor" on OpenSearch. Every one
    /// reaches the user as the cursor sentence with the server's reason.</summary>
    [SkippableTheory]
    [InlineData(Es, 404, "search_phase_execution_exception", "all shards failed", 400, "illegal_argument_exception")]
    [InlineData(Os, 404, "SearchPhaseExecutionException", "Error occurred in OpenSearch engine: all shards failed", 500, "UnsupportedOperationException")]
    public async Task Measured_2_closed_and_unknown_cursor_errors(
        string id, int closedStatus, string closedType, string closedReason, int unknownStatus, string unknownType)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 5);
        var sqlPath = id == Os ? "/_plugins/_sql" : "/_sql";

        var page = await fx.SendAsync(HttpMethod.Post, sqlPath, $$"""{"query":"select id from {{index}}","fetch_size":2}""");
        string cursor;
        using (var json = page.Json())
        {
            cursor = json.RootElement.GetProperty("cursor").GetString()!;
        }
        Assert.Equal(200, (await fx.CloseSqlCursorAsync(id, cursor)).StatusCode);

        var closed = await fx.SendAsync(HttpMethod.Post, sqlPath, JsonSerializer.Serialize(new { cursor }));
        Assert.Equal(closedStatus, closed.StatusCode);
        Assert.Equal(closedType, ErrorField(closed, "type"));
        Assert.Equal(closedReason, ErrorField(closed, "reason"));
        Assert.Contains(id == Es ? "search_context_missing_exception" : "SearchContextMissingException", closed.Body);

        var bogus = id == Os ? "n:abcdef" : "bm90LWEtY3Vyc29y";
        var unknown = await fx.SendAsync(HttpMethod.Post, sqlPath, JsonSerializer.Serialize(new { cursor = bogus }));
        Assert.Equal(unknownStatus, unknown.StatusCode);
        Assert.Equal(unknownType, ErrorField(unknown, "type"));

        // Through the executor, an unknown cursor is the same sentence with its own status.
        var provider = SearchTestServer.Provider(id);
        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var engine = id == Os ? SearchEngine.OpenSearch : SearchEngine.Elasticsearch;
        var token = new SearchContinuation(engine, SearchLanguage.Sql, bogus,
            new SearchLayout([new SearchLayoutColumn("id", "keyword", SearchCellKind.Text)]), 0).Encode();
        var result = await provider.CreateQueryExecutor(factory).ExecuteContinuationAsync(token, 2, None);
        Assert.StartsWith($"{provider.DisplayName} no longer accepts this result's cursor (HTTP {unknownStatus})", result.Error!.Message);
        Assert.Equal(unknownType, result.Error.SqlState);
    }

    /// <summary>Measurement 3 (and 13). <c>GET /*/_mapping?expand_wildcards=open</c> includes a data stream's
    /// backing index (<c>.ds-…</c>) on both engines, so one call carries the stream's fields. And
    /// <c>GET /_data_stream</c> answers 200 with a <c>data_streams</c> array — measured empty on a fresh node.</summary>
    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Measured_3_and_13_the_mapping_call_covers_backing_indices_and_data_streams_answer_200(string id)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);

        var before = await fx.SendAsync(HttpMethod.Get, "/_data_stream");
        Assert.Equal(200, before.StatusCode);
        using (var json = before.Json())
        {
            Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("data_streams").ValueKind);
        }

        var stream = await fx.CreateDataStreamAsync("mapping", StreamProperties);
        await fx.BulkAsync(stream, ["""{"@timestamp":"2024-01-01T00:00:00Z","msg":"m"}"""], create: true);

        var described = await fx.SendAsync(HttpMethod.Get, $"/_data_stream/{stream}");
        List<string> backing;
        using (var json = described.Json())
        {
            backing = json.RootElement.GetProperty("data_streams")[0].GetProperty("indices").EnumerateArray()
                .Select(i => i.GetProperty("index_name").GetString()!).ToList();
        }
        Assert.NotEmpty(backing);
        Assert.All(backing, name => Assert.StartsWith(".ds-", name));

        var mappings = await fx.SendAsync(HttpMethod.Get, "/*/_mapping?expand_wildcards=open");
        using (var json = mappings.Json())
        {
            var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.All(backing, name => Assert.Contains(name, keys));
        }
    }

    /// <summary>Measurement 12. An alias on a data stream: Elasticsearch accepts it and <c>GET /_alias</c> keys it
    /// by the STREAM's name (not the <c>.ds-</c> backing index), so the reader lists it; OpenSearch refuses the
    /// alias outright (404 <c>index_not_found_exception</c>), so there is nothing to list.</summary>
    [SkippableTheory]
    [InlineData(Es)]
    [InlineData(Os)]
    public async Task Measured_12_an_alias_on_a_data_stream(string id)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var stream = await fx.CreateDataStreamAsync("aliased", StreamProperties);
        await fx.BulkAsync(stream, ["""{"@timestamp":"2024-01-01T00:00:00Z","msg":"m"}"""], create: true);
        var alias = SearchTestServer.UniqueName("streamalias");

        var added = await fx.TryCreateAliasAsync(stream, alias);

        if (id == Os)
        {
            Assert.Equal(404, added.StatusCode);
            Assert.Equal("index_not_found_exception", ErrorField(added, "type"));
            return;
        }

        Assert.Equal(200, added.StatusCode);
        var aliases = await fx.SendAsync(HttpMethod.Get, "/_alias");
        using (var json = aliases.Json())
        {
            Assert.True(json.RootElement.GetProperty(stream).GetProperty("aliases").TryGetProperty(alias, out _));
        }

        await using var factory = SearchTestServer.Factory(SearchTestServer.Info(id));
        var reader = SearchTestServer.Provider(id).CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync(Assert.Single(await reader.GetDatabasesAsync(None)), None);
        var view = Assert.Single(snapshot.Tables, t => t.Name == alias);
        Assert.Equal(RelationKind.View, view.Kind);
        Assert.Contains(("msg", "keyword"), snapshot.ColumnsOf(view.Id).Select(c => (c.Name, c.DataType)));
    }

    /// <summary>Measurement 4, fields. A bare dotted field path, the whole path quoted, and each segment quoted all
    /// resolve on both SQL engines.</summary>
    [SkippableTheory]
    [InlineData(Es, "user.name", "\"user.name\"", "\"user\".\"name\"")]
    [InlineData(Os, "user.name", "`user.name`", "`user`.`name`")]
    public async Task Measured_4_a_dotted_field_path_resolves_bare_and_quoted(string id, string bare, string whole, string segments)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await fx.CreateIndexAsync("dotted", SchemaProperties);
        await fx.BulkAsync(index, ["""{"id":"a","user":{"name":"x"}}"""]);

        foreach (var field in new[] { bare, whole, segments })
        {
            var result = Assert.Single(await RunAsync(SearchTestServer.Info(id), $"select {field} from {index}"));
            Assert.True(result.Success, $"{field}: {result.Error?.Message}");
            Assert.Equal("x", Assert.Single(result.Rows)[0]);
        }
    }

    /// <summary>
    /// Measurement 4, index names. An index name holding a <c>-</c> or a <c>.</c>. Elasticsearch SQL takes neither
    /// bare (<c>parsing_exception</c> "mismatched input '.'" / "'-'") and both double-quoted. OpenSearch SQL takes
    /// both bare; backticked it takes the <c>-</c> name, and the <c>.</c> name too — except when the statement has
    /// a <c>LIMIT</c> and the request a <c>fetch_size</c> (as every Bearing request has): OpenSearch 3.10 then
    /// answers through its legacy engine, which keeps the backticks as part of the name ("no such index
    /// [`a.b`]"). What the spec wants of both engines is asserted: the "Select top rows" text (<c>Quote</c> +
    /// <c>SelectTopRows</c>) and what completion inserts (<c>QuoteIfNeeded</c>) both run. Today the dotted case
    /// fails on each engine for a different one of the two — the measurement a later decision has to settle.
    /// <para>
    /// Settled by decisions D1 and D2: Elasticsearch quotes any name that is not one plain word whole (so the
    /// completed index name and field path are <c>"…"</c>), and OpenSearch SQL is sent <c>fetch_size</c> only for a
    /// plain select, so the <c>LIMIT</c> of "Select top rows" stays on the main engine, which takes the backticks.
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(Es, "dash-")]
    [InlineData(Es, "dot.x")]
    [InlineData(Os, "dash-")]
    [InlineData(Os, "dot.x")]
    public async Task Measured_4_an_index_name_with_a_dash_or_a_dot_runs_as_the_dialect_writes_it(string id, string stem)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await fx.CreateIndexAsync("named", SchemaProperties, name: $"bearing_test_{stem}{Guid.NewGuid():N}");
        await fx.BulkAsync(index, ["""{"id":"a","user":{"name":"x"}}"""]);
        Bearing.Sql.ISqlDialect dialect = id == Os
            ? Bearing.Sql.OpenSearchDialect.Instance
            : Bearing.Sql.ElasticsearchDialect.Instance;

        var bare = Assert.Single(await RunAsync(SearchTestServer.Info(id), $"select user.name from {index}"));
        Assert.Equal(id == Os, bare.Success);
        if (id == Es)
        {
            Assert.Equal("parsing_exception", bare.Error!.SqlState);
        }

        var topRows = Assert.Single(await RunAsync(SearchTestServer.Info(id), dialect.SelectTopRows(dialect.Quote(index), 100)));
        Assert.True(topRows.Success, topRows.Error?.Message);

        var completed = dialect.QuoteIfNeeded(index);
        var asCompleted = Assert.Single(await RunAsync(SearchTestServer.Info(id), $"select user.name from {completed}"));
        Assert.True(asCompleted.Success, $"{completed}: {asCompleted.Error?.Message}");

        // The field as completion writes it, too: "user.name" on Elasticsearch, bare on OpenSearch.
        var field = dialect.QuoteIfNeeded("user.name");
        Assert.Equal(id == Es ? "\"user.name\"" : "user.name", field);
        var bothCompleted = Assert.Single(await RunAsync(SearchTestServer.Info(id), $"select {field} from {completed}"));
        Assert.True(bothCompleted.Success, $"{field} / {completed}: {bothCompleted.Error?.Message}");
        Assert.Equal("x", Assert.Single(bothCompleted.Rows)[0]);
    }

    /// <summary>Measurements 5 and 11. The ES|QL default-limit notice is an HTTP <c>Warning</c> header —
    /// <c>299 Elasticsearch-&lt;version&gt;-&lt;hash&gt; "No limit defined, adding default limit of [1000]"</c>, one
    /// header line per warning, read through <see cref="System.Net.Http.Headers.HttpResponseHeaders.NonValidated"/>
    /// — and <see cref="SearchProtocol.WarningTexts"/> takes out exactly the quoted text. A non-ASCII character
    /// arrives as a raw ISO-8859-1 byte (not percent-encoded); the handler decodes it as Latin-1, so it survives.</summary>
    [SkippableFact]
    public async Task Measured_5_and_11_the_esql_warning_header_and_what_bearing_takes_from_it()
    {
        await SearchTestServer.RequireAsync(Es);
        await using var fx = SearchTestServer.Fixtures(Es);
        var index = await PagingIndexAsync(fx, 3);

        var raw = await fx.SendAsync(HttpMethod.Post, "/_query", $$"""{"query":"FROM {{index}} | KEEP id"}""");
        var header = Assert.Single(raw.Warnings);
        Assert.Matches(new Regex("""^299 Elasticsearch-[0-9.]+(-SNAPSHOT)?-[0-9a-f]+ "No limit defined, adding default limit of \[1000\]"$"""), header);
        Assert.Equal(["No limit defined, adding default limit of [1000]"], SearchProtocol.WarningTexts(header));

        var several = await fx.SendAsync(HttpMethod.Post, "/_query", """{"query":"ROW x = \"é q\" | EVAL y = to_long(x)"}""");
        Assert.True(several.Warnings.Count >= 2);
        Assert.Contains(several.Warnings, w => w.EndsWith("\"Line 1:26: org.elasticsearch.xpack.esql.core.InvalidArgumentException: Cannot parse number [é q]\"", StringComparison.Ordinal));

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Es), """ROW x = "é q" | EVAL y = to_long(x)"""));
        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Message);
        Assert.StartsWith("No limit defined, adding default limit of [1000] Line 1:26: evaluation of [to_long(x)] failed", result.Notice);
        Assert.Contains("Cannot parse number [é q]", result.Notice);
    }

    /// <summary>Measurement 11, OpenSearch. Neither a PPL nor a SQL answer carries a body <c>warnings[]</c> or a
    /// <c>Warning</c> header for the statements these tests run.</summary>
    [SkippableFact]
    public async Task Measured_11_opensearch_answers_carry_no_warnings()
    {
        await SearchTestServer.RequireAsync(Os);
        await using var fx = SearchTestServer.Fixtures(Os);
        var index = await PagingIndexAsync(fx, 3);

        foreach (var (path, query) in new[]
                 {
                     ("/_plugins/_ppl", $"source={index} | head 5"),
                     ("/_plugins/_ppl", $"source={index}"),
                     ("/_plugins/_sql", $"select id from {index}"),
                 })
        {
            var raw = await fx.SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(new { query }));
            Assert.Equal(200, raw.StatusCode);
            Assert.Empty(raw.Warnings);
            Assert.False(HasProperty(raw, "warnings"));
        }
    }

    /// <summary>Measurement 7. ES|QL block comments nest (the vendor docs do not say so): the inner <c>*/</c> does not
    /// close the outer comment, so the <c>WHERE</c> inside it is not run — and a <c>;</c> inside it does not end the
    /// statement for Bearing's splitter either. On OpenSearch neither PPL nor SQL nests: the same text is a syntax
    /// error.</summary>
    [SkippableFact]
    public async Task Measured_7_esql_block_comments_nest_and_opensearch_ones_do_not()
    {
        await SearchTestServer.RequireAsync(Es);
        await SearchTestServer.RequireAsync(Os);
        await using var es = SearchTestServer.Fixtures(Es);
        await using var os = SearchTestServer.Fixtures(Os);
        var esIndex = await PagingIndexAsync(es, 3);
        var osIndex = await PagingIndexAsync(os, 3);

        var nested = Assert.Single(await RunAsync(SearchTestServer.Info(Es),
            $"FROM {esIndex} /* a /* b */ | WHERE n > 100 */ | KEEP n | LIMIT 2"));
        Assert.True(nested.Success, nested.Error?.Message);
        Assert.Equal(2, nested.Rows.Count);

        var withSemicolon = await RunAsync(SearchTestServer.Info(Es), $"FROM {esIndex} /* a /* b */ ; c */ | KEEP n | LIMIT 1");
        var only = Assert.Single(withSemicolon);
        Assert.True(only.Success, only.Error?.Message);

        var ppl = await os.SendAsync(HttpMethod.Post, "/_plugins/_ppl",
            JsonSerializer.Serialize(new { query = $"source={osIndex} /* a /* b */ c */ | head 1" }));
        Assert.Equal(400, ppl.StatusCode);
        Assert.Equal("SyntaxCheckException", ErrorField(ppl, "type"));
        var sql = await os.SendAsync(HttpMethod.Post, "/_plugins/_sql",
            JsonSerializer.Serialize(new { query = $"select n /* a /* b */ c */ from {osIndex}" }));
        Assert.Equal(400, sql.StatusCode);
    }

    /// <summary>Measurement 9. A trailing <c>;</c> is a parse error on both Elasticsearch endpoints
    /// (<c>extraneous input ';' expecting &lt;EOF&gt;</c>) and is accepted by OpenSearch SQL and PPL — Bearing never
    /// sends it, so the statement runs everywhere. OpenSearch 3.10's PPL reads <c>//</c> as a comment.</summary>
    [SkippableTheory]
    [InlineData(Es, "/_sql", "select id from {0};", 400)]
    [InlineData(Es, "/_query", "FROM {0} | KEEP id | LIMIT 1;", 400)]
    [InlineData(Os, "/_plugins/_sql", "select id from {0};", 200)]
    [InlineData(Os, "/_plugins/_ppl", "source={0} | fields id | head 1;", 200)]
    public async Task Measured_9_a_trailing_semicolon_never_reaches_the_server(string id, string path, string query, int rawStatus)
    {
        await SearchTestServer.RequireAsync(id);
        await using var fx = SearchTestServer.Fixtures(id);
        var index = await PagingIndexAsync(fx, 3);
        var text = string.Format(query, index);

        var raw = await fx.SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(new { query = text }));
        Assert.Equal(rawStatus, raw.StatusCode);
        if (rawStatus == 400)
        {
            Assert.EndsWith("extraneous input ';' expecting <EOF>", ErrorField(raw, "reason"));
        }

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(id), text));
        Assert.True(result.Success, result.Error?.Message);
    }

    [SkippableFact]
    public async Task Measured_9_opensearch_ppl_reads_a_double_slash_comment()
    {
        await SearchTestServer.RequireAsync(Os);
        await using var fx = SearchTestServer.Fixtures(Os);
        var index = await PagingIndexAsync(fx, 3);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Os), $"source={index} // a note | where n > 100\n| fields n | head 1"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(result.Rows);
    }

    /// <summary>
    /// Measurement 10. A normal ES|QL answer carries <c>"is_partial": false</c>. A partial one occurs with the
    /// request Bearing sends: <c>esql.query.allow_partial_results</c> defaults to true on 9.5, so a query over an
    /// index whose shard cannot be read answers HTTP 200 with <c>"is_partial": true</c> — and no <c>Warning</c>
    /// header. Decision D3: <see cref="SearchProtocol"/> reads <c>is_partial</c>, so the result's notice says the
    /// answer was marked partial and rows may be missing; it no longer looks whole.
    /// </summary>
    [SkippableFact]
    public async Task Measured_10_a_partial_esql_answer_is_http_200_and_its_notice_says_partial()
    {
        await SearchTestServer.RequireAsync(Es);
        await using var fx = SearchTestServer.Fixtures(Es);
        var good = await PagingIndexAsync(fx, 3);
        var broken = await fx.CreateUnallocatableIndexAsync("red", """{"id":{"type":"keyword"},"n":{"type":"long"}}""");

        var whole = await fx.SendAsync(HttpMethod.Post, "/_query", $$"""{"query":"FROM {{good}} | KEEP n | LIMIT 5"}""");
        Assert.False(BoolProperty(whole, "is_partial"));

        var query = $"FROM {good},{broken} | KEEP n | LIMIT 5";
        var partial = await fx.SendAsync(HttpMethod.Post, "/_query", JsonSerializer.Serialize(new { query }));
        Assert.Equal(200, partial.StatusCode);
        Assert.True(BoolProperty(partial, "is_partial"));
        Assert.Empty(partial.Warnings);

        var result = Assert.Single(await RunAsync(SearchTestServer.Info(Es), query));
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.NotNull(result.Notice);
        Assert.StartsWith(SearchProtocol.PartialAnswerSentence, result.Notice);

        var normal = Assert.Single(await RunAsync(SearchTestServer.Info(Es), $"FROM {good} | KEEP n | LIMIT 5"));
        Assert.True(normal.Success, normal.Error?.Message);
        Assert.Null(normal.Notice);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>An index of <paramref name="count"/> documents with a unique keyword <c>id</c> and a long
    /// <c>n</c>.</summary>
    private static async Task<string> PagingIndexAsync(SearchFixtures fx, int count)
    {
        var index = await fx.CreateIndexAsync("rows", """{"id":{"type":"keyword"},"n":{"type":"long"}}""");
        await fx.BulkAsync(index, Enumerable.Range(0, count).Select(i => $$$"""{"id":"d{{{i:0000}}}","n":{{{i}}}}"""));

        return index;
    }

    private static async Task<string> ValueIndexAsync(SearchFixtures fx)
    {
        var index = await fx.CreateIndexAsync("values", ValueProperties);
        await fx.BulkAsync(index, [ValueDocument]);

        return index;
    }

    private static async Task<IReadOnlyList<QueryResult>> RunAsync(ConnectionInfo info, string sql, int? maxRows = 1000)
    {
        var provider = SearchTestServer.Provider(info.ProviderId);
        await using var factory = provider.CreateConnectionFactory(info, null);

        return await provider.CreateQueryExecutor(factory).ExecuteAsync(sql, new QueryOptions { MaxRows = maxRows }, None);
    }

    private static async Task<List<RowBatch>> CollectAsync(IAsyncEnumerable<RowBatch> batches)
    {
        var all = new List<RowBatch>();
        await foreach (var batch in batches)
        {
            all.Add(batch);
        }

        return all;
    }

    private static List<string> SchemaTypes(RawSearchResponse response)
    {
        using var json = response.Json();

        return json.RootElement.GetProperty("schema").EnumerateArray().Select(c => c.GetProperty("type").GetString()!).ToList();
    }

    private static bool HasProperty(RawSearchResponse response, string name)
    {
        using var json = response.Json();

        return json.RootElement.TryGetProperty(name, out _);
    }

    private static bool BoolProperty(RawSearchResponse response, string name)
    {
        using var json = response.Json();

        return json.RootElement.GetProperty(name).GetBoolean();
    }

    private static string? ErrorField(RawSearchResponse response, string name)
    {
        using var json = response.Json();

        return json.RootElement.GetProperty("error").TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
