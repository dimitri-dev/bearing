using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <see cref="SearchBatch"/>: what the executor sends — each statement's text (no terminating <c>;</c>, no
/// comment around it) and the language it is sent in, for every row of the spec's language table on both
/// engines (Behaviour 4, SC2) and the four languages' own lexical forms (SC3). <c>SearchScanParityTests</c>
/// holds it to <c>Bearing.Sql</c>'s reading of the same corpus.
/// </summary>
public class SearchBatchTests
{
    private const SearchEngine Es = SearchEngine.Elasticsearch;
    private const SearchEngine Os = SearchEngine.OpenSearch;

    private static SearchBatchStatement Only(SearchEngine engine, string script)
        => Assert.Single(SearchBatch.Statements(engine, script));

    // ---- Language: Elasticsearch ----

    [Theory]
    [InlineData("SELECT * FROM logs")]
    [InlineData("select * from logs")]
    [InlineData("DESCRIBE logs")]
    [InlineData("desc logs")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SYS COLUMNS")]
    [InlineData("debug select 1")]
    [InlineData("SHOW TABLES")]
    [InlineData("show functions")]
    [InlineData("SHOW")]
    [InlineData("-- a note\nSELECT 1")]
    [InlineData("/* a /* nested */ note */ SELECT 1")]
    [InlineData("// a note\nselect 1")]
    public void Elasticsearch_sql_lead_words_are_sent_as_sql(string script)
        => Assert.Equal(SearchLanguage.Sql, Only(Es, script).Language);

    [Theory]
    [InlineData("SHOW INFO")]
    [InlineData("show info")]
    [InlineData("SHOW /* which */ INFO")]
    [InlineData("FROM logs | LIMIT 5")]
    [InlineData("from logs")]
    [InlineData("ROW a = 1")]
    [InlineData("TS metrics | LIMIT 1")]
    [InlineData("PROMQL up")]
    [InlineData("-- a note\nFROM logs")]
    [InlineData("// a note\nFrom logs")]
    public void Esql_lead_words_are_sent_as_esql(string script)
        => Assert.Equal(SearchLanguage.Piped, Only(Es, script).Language);

    [Theory]
    [InlineData("foo | bar", nameof(SearchLanguage.Piped))]
    [InlineData("foo bar", nameof(SearchLanguage.Sql))]
    [InlineData("foo 'a | b'", nameof(SearchLanguage.Sql))]
    [InlineData("foo \"a | b\"", nameof(SearchLanguage.Sql))]
    [InlineData("foo -- |\n", nameof(SearchLanguage.Sql))]
    [InlineData("foo /* | */ bar", nameof(SearchLanguage.Sql))]
    [InlineData("foo (a | b)", nameof(SearchLanguage.Sql))]
    [InlineData("foo || bar", nameof(SearchLanguage.Sql))]
    public void An_unknown_first_word_is_piped_only_with_a_top_level_pipe_on_elasticsearch(string script, string expected)
        => Assert.Equal(Enum.Parse<SearchLanguage>(expected), Only(Es, script).Language);

    // ---- Language: OpenSearch ----

    [Theory]
    [InlineData("SELECT * FROM logs")]
    [InlineData("delete from logs where a = 1")]
    [InlineData("SHOW TABLES LIKE 'logs%'")]
    [InlineData("show tables like 'l%'")]
    [InlineData("DESCRIBE TABLES LIKE 'logs'")]
    [InlineData("describe tables like 'logs' columns like 'a%'")]
    [InlineData("# a note\nselect 1")]
    [InlineData("-- a note\nselect 1")]
    public void Opensearch_sql_lead_words_are_sent_as_sql(string script)
        => Assert.Equal(SearchLanguage.Sql, Only(Os, script).Language);

    [Theory]
    [InlineData("search source=logs")]
    [InlineData("source=logs | head 5")]
    [InlineData("source = logs")]
    [InlineData("SOURCE=logs")]
    [InlineData("index=logs")]
    [InlineData("describe logs")]
    [InlineData("show datasources")]
    [InlineData("SHOW")]
    [InlineData("explain source=logs | head 1")]
    [InlineData("multisearch [search source=a] [search source=b]")]
    [InlineData("union [source=a] [source=b]")]
    [InlineData("makeresults count=1")]
    [InlineData("graphlookup x")]
    [InlineData("| source=logs")]
    [InlineData("// a note\nsource=logs")]
    public void Ppl_lead_words_are_sent_as_ppl(string script)
        => Assert.Equal(SearchLanguage.Piped, Only(Os, script).Language);

    [Theory]
    [InlineData("foo | bar", nameof(SearchLanguage.Piped))]
    [InlineData("foo bar", nameof(SearchLanguage.Sql))]
    [InlineData("foo '|'", nameof(SearchLanguage.Sql))]
    [InlineData("foo \"|\"", nameof(SearchLanguage.Sql))]
    [InlineData("foo `a|b`", nameof(SearchLanguage.Sql))]
    [InlineData("foo # |\n", nameof(SearchLanguage.Sql))]
    [InlineData("foo -- |", nameof(SearchLanguage.Sql))]
    [InlineData("foo --| bar", nameof(SearchLanguage.Piped))]
    [InlineData("foo /* | */ bar", nameof(SearchLanguage.Sql))]
    public void An_unknown_first_word_is_piped_only_with_a_top_level_pipe_on_opensearch(string script, string expected)
        => Assert.Equal(Enum.Parse<SearchLanguage>(expected), Only(Os, script).Language);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), nameof(SearchLanguage.Sql), "SQL")]
    [InlineData(nameof(SearchEngine.Elasticsearch), nameof(SearchLanguage.Piped), "ES|QL")]
    [InlineData(nameof(SearchEngine.OpenSearch), nameof(SearchLanguage.Sql), "SQL")]
    [InlineData(nameof(SearchEngine.OpenSearch), nameof(SearchLanguage.Piped), "PPL")]
    public void Each_language_has_the_name_its_errors_lead_with(string engineName, string languageName, string expected)
        => Assert.Equal(expected, SearchBatch.LanguageName(Enum.Parse<SearchEngine>(engineName), Enum.Parse<SearchLanguage>(languageName)));

    // ---- What is sent ----

    [Fact]
    public void Each_statement_of_a_script_is_sent_alone_in_its_own_language()
    {
        var statements = SearchBatch.Statements(Es, "SELECT 1; FROM logs | LIMIT 1;\nshow info; describe logs");

        Assert.Equal(["SELECT 1", "FROM logs | LIMIT 1", "show info", "describe logs"], statements.Select(s => s.Text));
        Assert.Equal(
            [SearchLanguage.Sql, SearchLanguage.Piped, SearchLanguage.Piped, SearchLanguage.Sql],
            statements.Select(s => s.Language));
        Assert.Equal(["SELECT", "FROM", "SHOW", "DESCRIBE"], statements.Select(s => s.Verb));
    }

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select 1;", "select 1")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "  select 1  ;  ", "select 1")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select 1 /* note */ ;", "select 1")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM logs | LIMIT 1 ;\n", "FROM logs | LIMIT 1")]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=logs | head 1;", "source=logs | head 1")]
    [InlineData(nameof(SearchEngine.OpenSearch), "select 1 # done\n;", "select 1")]
    public void The_terminating_semicolon_is_never_sent(string engineName, string script, string expected)
        => Assert.Equal(expected, Only(Enum.Parse<SearchEngine>(engineName), script).Text);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "-- why\nFROM logs | LIMIT 1", "FROM logs | LIMIT 1")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select a -- trailing\n", "select a")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM a // trailing", "FROM a")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select a, /* inner */ b from t", "select a, /* inner */ b from t")]
    [InlineData(nameof(SearchEngine.OpenSearch), "# why\nsource=logs", "source=logs")]
    [InlineData(nameof(SearchEngine.OpenSearch), "select 1 # trailing", "select 1")]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=a\n// inner\n| head 1", "source=a\n// inner\n| head 1")]
    public void Comments_around_a_statement_are_not_sent_and_comments_inside_it_are(
        string engineName, string script, string expected)
        => Assert.Equal(expected, Only(Enum.Parse<SearchEngine>(engineName), script).Text);

    [Fact]
    public void The_start_is_the_first_code_token()
    {
        const string script = "select 1;\n-- next\nFROM b";
        var statements = SearchBatch.Statements(Es, script);

        Assert.Equal([0, script.IndexOf("FROM", StringComparison.Ordinal)], statements.Select(s => s.Start));
    }

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select 1;;", 2)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "; select 1", 2)]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=a; ; source=b", 3)]
    public void A_bare_semicolon_is_an_empty_statement_so_indexes_match_the_editors_split(
        string engineName, string script, int count)
    {
        var statements = SearchBatch.Statements(Enum.Parse<SearchEngine>(engineName), script);

        Assert.Equal(count, statements.Count);
        var empty = Assert.Single(statements, s => s.IsEmpty);
        Assert.Equal("", empty.Text);
        Assert.Equal(";", empty.Verb);
    }

    [Fact]
    public void A_leading_pipe_is_its_own_verb()
        => Assert.Equal("|", Only(Os, "| where a > 1").Verb);

    // ---- Splitting ----

    [Theory]
    [InlineData("select 'a;b' from t; select 2", 2)]
    [InlineData("select 'it''s;' from t", 1)]
    [InlineData("select \"a;b\" from t; select 2", 2)]
    [InlineData("select `a;b` from t", 1)]
    [InlineData("select 1 -- ;\nfrom t", 1)]
    [InlineData("select 1 /* ; /* ; */ ; */ from t", 1)]
    [InlineData("SELECT 1 /* /* */ ; SELECT 2", 1)]
    [InlineData("SELECT 1 // ; SELECT 2", 2)]
    [InlineData("select 'a\\'; select 2", 2)]
    [InlineData("FROM logs | WHERE a == \"x;y\"; FROM b", 2)]
    [InlineData("FROM logs | WHERE a == \"x\\\";y\"", 1)]
    [InlineData("FROM logs | WHERE a == \"\"\"x;\"y\"\"\"; FROM b", 2)]
    [InlineData("FROM logs // ;\n| LIMIT 1", 1)]
    [InlineData("FROM logs /* ; */ | LIMIT 1", 1)]
    // ES|QL block comments nest (measured live): the inner */ does not close the outer comment.
    [InlineData("FROM logs /* /* */ ; FROM b", 1)]
    [InlineData("FROM logs /* a /* b */ ; c */ | LIMIT 1; FROM b", 2)]
    [InlineData("FROM logs | WHERE a == 'x; FROM b", 2)]
    [InlineData("FROM logs -- ; FROM b", 2)]
    [InlineData("FROM logs | KEEP `a;b`", 1)]
    public void Elasticsearch_splits_by_each_statements_own_lexical_rules(string script, int count)
        => Assert.Equal(count, SearchBatch.Statements(Es, script).Count);

    [Theory]
    [InlineData("select 'a;b', \"c;d\" from t; select 2", 2)]
    [InlineData("select 'it\\'s;' from t", 1)]
    [InlineData("select 'it''s;' from t", 1)]
    [InlineData("select \"it\\\";\" from t", 1)]
    [InlineData("select `a;b` from t", 1)]
    [InlineData("select 1 # ;\nfrom t", 1)]
    [InlineData("select 1 -- ;\nfrom t", 1)]
    [InlineData("select 1 --;\nselect 2", 2)]
    [InlineData("select 1 /* /* */ ; select 2", 2)]
    [InlineData("select 1 // ; select 2", 2)]
    [InlineData("source=logs | where a = 'x;y'", 1)]
    [InlineData("source=logs | where a = \"x;y\"", 1)]
    [InlineData("source=logs | where a = 'it\\'s;'", 1)]
    [InlineData("source=logs // ;\n| head 1", 1)]
    [InlineData("source=logs /* ; */ | head 1", 1)]
    [InlineData("source=logs # ; source=b", 2)]
    [InlineData("source=logs -- ; source=b", 2)]
    [InlineData("source=logs | fields `a;b`", 1)]
    public void Opensearch_splits_by_each_statements_own_lexical_rules(string script, int count)
        => Assert.Equal(count, SearchBatch.Statements(Os, script).Count);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select 'abc; select 2")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select \"abc; select 2")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM a | WHERE b == \"x; FROM c")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM a | WHERE b == \"\"\"x; FROM c")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM a | KEEP `x; FROM c")]
    [InlineData(nameof(SearchEngine.OpenSearch), "select `a; select 2")]
    [InlineData(nameof(SearchEngine.OpenSearch), "select \"a\\")]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=a | where b = 'x; source=c")]
    public void An_unterminated_form_runs_to_the_end(string engineName, string script)
        => Assert.Equal(script, Only(Enum.Parse<SearchEngine>(engineName), script).Text);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select /* abc; select 2")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select /* /* */ abc; select 2")]
    [InlineData(nameof(SearchEngine.OpenSearch), "select /* abc; select 2")]
    public void An_unterminated_comment_swallows_the_rest_and_is_not_sent(string engineName, string script)
        => Assert.Equal("select", Only(Enum.Parse<SearchEngine>(engineName), script).Text);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select 1\n\nselect 2", 2)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM logs | LIMIT 1\n\nSELECT 1", 2)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "SELECT a,\n\nb FROM t", 1)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select a\n\nfrom t", 1)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "FROM a\n\n| LIMIT 1", 1)]
    [InlineData(nameof(SearchEngine.Elasticsearch), "select (1\n\nselect 2)", 1)]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=a\n\nsource=b", 2)]
    [InlineData(nameof(SearchEngine.OpenSearch), "select a from t\n\nunion\n\nselect b from u", 1)]
    [InlineData(nameof(SearchEngine.OpenSearch), "source=a |\n\nsearch source=b", 1)]
    public void A_blank_line_before_a_statement_opener_splits(string engineName, string script, int count)
        => Assert.Equal(count, SearchBatch.Statements(Enum.Parse<SearchEngine>(engineName), script).Count);

    [Fact]
    public void A_blank_line_split_sends_each_half_without_the_comment_between()
    {
        var statements = SearchBatch.Statements(Es, "FROM logs | LIMIT 1 // first\n\n// second\nSELECT 1");

        Assert.Equal(["FROM logs | LIMIT 1", "SELECT 1"], statements.Select(s => s.Text));
        Assert.Equal([SearchLanguage.Piped, SearchLanguage.Sql], statements.Select(s => s.Language));
    }

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "   \n ")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "-- only a comment\n")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "// only a comment")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "/* only */")]
    [InlineData(nameof(SearchEngine.Elasticsearch), "/* only /* nested */ */ -- and more")]
    [InlineData(nameof(SearchEngine.OpenSearch), "# only a comment")]
    [InlineData(nameof(SearchEngine.OpenSearch), "// only a comment")]
    [InlineData(nameof(SearchEngine.OpenSearch), "-- only\n/* comments */")]
    public void Whitespace_and_comments_alone_are_no_statement(string engineName, string script)
        => Assert.Empty(SearchBatch.Statements(Enum.Parse<SearchEngine>(engineName), script));

    [Fact]
    public void Words_inside_strings_and_comments_do_not_decide_the_language()
    {
        Assert.Equal(SearchLanguage.Sql, Only(Es, "-- FROM logs\nselect 'FROM | x'").Language);
        Assert.Equal(SearchLanguage.Sql, Only(Os, "/* source=a | head */ select \"| x\"").Language);
    }

    // ---- Plain select (decision D2: what OpenSearch SQL may be asked to page) ----

    [Theory]
    [InlineData("select * from logs")]
    [InlineData("SELECT a, b FROM logs WHERE a > 1 ORDER BY b DESC")]
    [InlineData("Select * From logs")]
    [InlineData("select * from `logs-2026.11.21`")]
    [InlineData("select * from logs.archive where user.name = 'x'")]
    [InlineData("select `limit`, `group` from logs")]
    [InlineData("select * from logs where msg = 'limit 5 group by count(x)'")]
    [InlineData("select * from logs where msg = \"distinct\"")]
    [InlineData("select * from logs /* limit 5 */")]
    [InlineData("select * from logs -- limit 5")]
    [InlineData("select * from logs # union select 1")]
    [InlineData("select count, first, last from logs")]
    [InlineData("select * from logs where match(msg, 'x') and abs(a) > 1")]
    [InlineData("select limits, grouped, countx(a) from logs")]
    public void A_select_with_a_top_level_from_and_none_of_the_excluded_shapes_is_plain(string text)
        => Assert.True(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, text));

    [Theory]
    [InlineData("select * from logs limit 100")]
    [InlineData("select * from logs LIMIT 100")]
    [InlineData("select * from logs Limit 5 offset 10")]
    [InlineData("select * from logs offset 10")]
    [InlineData("select level from logs group by level")]
    [InlineData("select level from logs having level > 1")]
    [InlineData("select * from a join b on a.id = b.id")]
    [InlineData("select a from x union select a from y")]
    [InlineData("select a from x intersect select a from y")]
    [InlineData("select a from x except select a from y")]
    [InlineData("select a from x minus select a from y")]
    [InlineData("select row_number() over (order by a) from logs")]
    [InlineData("select distinct level from logs")]
    [InlineData("select * from (select a from logs) t")]
    [InlineData("select * from logs where a in (select b from x)")]
    [InlineData("select 1")]
    [InlineData("select now()")]
    [InlineData("select extract(year from d)")]
    [InlineData("show tables like 'logs'")]
    [InlineData("describe tables like 'logs'")]
    [InlineData("DELETE FROM logs")]
    [InlineData("(select * from logs)")]
    [InlineData("")]
    public void Anything_else_is_not_plain(string text)
        => Assert.False(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, text));

    [Theory]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("STDDEV")]
    [InlineData("STDDEV_POP")]
    [InlineData("STDDEV_SAMP")]
    [InlineData("VARIANCE")]
    [InlineData("VAR_POP")]
    [InlineData("VAR_SAMP")]
    [InlineData("PERCENTILE")]
    [InlineData("PERCENTILE_APPROX")]
    [InlineData("TAKE")]
    [InlineData("FIRST")]
    [InlineData("LAST")]
    [InlineData("EARLIEST")]
    [InlineData("LATEST")]
    public void An_aggregate_call_is_not_plain_in_any_case_or_spacing(string function)
    {
        Assert.False(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, $"select {function}(a) from logs"));
        Assert.False(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, $"select {function.ToLowerInvariant()} (a) from logs"));
        Assert.False(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, $"select {function} /* c */ (a) from logs"));
        Assert.True(SearchBatch.IsPlainSelect(Os, SearchLanguage.Sql, $"select `{function}(a)` from logs"));
    }

    [Fact]
    public void A_piped_statement_is_never_plain()
        => Assert.False(SearchBatch.IsPlainSelect(Os, SearchLanguage.Piped, "select * from logs"));

    [Fact]
    public void The_statement_overload_reads_the_statements_own_language_and_text()
    {
        var statements = SearchBatch.Statements(Os, "select * from logs; select * from logs limit 5; source=logs | head 5");

        Assert.Equal([true, false, false], statements.Select(s => SearchBatch.IsPlainSelect(Os, s)));
    }

    [Fact]
    public void Elasticsearch_sql_reads_a_double_quoted_word_as_a_name_not_a_keyword()
        => Assert.True(SearchBatch.IsPlainSelect(Es, SearchLanguage.Sql, "select \"limit\" from \"logs-2026.11.21\""));
}
