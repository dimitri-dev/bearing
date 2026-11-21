using System.Text;
using System.Text.Json;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <see cref="SearchProtocol"/>: the exact request bodies for the four endpoints and their two continuations,
/// and every answer shape read into one neutral page — columns, rows, cursor, notice — with anything else a
/// typed protocol error (SC9). Response bodies follow the shapes the vendors document (research notes); the live
/// tests (SC13–SC15) hold them to real servers.
/// </summary>
public class SearchProtocolTests
{
    private static readonly SearchEndpoint EsSql = SearchProtocol.EndpointFor(SearchEngine.Elasticsearch, SearchLanguage.Sql);
    private static readonly SearchEndpoint EsQl = SearchProtocol.EndpointFor(SearchEngine.Elasticsearch, SearchLanguage.Piped);
    private static readonly SearchEndpoint OsSql = SearchProtocol.EndpointFor(SearchEngine.OpenSearch, SearchLanguage.Sql);
    private static readonly SearchEndpoint Ppl = SearchProtocol.EndpointFor(SearchEngine.OpenSearch, SearchLanguage.Piped);

    // ---- Endpoints ----

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), nameof(SearchLanguage.Sql), "/_sql?format=json", "SQL", true)]
    [InlineData(nameof(SearchEngine.Elasticsearch), nameof(SearchLanguage.Piped), "/_query?format=json", "ES|QL", false)]
    [InlineData(nameof(SearchEngine.OpenSearch), nameof(SearchLanguage.Sql), "/_plugins/_sql?format=jdbc", "SQL", true)]
    [InlineData(nameof(SearchEngine.OpenSearch), nameof(SearchLanguage.Piped), "/_plugins/_ppl?format=jdbc", "PPL", false)]
    public void Each_engine_and_language_has_one_endpoint(
        string engineName, string languageName, string path, string name, bool pages)
    {
        var endpoint = SearchProtocol.EndpointFor(Enum.Parse<SearchEngine>(engineName), Enum.Parse<SearchLanguage>(languageName));

        Assert.Equal(Enum.Parse<SearchEngine>(engineName), endpoint.Engine);
        Assert.Equal(Enum.Parse<SearchLanguage>(languageName), endpoint.Language);
        Assert.Equal(path, endpoint.Path);
        Assert.Equal(name, endpoint.LanguageName);
        Assert.Equal(pages, endpoint.Pages);
    }

    // ---- Request bodies ----

    [Fact]
    public void Elasticsearch_sql_sends_the_query_the_page_size_and_a_five_minute_page_timeout()
        => AssertBody(
            SearchProtocol.FirstPageBody(EsSql, "SELECT * FROM logs", 100),
            ("query", "SELECT * FROM logs"), ("fetch_size", 100), ("page_timeout", "5m"));

    [Fact]
    public void Esql_sends_the_query_alone()
        => AssertBody(SearchProtocol.FirstPageBody(EsQl, "FROM logs | LIMIT 5", 100), ("query", "FROM logs | LIMIT 5"));

    [Fact]
    public void Opensearch_sql_sends_the_query_and_the_page_size()
        => AssertBody(
            SearchProtocol.FirstPageBody(OsSql, "select * from logs", 250),
            ("query", "select * from logs"), ("fetch_size", 250));

    [Fact]
    public void Ppl_sends_the_query_alone()
        => AssertBody(SearchProtocol.FirstPageBody(Ppl, "source=logs | head 5", 100), ("query", "source=logs | head 5"));

    [Fact]
    public void An_elasticsearch_continuation_sends_the_cursor_alone()
        => AssertBody(SearchProtocol.ContinuationBody(EsSql, "sDXF1ZXJ5QW5k=="), ("cursor", "sDXF1ZXJ5QW5k=="));

    [Fact]
    public void An_opensearch_continuation_sends_the_cursor_alone()
        => AssertBody(SearchProtocol.ContinuationBody(OsSql, "d:eyJhIjoxfQ"), ("cursor", "d:eyJhIjoxfQ"));

    [Fact]
    public void A_piped_language_has_no_continuation()
    {
        Assert.Throws<InvalidOperationException>(() => SearchProtocol.ContinuationBody(EsQl, "x"));
        Assert.Throws<InvalidOperationException>(() => SearchProtocol.ContinuationBody(Ppl, "x"));
    }

    [Theory]
    [InlineData("select 'it''s' from \"my \"\"index\"\"\"")]
    [InlineData("FROM logs | WHERE msg == \"a \\\"quoted\\\" \\\\ back\"")]
    [InlineData("select a,\r\n\tb\nfrom t -- note")]
    [InlineData("select '</script>&+ü€😀' from t")]
    [InlineData("select '\u0001\u001f' from t")]
    public void A_query_text_round_trips_exactly(string query)
    {
        foreach (var endpoint in new[] { EsSql, EsQl, OsSql, Ppl })
        {
            using var document = JsonDocument.Parse(SearchProtocol.FirstPageBody(endpoint, query, 10));
            Assert.Equal(query, document.RootElement.GetProperty("query").GetString());
        }
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(100, 100)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10_000, 10_000)]
    [InlineData(10_001, 10_000)]
    [InlineData(int.MaxValue, 10_000)]
    public void The_page_size_is_clamped_to_what_the_servers_take(int? maxRows, int expected)
    {
        Assert.Equal(expected, SearchProtocol.FetchSize(maxRows));

        using var document = JsonDocument.Parse(SearchProtocol.FirstPageBody(OsSql, "select a from logs", maxRows));
        Assert.Equal(expected, document.RootElement.GetProperty("fetch_size").GetInt32());
    }

    /// <summary>
    /// Decision D2: OpenSearch SQL asks for pages only for a plain select — with <c>fetch_size</c> any other shape
    /// is answered by the legacy engine, which mislabels column types (measured on 3.10.0). Elasticsearch SQL pages
    /// every shape itself, so it always asks.
    /// </summary>
    [Theory]
    [InlineData("select a, b from logs where a > 1 order by b", true)]
    [InlineData("SELECT * FROM `logs-2026.11.21`", true)]
    [InlineData("select * from logs limit 100", false)]
    [InlineData("select count(*) from logs", false)]
    [InlineData("select level, count(*) from logs group by level", false)]
    [InlineData("select distinct level from logs", false)]
    [InlineData("select 1", false)]
    public void Opensearch_sql_sends_a_page_size_only_for_a_plain_select(string query, bool pages)
    {
        Assert.Equal(pages, SearchProtocol.AsksForPages(OsSql, query));

        using var os = JsonDocument.Parse(SearchProtocol.FirstPageBody(OsSql, query, 100));
        Assert.Equal(query, os.RootElement.GetProperty("query").GetString());
        Assert.Equal(pages, os.RootElement.TryGetProperty("fetch_size", out _));
        Assert.Equal(pages ? 2 : 1, os.RootElement.EnumerateObject().Count());

        Assert.True(SearchProtocol.AsksForPages(EsSql, query));
        using var es = JsonDocument.Parse(SearchProtocol.FirstPageBody(EsSql, query, 100));
        Assert.Equal(100, es.RootElement.GetProperty("fetch_size").GetInt32());
        Assert.Equal("5m", es.RootElement.GetProperty("page_timeout").GetString());
    }

    [Fact]
    public void A_piped_endpoint_never_asks_for_pages()
    {
        Assert.False(SearchProtocol.AsksForPages(EsQl, "FROM logs"));
        Assert.False(SearchProtocol.AsksForPages(Ppl, "source=logs"));
    }

    // ---- Responses: one realistic answer per endpoint ----

    [Fact]
    public void An_elasticsearch_sql_first_page_reads_columns_rows_and_cursor()
    {
        const string body = """
            {"columns":[{"name":"author","type":"text"},{"name":"page_count","type":"short"},
                        {"name":"release_date","type":"datetime"}],
             "rows":[["Peter F. Hamilton",1123,"2000-03-15T00:00:00.000Z"],["Vernor Vinge",613,"1992-06-01T00:00:00.000Z"]],
             "cursor":"sDXF1ZXJ5QW5kRmV0Y2gBAAAAAAAAAAEWWWdrRlVfSS1TbDYtcW9lc1FJNmlYdw=="}
            """;

        var page = SearchProtocol.ParseFirstPage(EsSql, Ok(body));

        Assert.Equal(
            [new SearchColumn("author", "text"), new SearchColumn("page_count", "short"), new SearchColumn("release_date", "datetime")],
            page.Columns);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal("Vernor Vinge", page.Rows[1][0].GetString());
        Assert.Equal(613, page.Rows[1][1].GetInt32());
        Assert.Equal("sDXF1ZXJ5QW5kRmV0Y2gBAAAAAAAAAAEWWWdrRlVfSS1TbDYtcW9lc1FJNmlYdw==", page.Cursor);
        Assert.Null(page.Notice);
    }

    [Fact]
    public void An_esql_answer_reads_values_and_never_a_cursor()
    {
        const string body = """
            {"took":28,"is_partial":false,"documents_found":2,"values_loaded":4,
             "columns":[{"name":"emp_no","type":"integer"},{"name":"languages","type":"integer"}],
             "values":[[10001,[1,2]],[10002,null]],"cursor":"ignored"}
            """;

        var page = SearchProtocol.ParseFirstPage(EsQl, Ok(body));

        Assert.Equal([new SearchColumn("emp_no", "integer"), new SearchColumn("languages", "integer")], page.Columns);
        Assert.Equal(JsonValueKind.Array, page.Rows[0][1].ValueKind);
        Assert.Equal(JsonValueKind.Null, page.Rows[1][1].ValueKind);
        Assert.Null(page.Cursor);
        Assert.Null(page.Notice);
    }

    /// <summary>Decision D3: Elasticsearch answers HTTP 200 with <c>"is_partial": true</c> and no Warning header
    /// when a shard could not be read (measured, 9.5.6); the notice says so first, ahead of any warning.</summary>
    [Fact]
    public void An_esql_partial_answer_says_so_ahead_of_any_warning()
    {
        var body = """{"is_partial":true,"columns":[{"name":"a","type":"long"}],"values":[[1]]}""";

        var alone = SearchProtocol.ParseFirstPage(EsQl, Ok(body));
        Assert.Equal(SearchProtocol.PartialAnswerSentence, alone.Notice);
        Assert.Contains("is_partial", alone.Notice);
        Assert.Contains("rows may be missing", alone.Notice);

        var warned = SearchProtocol.ParseFirstPage(
            EsQl, new SearchResponse(200, Encoding.UTF8.GetBytes(body), ["299 Elasticsearch-9.5.6-x \"No limit defined\""]));
        Assert.Equal($"{SearchProtocol.PartialAnswerSentence} No limit defined", warned.Notice);
    }

    [Theory]
    [InlineData("""{"is_partial":false,"columns":[{"name":"a","type":"long"}],"values":[[1]]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"values":[[1]]}""")]
    [InlineData("""{"is_partial":"true","columns":[{"name":"a","type":"long"}],"values":[[1]]}""")]
    public void An_esql_answer_not_marked_partial_has_no_partial_notice(string body)
        => Assert.Null(SearchProtocol.ParseFirstPage(EsQl, Ok(body)).Notice);

    [Fact]
    public void Only_esql_reads_is_partial()
    {
        var sql = SearchProtocol.ParseFirstPage(EsSql, Ok("""{"is_partial":true,"columns":[{"name":"a","type":"long"}],"rows":[[1]]}"""));
        var ppl = SearchProtocol.ParseFirstPage(Ppl, Ok("""{"is_partial":true,"schema":[{"name":"a","type":"long"}],"datarows":[[1]]}"""));

        Assert.Null(sql.Notice);
        Assert.Null(ppl.Notice);
    }

    [Fact]
    public void An_opensearch_sql_first_page_reads_schema_datarows_and_cursor()
    {
        const string body = """
            {"schema":[{"name":"firstname","type":"text"},{"name":"age","type":"long"}],
             "datarows":[["Amber",32],["Hattie",36]],"total":1000,"size":2,"status":200,
             "cursor":"d:eyJhIjp7fSwicyI6IkRYRjFaWEo1UVc1a1JtVjBZMmdCQUFBQUFBQUFBQU1XZWpkdFRFRkZUMlpTZEZkeFdsWktT"}
            """;

        var page = SearchProtocol.ParseFirstPage(OsSql, Ok(body));

        Assert.Equal([new SearchColumn("firstname", "text"), new SearchColumn("age", "long")], page.Columns);
        Assert.Equal(["Amber", "Hattie"], page.Rows.Select(r => r[0].GetString()));
        Assert.StartsWith("d:", page.Cursor);
    }

    [Fact]
    public void A_ppl_answer_reads_schema_and_datarows()
    {
        const string body = """
            {"schema":[{"name":"firstname","type":"string"},{"name":"age","type":"bigint"}],
             "datarows":[["Amber",32]],"total":1,"size":1}
            """;

        var page = SearchProtocol.ParseFirstPage(Ppl, Ok(body));

        Assert.Equal([new SearchColumn("firstname", "string"), new SearchColumn("age", "bigint")], page.Columns);
        Assert.Single(page.Rows);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void An_opensearch_alias_names_the_column_when_it_is_not_empty()
    {
        const string body = """
            {"schema":[{"name":"age","alias":"years","type":"long"},{"name":"n","alias":"","type":"long"},
                       {"name":"m","alias":null,"type":"long"}],
             "datarows":[[1,2,3]]}
            """;

        var page = SearchProtocol.ParseFirstPage(OsSql, Ok(body));

        Assert.Equal(["years", "n", "m"], page.Columns!.Select(c => c.Name));
    }

    [Fact]
    public void Elasticsearch_has_no_alias_rule()
    {
        var page = SearchProtocol.ParseFirstPage(EsSql, Ok("""{"columns":[{"name":"a","alias":"b","type":"long"}],"rows":[]}"""));

        Assert.Equal("a", Assert.Single(page.Columns!).Name);
    }

    [Fact]
    public void A_continuation_page_has_rows_and_maybe_a_cursor_but_no_columns()
    {
        var middle = SearchProtocol.ParseContinuationPage(EsSql, Ok("""{"rows":[[1,"a"],[2,"b"]],"cursor":"next"}"""), 2);
        var last = SearchProtocol.ParseContinuationPage(OsSql, Ok("""{"datarows":[[3,"c"]]}"""), 2);

        Assert.Null(middle.Columns);
        Assert.Equal(2, middle.Rows.Count);
        Assert.Equal("next", middle.Cursor);
        Assert.Null(last.Columns);
        Assert.Single(last.Rows);
        Assert.Null(last.Cursor);
    }

    [Fact]
    public void A_continuation_page_ignores_columns_it_carries()
    {
        var page = SearchProtocol.ParseContinuationPage(
            EsSql, Ok("""{"columns":[{"name":"x","type":"long"}],"rows":[[1,2]]}"""), 2);

        Assert.Null(page.Columns);
        Assert.Equal(2, page.Rows[0].Length);
    }

    [Theory]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[1]]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[1]],"cursor":null}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[1]],"cursor":""}""")]
    public void An_absent_null_or_empty_cursor_means_no_more_rows(string body)
        => Assert.Null(SearchProtocol.ParseFirstPage(EsSql, Ok(body)).Cursor);

    [Fact]
    public void Rows_outlive_the_parsed_answer()
    {
        var page = SearchProtocol.ParseFirstPage(EsQl, Ok("""{"columns":[{"name":"o","type":"object"}],"values":[[{"k":[1,2]}]]}"""));

        GC.Collect();
        Assert.Equal("""{"k":[1,2]}""", page.Rows[0][0].GetRawText());
    }

    // ---- Notice ----

    [Theory]
    [InlineData("299 Elasticsearch-9.1.0-abc123 \"No limit defined, adding default limit of [1000]\"",
        "No limit defined, adding default limit of [1000]")]
    [InlineData("299 Elasticsearch-9.1.0-abc123 \"Line 1:1: \\\"x\\\" is a \\\\ path\"", "Line 1:1: \"x\" is a \\ path")]
    [InlineData("299 Elasticsearch-8.0.0-abc \"dated\" \"Mon, 01 Jan 2024 00:00:00 GMT\"", "dated")]
    [InlineData("299 OpenSearch-2.19.0-xyz \"caf%C3%A9 at 100%25\"", "café at 100%")]
    [InlineData("  a header without the shape  ", "a header without the shape")]
    public void A_warning_header_gives_its_quoted_text(string header, string expected)
        => Assert.Equal([expected], SearchProtocol.WarningTexts(header));

    [Fact]
    public void One_warning_header_can_carry_several_warnings()
        => Assert.Equal(
            ["first", "second, with a comma"],
            SearchProtocol.WarningTexts("299 Elasticsearch-9.1.0-a \"first\", 299 Elasticsearch-9.1.0-a \"second, with a comma\""));

    [Fact]
    public void A_warning_reaches_the_notice_of_the_page()
    {
        var response = new SearchResponse(
            200,
            Encoding.UTF8.GetBytes("""{"columns":[{"name":"a","type":"long"}],"values":[[1]]}"""),
            ["299 Elasticsearch-9.1.0-abc \"No limit defined, adding default limit of [1000]\""]);

        var page = SearchProtocol.ParseFirstPage(EsQl, response);

        Assert.Equal("No limit defined, adding default limit of [1000]", page.Notice);
    }

    [Fact]
    public void Opensearch_body_warnings_join_the_header_warnings_once_each()
    {
        var response = new SearchResponse(
            200,
            Encoding.UTF8.GetBytes("""
                {"schema":[{"name":"a","type":"long"}],"datarows":[[1]],
                 "warnings":["Result truncated to 10000 rows.", {"message":"From an object."}, "from the header", null]}
                """),
            ["299 OpenSearch-3.1.0-x \"from the header\"", "299 OpenSearch-3.1.0-x \"from the header\""]);

        var page = SearchProtocol.ParseFirstPage(Ppl, response);

        Assert.Equal("from the header Result truncated to 10000 rows. From an object.", page.Notice);
    }

    [Fact]
    public void No_warning_is_no_notice()
    {
        Assert.Null(SearchProtocol.Notice([], []));
        Assert.Null(SearchProtocol.Notice(["   "], [""]));
    }

    [Fact]
    public void A_long_warning_is_cut()
    {
        var notice = SearchProtocol.Notice([new string('x', 2_000)], []);

        Assert.Equal(SearchProtocol.MaxNoticeLength + 1, notice!.Length);
        Assert.EndsWith("…", notice);
    }

    // ---- Malformed answers ----

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{"rows":[[1]]}""")]
    [InlineData("""{"columns":{},"rows":[]}""")]
    [InlineData("""{"columns":[{"name":"a"}],"rows":[]}""")]
    [InlineData("""{"columns":[{"type":"long"}],"rows":[]}""")]
    [InlineData("""{"columns":[{"name":1,"type":"long"}],"rows":[]}""")]
    [InlineData("""{"columns":["a"],"rows":[]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":{}}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[1]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[1,2]]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[]]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"rows":[[1]],"cursor":5}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"values":[[1]]}""")]
    public void An_answer_of_another_shape_is_a_protocol_error_naming_the_endpoint(string body)
    {
        var error = Assert.Throws<SearchProtocolException>(() => SearchProtocol.ParseFirstPage(EsSql, Ok(body)));

        Assert.Same(EsSql, error.Endpoint);
        Assert.Contains("Elasticsearch SQL (/_sql)", error.Message);
    }

    [Theory]
    [InlineData("""{"schema":[{"name":"a","type":"long"}],"rows":[[1]]}""")]
    [InlineData("""{"columns":[{"name":"a","type":"long"}],"datarows":[[1]]}""")]
    public void Opensearch_reads_its_own_keys_only(string body)
    {
        var error = Assert.Throws<SearchProtocolException>(() => SearchProtocol.ParseFirstPage(Ppl, Ok(body)));

        Assert.Contains("OpenSearch PPL (/_plugins/_ppl)", error.Message);
    }

    [Fact]
    public void A_continuation_row_of_the_wrong_width_is_a_protocol_error()
        => Assert.Throws<SearchProtocolException>(
            () => SearchProtocol.ParseContinuationPage(OsSql, Ok("""{"datarows":[[1,2,3]]}"""), 2));

    [Fact]
    public void A_failed_status_is_not_read_as_a_page()
        => Assert.Throws<SearchProtocolException>(() => SearchProtocol.ParseFirstPage(
            EsSql, new SearchResponse(400, Encoding.UTF8.GetBytes("""{"columns":[],"rows":[]}"""), [])));

    [Fact]
    public void A_protocol_error_quotes_nothing_of_the_body()
    {
        var error = Assert.Throws<SearchProtocolException>(
            () => SearchProtocol.ParseFirstPage(EsSql, Ok("""{"columns":[{"name":"secret-ish","type":"long"}],"rows":[["value-xyz",2]]}""")));

        Assert.DoesNotContain("value-xyz", error.Message);
        Assert.DoesNotContain("secret-ish", error.Message);
    }

    // ---- Helpers ----

    private static SearchResponse Ok(string body) => new(200, Encoding.UTF8.GetBytes(body), []);

    /// <summary>The body is a JSON object with exactly these properties, in this order, with these values.</summary>
    private static void AssertBody(byte[] body, params (string Name, object Value)[] expected)
    {
        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.EnumerateObject().ToList();

        Assert.Equal(expected.Select(e => e.Name), properties.Select(p => p.Name));
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = properties[i].Value;
            switch (expected[i].Value)
            {
                case string text:
                    Assert.Equal(JsonValueKind.String, actual.ValueKind);
                    Assert.Equal(text, actual.GetString());
                    break;
                case int number:
                    Assert.Equal(JsonValueKind.Number, actual.ValueKind);
                    Assert.Equal(number, actual.GetInt32());
                    break;
            }
        }
    }
}
