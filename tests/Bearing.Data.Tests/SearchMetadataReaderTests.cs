using System.Net.Http;
using System.Text;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The Elasticsearch / OpenSearch schema tree against a hand-rolled fake API with canned bodies (§4.1), SC11: indices,
/// aliases and data streams become relations of the right kind; dot-named indices are left out everywhere; mappings
/// become dotted columns (an alias's merged, a data stream's from its newest backing index, asked for once more when
/// the first answer lacked it); absence stays typed — <c>_data_stream</c> not served is "none", refused is a failure,
/// and a refused <c>_cat/indices</c> fails naming the call; sizes never turn a missing figure into 0. The live proof
/// is <c>SearchLiveTests</c>.
/// </summary>
public class SearchMetadataReaderTests
{
    private const string Host = "search.example.internal";
    private const string StreamMappingPath = "/logs-app/_mapping";

    private const string IndicesBody = """
        [{"index":"orders","docs.count":"12","store.size":"4096"},
         {"index":".kibana_1","docs.count":"3","store.size":"100"},
         {"index":"logs-2026.10","docs.count":"5","store.size":"2048"},
         {"index":"logs-2026.11","docs.count":"7","store.size":"1024"}]
        """;

    private const string AliasesBody = """
        {"logs-2026.10":{"aliases":{"logs":{}}},
         "logs-2026.11":{"aliases":{"logs":{"is_write_index":true}}},
         ".kibana_1":{"aliases":{".kibana":{},"kibana-only":{}}},
         ".security-7":{"aliases":{"secret-only":{}}},
         "logs-app":{"aliases":{"app":{}}}}
        """;

    private const string DataStreamsBody = """
        {"data_streams":[
          {"name":"logs-app","indices":[{"index_name":".ds-logs-app-2026.11.01-000001"},{"index_name":".ds-logs-app-2026.11.21-000002"}]},
          {"name":".internal-stream","indices":[{"index_name":".ds-.internal-stream-000001"}]},
          {"name":"ilm-history","hidden":true,"indices":[{"index_name":".ds-ilm-history-000001"}]}]}
        """;

    private const string MappingsBody = """
        {"orders":{"mappings":{"properties":{"customer":{"properties":{"name":{"type":"text","fields":{"keyword":{"type":"keyword"}}}}},"total":{"type":"double"}}}},
         "logs-2026.10":{"mappings":{"properties":{"message":{"type":"keyword"},"@timestamp":{"type":"date"}}}},
         "logs-2026.11":{"mappings":{"properties":{"message":{"type":"text"},"host":{"type":"keyword"}}}}}
        """;

    private const string StreamMappingsBody = """
        {".ds-logs-app-2026.11.01-000001":{"mappings":{"properties":{"old":{"type":"keyword"}}}},
         ".ds-logs-app-2026.11.21-000002":{"mappings":{"properties":{"@timestamp":{"type":"date"},"level":{"type":"keyword"}}}}}
        """;

    // ---- relations -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch))]
    [InlineData(nameof(SearchEngine.OpenSearch))]
    public async Task Indices_and_data_streams_are_tables_and_aliases_are_views_in_one_empty_schema(string engineName)
    {
        var api = Cluster();

        var snapshot = await Reader(api, Enum.Parse<SearchEngine>(engineName)).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Equal(Host, snapshot.Database);
        Assert.Equal([""], snapshot.Schemas);
        Assert.Equal(
            [("app", RelationKind.View), ("logs", RelationKind.View), ("logs-2026.10", RelationKind.Table),
             ("logs-2026.11", RelationKind.Table), ("logs-app", RelationKind.Table), ("orders", RelationKind.Table)],
            snapshot.Tables.Select(t => (t.Name, t.Kind)));
        Assert.All(snapshot.Tables, t => Assert.Equal("", t.Schema));
    }

    [Fact]
    public async Task Dot_named_indices_streams_and_aliases_are_never_listed_nor_an_alias_over_hidden_indices_only()
    {
        var snapshot = await Reader(Cluster()).LoadSnapshotAsync(Host, CancellationToken.None);
        var names = snapshot.Tables.Select(t => t.Name).ToList();

        Assert.DoesNotContain(names, name => name.StartsWith('.'));
        Assert.DoesNotContain(".internal-stream", names);
        Assert.DoesNotContain("ilm-history", names); // a hidden data stream
        Assert.DoesNotContain("kibana-only", names); // points only at .kibana_1
        Assert.DoesNotContain("secret-only", names); // points only at .security-7
    }

    [Fact]
    public async Task An_index_contributes_its_flattened_mapping_as_ordered_nullable_non_key_columns()
    {
        var snapshot = await Reader(Cluster()).LoadSnapshotAsync(Host, CancellationToken.None);

        var columns = ColumnsOf(snapshot, "orders");

        Assert.Equal(
            [(1, "customer.name", "text"), (2, "customer.name.keyword", "keyword"), (3, "total", "double")],
            columns.Select(c => (c.Ordinal, c.Name, c.DataType)));
        Assert.All(columns, c => Assert.False(c.NotNull));
        Assert.All(columns, c => Assert.False(c.IsPrimaryKey));
        Assert.Empty(snapshot.ForeignKeysTouching(snapshot.ResolveTable(null, "orders")!.Id));
    }

    [Fact]
    public async Task An_alias_takes_its_indices_columns_merged_in_first_seen_order_with_differing_types_both_named()
    {
        var snapshot = await Reader(Cluster()).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Equal(
            [("message", "keyword | text"), ("@timestamp", "date"), ("host", "keyword")],
            ColumnsOf(snapshot, "logs").Select(c => (c.Name, c.DataType)));
    }

    [Fact]
    public async Task A_data_stream_takes_its_newest_backing_indexs_columns_asked_for_once_when_the_first_answer_lacked_it()
    {
        var api = Cluster();

        var snapshot = await Reader(api).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Equal([("@timestamp", "date"), ("level", "keyword")], ColumnsOf(snapshot, "logs-app").Select(c => (c.Name, c.DataType)));
        // An alias over the stream gets the stream's columns.
        Assert.Equal(["@timestamp", "level"], ColumnsOf(snapshot, "app").Select(c => c.Name));
        Assert.Equal(1, api.Requests.Count(r => r.Path == StreamMappingPath));
        Assert.Equal(
            [SearchMetadataReader.IndicesPath, SearchMetadataReader.AliasesPath, SearchMetadataReader.DataStreamsPath,
             SearchMetadataReader.MappingsPath, StreamMappingPath],
            api.Requests.Select(r => r.Path));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_data_stream_whose_backing_index_was_in_the_first_mapping_answer_needs_no_second_request()
    {
        const string mappings = """
            {"orders":{"mappings":{"properties":{"total":{"type":"double"}}}},
             ".ds-logs-app-2026.11.21-000002":{"mappings":{"properties":{"level":{"type":"keyword"}}}}}
            """;
        var api = Cluster(mappings: mappings);

        var snapshot = await Reader(api).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Equal(["level"], ColumnsOf(snapshot, "logs-app").Select(c => c.Name));
        Assert.DoesNotContain(api.Requests, r => r.Path == StreamMappingPath);
    }

    [Fact]
    public async Task The_mapping_requests_carry_the_64_mb_ceiling()
    {
        var api = Cluster();

        await Reader(api).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.All(
            api.Requests.Where(r => r.Path.Contains("_mapping", StringComparison.Ordinal)),
            r => Assert.Equal(64L * 1024 * 1024, r.Limit));
    }

    [Fact]
    public void Many_missing_streams_are_asked_for_in_requests_that_each_stay_under_the_path_bound()
    {
        var streams = Enumerable.Range(0, 200).Select(i => $"logs-service-number-{i:D3}").ToList();

        var paths = SearchMetadataReader.StreamMappingPaths(streams);

        Assert.True(paths.Count > 1);
        Assert.All(paths, p => Assert.True(p.Length <= SearchMetadataReader.MaxStreamMappingPathLength));
        Assert.Equal(
            streams,
            paths.SelectMany(p => p["/".Length..^"/_mapping".Length].Split(',')));
        Assert.Equal(["/a%20b,c/_mapping"], SearchMetadataReader.StreamMappingPaths(["a b", "c"]));
        Assert.Empty(SearchMetadataReader.StreamMappingPaths([]));
    }

    // ---- absence stays typed ---------------------------------------------------------------------------

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(405)]
    public async Task A_server_without_data_streams_lists_none_and_the_snapshot_succeeds(int status)
    {
        var api = Cluster(streams: Answer(status, """{"error":{"type":"illegal_argument_exception","reason":"no handler found"},"status":404}"""));

        var snapshot = await Reader(api).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.DoesNotContain(snapshot.Tables, t => t.Name == "logs-app");
        Assert.Contains(snapshot.Tables, t => t.Name == "orders");
        Assert.DoesNotContain(api.Requests, r => r.Path == StreamMappingPath);
    }

    [Fact]
    public async Task A_refused_data_stream_list_fails_the_snapshot_naming_the_call_rather_than_looking_empty()
    {
        var api = Cluster(streams: Answer(403, """{"error":{"type":"security_exception","reason":"action [indices:admin/data_stream/get] is unauthorized for user [reader]"},"status":403}"""));

        var e = await Assert.ThrowsAsync<SearchServerException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal(403, e.StatusCode);
        Assert.Equal(
            "Elasticsearch refused listing the data streams (_data_stream, HTTP 403): action [indices:admin/data_stream/get] "
            + "is unauthorized for user [reader].",
            e.Message);
        Assert.Equal("security_exception", e.Error.SqlState);
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.Classify(e)); // a privilege, not a sign-in
    }

    [Fact]
    public async Task A_data_stream_server_error_fails_the_snapshot_too()
    {
        var api = Cluster(streams: Answer(500, """{"error":{"type":"exception","reason":"boom"},"status":500}"""));

        var e = await Assert.ThrowsAsync<SearchServerException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal("Elasticsearch answered listing the data streams (_data_stream) with HTTP 500: boom.", e.Message);
    }

    [Fact]
    public async Task A_refused_index_list_fails_the_snapshot_naming_the_call()
    {
        var api = Cluster(indices: Answer(403, """{"error":{"type":"security_exception","reason":"action [indices:monitor/stats] is unauthorized"},"status":403}"""));

        var e = await Assert.ThrowsAsync<SearchServerException>(() => Reader(api, SearchEngine.OpenSearch).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal(
            "OpenSearch refused listing the indices (_cat/indices, HTTP 403): action [indices:monitor/stats] is unauthorized.",
            e.Message);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_refused_alias_list_fails_the_snapshot_naming_the_call()
    {
        var api = Cluster(aliases: Answer(403, "forbidden"));

        var e = await Assert.ThrowsAsync<SearchServerException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.StartsWith("Elasticsearch refused listing the aliases (_alias, HTTP 403): forbidden", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_is_the_factorys_sign_in_failure()
    {
        var api = Cluster(indices: Answer(401, ""));

        var e = await Assert.ThrowsAsync<SearchSignInException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(e));
    }

    [Fact]
    public async Task A_refusal_reason_carrying_a_credential_is_scrubbed()
    {
        var api = Cluster(indices: Answer(403, """{"error":{"type":"security_exception","reason":"denied for Authorization: Basic dXNlcjpodW50ZXIy"},"status":403}"""));

        var e = await Assert.ThrowsAsync<SearchServerException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.DoesNotContain("dXNlcjpodW50ZXIy", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_answer_over_the_ceiling_fails_with_the_sentence_naming_it_and_saying_queries_still_run()
    {
        var api = Cluster(mappingsTooLarge: true);

        var e = await Assert.ThrowsAsync<SearchMetadataException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal(
            "The field mappings are larger than 64 MB, the most Bearing reads for one request, so the schema tree was not "
            + "loaded. Queries still run: write the index name in the editor.",
            e.Message);
    }

    [Fact]
    public async Task An_answer_that_is_not_the_expected_json_fails_naming_the_call()
    {
        var api = Cluster(aliases: Answer(200, "<html>proxy login</html>"));

        var e = await Assert.ThrowsAsync<SearchMetadataException>(() => Reader(api).LoadSnapshotAsync(Host, CancellationToken.None));

        Assert.Equal(
            "Elasticsearch answered listing the aliases (_alias) with something Bearing could not read as the expected JSON.",
            e.Message);
    }

    [Fact]
    public async Task A_cluster_with_no_listed_relation_asks_for_no_mapping()
    {
        var api = Cluster(indices: Answer(200, """[{"index":".kibana_1","docs.count":"1","store.size":"1"}]"""),
            aliases: Answer(200, "{}"), streams: Answer(200, """{"data_streams":[]}"""));

        var snapshot = await Reader(api).LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Empty(snapshot.Tables);
        Assert.DoesNotContain(api.Requests, r => r.Path.Contains("_mapping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Never_reads_a_security_node_or_cluster_settings_api()
    {
        var api = Cluster();
        var reader = Reader(api);

        await reader.LoadSnapshotAsync(Host, CancellationToken.None);
        await reader.GetRelationSizesAsync(CancellationToken.None);
        await reader.GetViewDefinitionAsync(SearchMetadataReader.StableId("logs"), CancellationToken.None);
        await reader.GetRolesAsync(CancellationToken.None);
        await reader.GetRoleGrantsAsync("anyone", CancellationToken.None);

        Assert.DoesNotContain(api.Requests, r =>
            r.Path.Contains("_security", StringComparison.Ordinal)
            || r.Path.Contains("_nodes", StringComparison.Ordinal)
            || r.Path.Contains("_cluster", StringComparison.Ordinal)
            || r.Path.Contains("_plugins/_security", StringComparison.Ordinal));
    }

    // ---- ids -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ids_are_stable_across_two_loads_and_resolve_back()
    {
        var reader = Reader(Cluster());

        var first = await reader.LoadSnapshotAsync(Host, CancellationToken.None);
        var second = await reader.LoadSnapshotAsync(Host, CancellationToken.None);

        Assert.Equal(first.Tables.Select(t => (t.Name, t.Id)), second.Tables.Select(t => (t.Name, t.Id)));
        Assert.Equal(first.Tables.Count, first.Tables.Select(t => t.Id).Distinct().Count());
        Assert.All(first.Tables, t => Assert.Equal(SearchMetadataReader.StableId(t.Name), t.Id));
        Assert.All(first.Tables, t => Assert.Equal(t.Name, first.TableById(t.Id)!.Name));

        // An alias id resolves back through the current alias list.
        var logs = first.Tables.Single(t => t.Name == "logs");
        Assert.StartsWith("-- alias \"logs\"", await reader.GetViewDefinitionAsync(logs.Id, CancellationToken.None), StringComparison.Ordinal);
    }

    // ---- sizes -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Sizes_come_from_cat_indices_and_a_missing_figure_is_never_a_zero()
    {
        var api = Cluster(indices: Answer(200, """
            [{"index":"orders","docs.count":"12","store.size":"4096"},
             {"index":"no-size","docs.count":"3"},
             {"index":"null-size","docs.count":"3","store.size":null},
             {"index":"odd-count","docs.count":"n/a","store.size":"10"},
             {"index":"negative","docs.count":"-1","store.size":"20"},
             {"index":".hidden","docs.count":"9","store.size":"30"}]
            """));

        var sizes = await Reader(api).GetRelationSizesAsync(CancellationToken.None);

        Assert.Equal(
            [SearchMetadataReader.StableId("orders"), SearchMetadataReader.StableId("odd-count"), SearchMetadataReader.StableId("negative")],
            sizes.Select(s => s.TableId));
        var orders = sizes[0];
        Assert.Equal((4096L, 4096L, 0L, 0L, (long?)12), (orders.TotalBytes, orders.TableBytes, orders.IndexBytes, orders.ToastBytes, orders.EstimatedRows));
        Assert.Null(sizes[1].EstimatedRows);
        Assert.Equal(10, sizes[1].TotalBytes);
        Assert.Null(sizes[2].EstimatedRows);
        Assert.Equal([SearchMetadataReader.IndicesPath], api.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task Aliases_and_data_streams_get_no_size_entry()
    {
        var sizes = await Reader(Cluster()).GetRelationSizesAsync(CancellationToken.None);

        Assert.DoesNotContain(sizes, s => s.TableId == SearchMetadataReader.StableId("logs"));
        Assert.DoesNotContain(sizes, s => s.TableId == SearchMetadataReader.StableId("logs-app"));
        Assert.Equal(3, sizes.Count);
    }

    [Fact]
    public async Task The_one_database_is_the_real_host_with_no_size_and_needs_no_request()
    {
        var api = Cluster();
        var reader = Reader(api, host: "  " + Host + " ");

        Assert.Equal([Host], await reader.GetDatabasesAsync(CancellationToken.None));
        var size = Assert.Single(await reader.GetDatabaseSizesAsync(CancellationToken.None));
        Assert.Equal(Host, size.Database);
        Assert.Null(size.Bytes);
        Assert.Empty(api.Requests);
    }

    // ---- alias definition ------------------------------------------------------------------------------

    [Fact]
    public async Task An_alias_definition_is_comment_lines_listing_its_targets()
    {
        var definition = await Reader(Cluster()).GetViewDefinitionAsync(SearchMetadataReader.StableId("logs"), CancellationToken.None);

        Assert.Equal("-- alias \"logs\" points at: logs-2026.10, logs-2026.11\n", definition);
    }

    [Fact]
    public async Task A_filter_and_routing_are_noted_only_when_the_alias_has_them()
    {
        var api = Cluster(aliases: Answer(200, """
            {"a":{"aliases":{"same":{"filter":{"term":{"user.id":"kimchy"}},"index_routing":"1","search_routing":"1,2"},
                             "mixed":{"filter":{"term":{"x":1}}}}},
             "b":{"aliases":{"same":{"filter":{"term":{"user.id":"kimchy"}},"index_routing":"1","search_routing":"1,2"},
                             "mixed":{}}},
             ".hidden":{"aliases":{"mixed":{"filter":{"term":{"secret":true}}}}}}
            """));
        var reader = Reader(api);

        var same = await reader.GetViewDefinitionAsync(SearchMetadataReader.StableId("same"), CancellationToken.None);
        var mixed = await reader.GetViewDefinitionAsync(SearchMetadataReader.StableId("mixed"), CancellationToken.None);

        Assert.Equal(
            "-- alias \"same\" points at: a, b\n"
            + "-- filtered: {\"term\":{\"user.id\":\"kimchy\"}}\n"
            + "-- index routing: 1\n"
            + "-- search routing: 1,2\n",
            same);
        Assert.Equal(
            "-- alias \"mixed\" points at: a, b\n"
            + "-- and 1 hidden or system index (names starting with \".\"), not listed\n"
            + "-- filtered on a: {\"term\":{\"x\":1}}\n",
            mixed);
        Assert.DoesNotContain("secret", mixed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_line_of_a_definition_is_a_comment_even_for_a_filter_holding_a_line_break()
    {
        var api = Cluster(aliases: Answer(200, """
            {"a":{"aliases":{"f":{"filter":{"match":{"msg":"line one\nline two; drop"}},"search_routing":"x\ny"}}}}
            """));

        var definition = await Reader(api).GetViewDefinitionAsync(SearchMetadataReader.StableId("f"), CancellationToken.None);

        Assert.All(definition.TrimEnd('\n').Split('\n'), line => Assert.StartsWith("--", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_id_that_is_not_a_listed_alias_has_an_empty_definition()
    {
        var reader = Reader(Cluster());

        Assert.Equal("", await reader.GetViewDefinitionAsync(SearchMetadataReader.StableId("orders"), CancellationToken.None));
        Assert.Equal("", await reader.GetViewDefinitionAsync(SearchMetadataReader.StableId(".kibana"), CancellationToken.None));
        Assert.Equal("", await reader.GetViewDefinitionAsync(12345, CancellationToken.None));
    }

    // ---- the kinds neither engine has --------------------------------------------------------------------

    [Fact]
    public async Task The_kinds_neither_engine_has_are_empty_and_ask_the_server_nothing()
    {
        var api = Cluster();
        var reader = Reader(api);

        var details = await reader.GetTableDetailsAsync(SearchMetadataReader.StableId("orders"), CancellationToken.None);
        Assert.Empty(details.Constraints);
        Assert.Empty(details.Indexes);
        Assert.Empty(details.Triggers);
        Assert.Empty(await reader.GetRoutinesAsync(CancellationToken.None));
        Assert.Same(DatabaseObjectKinds.Empty, await reader.GetDatabaseObjectsAsync(CancellationToken.None));
        Assert.Empty(await reader.GetRolesAsync(CancellationToken.None));
        Assert.Empty(await reader.GetTablespacesAsync(CancellationToken.None));
        var grants = await reader.GetRoleGrantsAsync("reader", CancellationToken.None);
        Assert.False(grants.Visible);
        Assert.Empty(grants.Grants);
        var routine = await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetRoutineDefinitionAsync(1, CancellationToken.None));
        Assert.Equal("Elasticsearch has no stored routines.", routine.Message);
        Assert.Null(await ((IMetadataReader)reader).SampleAttributesAsync(
            new TableInfo(1, "", "orders", RelationKind.Table), 20, CancellationToken.None));
        Assert.Empty(api.Requests);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static IReadOnlyList<ColumnInfo> ColumnsOf(ISchemaSnapshot snapshot, string name)
        => snapshot.ColumnsOf(snapshot.Tables.Single(t => t.Name == name).Id);

    private static SearchMetadataReader Reader(FakeSearchApi api, SearchEngine engine = SearchEngine.Elasticsearch, string host = Host)
        => new(new SearchConnectionFactory(api, engine, new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "search",
            ProviderId = engine == SearchEngine.Elasticsearch ? "elasticsearch" : "opensearch",
            Host = host,
            Port = 9200,
        }));

    /// <summary>
    /// A small cluster: two log indices behind the alias <c>logs</c>, an <c>orders</c> index, a hidden
    /// <c>.kibana_1</c>, the data stream <c>logs-app</c> (behind the alias <c>app</c>) whose backing indices the first
    /// mapping answer does not include, and a dot-named and a hidden stream. Each call can be replaced.
    /// </summary>
    private static FakeSearchApi Cluster(
        SearchResponse? indices = null,
        SearchResponse? aliases = null,
        SearchResponse? streams = null,
        string? mappings = null,
        bool mappingsTooLarge = false)
        => new(sent => sent.Path switch
        {
            SearchMetadataReader.IndicesPath => indices ?? Answer(200, IndicesBody),
            SearchMetadataReader.AliasesPath => aliases ?? Answer(200, AliasesBody),
            SearchMetadataReader.DataStreamsPath => streams ?? Answer(200, DataStreamsBody),
            SearchMetadataReader.MappingsPath when mappingsTooLarge => throw new SearchResponseTooLargeException(sent.Limit),
            SearchMetadataReader.MappingsPath => Answer(200, mappings ?? MappingsBody),
            StreamMappingPath => Answer(200, StreamMappingsBody),
            _ => Answer(404, $$"""{"error":{"type":"index_not_found_exception","reason":"no such path {{sent.Path}}"},"status":404}"""),
        });

    private static SearchResponse Answer(int status, string body) => new(status, Encoding.UTF8.GetBytes(body), []);

    /// <summary>One request as the fake saw it.</summary>
    private sealed record Sent(HttpMethod Method, string Path, bool HasBody, long Limit);

    /// <summary>The whole transport, as <see cref="ISearchApi"/>: records every request and answers with what the
    /// test's responder says.</summary>
    private sealed class FakeSearchApi(Func<Sent, SearchResponse> respond) : ISearchApi
    {
        public List<Sent> Requests { get; } = [];

        public Task<SearchResponse> SendAsync(
            HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
        {
            var sent = new Sent(method, pathAndQuery, jsonBody is not null, maxResponseBytes);
            Requests.Add(sent);

            return Task.FromResult(respond(sent));
        }

        public void Dispose()
        {
        }
    }
}
