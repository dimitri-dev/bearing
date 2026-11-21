using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <see cref="SearchScan"/>: the language of each statement (spec Behaviour 4, every row of its table for both
/// engines), and the four languages' own lexical forms — where a statement ends, what is a string, what a
/// comment, what a literal (SC2, SC3). <c>Bearing.Data</c>'s <c>SearchBatch</c> re-implements these rules, so
/// every rule here is a theory row it can be held to.
/// </summary>
public class SearchScanTests
{
    private const SearchEngine Es = SearchEngine.Elasticsearch;
    private const SearchEngine Os = SearchEngine.OpenSearch;

    private static SearchLanguage LanguageOf(SearchEngine engine, string sql)
        => Assert.Single(SearchScan.Statements(engine, sql)).Language;

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
    public void Elasticsearch_sql_lead_words_read_as_sql(string sql)
        => Assert.Equal(SearchLanguage.Sql, LanguageOf(Es, sql));

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
    public void Esql_lead_words_read_as_piped(string sql)
        => Assert.Equal(SearchLanguage.Piped, LanguageOf(Es, sql));

    [Theory]
    [InlineData("foo | bar", SearchLanguage.Piped)]
    [InlineData("foo bar", SearchLanguage.Sql)]
    [InlineData("foo 'a | b'", SearchLanguage.Sql)]
    [InlineData("foo \"a | b\"", SearchLanguage.Sql)]
    [InlineData("foo -- |\n", SearchLanguage.Sql)]
    [InlineData("foo /* | */ bar", SearchLanguage.Sql)]
    [InlineData("foo (a | b)", SearchLanguage.Sql)]
    [InlineData("foo || bar", SearchLanguage.Sql)]
    public void An_unknown_first_word_is_piped_only_with_a_top_level_pipe_on_elasticsearch(string sql, SearchLanguage expected)
    {
        var only = Assert.Single(SearchScan.Statements(Es, sql));
        Assert.Equal(expected, only.Language);
        Assert.False(only.LanguageFromLeadWord);
    }

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
    public void Opensearch_sql_lead_words_read_as_sql(string sql)
        => Assert.Equal(SearchLanguage.Sql, LanguageOf(Os, sql));

    [Theory]
    [InlineData("search source=logs")]
    [InlineData("source=logs | head 5")]
    [InlineData("source = logs")]
    [InlineData("SOURCE=logs")]
    [InlineData("index=logs")]
    [InlineData("describe logs")]
    [InlineData("DESCRIBE logs")]
    [InlineData("show datasources")]
    [InlineData("SHOW")]
    [InlineData("explain source=logs | head 1")]
    [InlineData("multisearch [search source=a] [search source=b]")]
    [InlineData("union [source=a] [source=b]")]
    [InlineData("makeresults count=1")]
    [InlineData("graphlookup x")]
    [InlineData("| source=logs")]
    [InlineData("// a note\nsource=logs")]
    public void Ppl_lead_words_read_as_piped(string sql)
        => Assert.Equal(SearchLanguage.Piped, LanguageOf(Os, sql));

    [Theory]
    [InlineData("foo | bar", SearchLanguage.Piped)]
    [InlineData("foo bar", SearchLanguage.Sql)]
    [InlineData("foo '|'", SearchLanguage.Sql)]
    [InlineData("foo \"|\"", SearchLanguage.Sql)]
    [InlineData("foo `a|b`", SearchLanguage.Sql)]
    [InlineData("foo # |\n", SearchLanguage.Sql)]
    [InlineData("foo -- |", SearchLanguage.Sql)]
    [InlineData("foo --| bar", SearchLanguage.Piped)]
    [InlineData("foo /* | */ bar", SearchLanguage.Sql)]
    public void An_unknown_first_word_is_piped_only_with_a_top_level_pipe_on_opensearch(string sql, SearchLanguage expected)
    {
        var only = Assert.Single(SearchScan.Statements(Os, sql));
        Assert.Equal(expected, only.Language);
        Assert.False(only.LanguageFromLeadWord);
    }

    [Fact]
    public void Lead_words_skip_comments_and_are_upper_cased()
    {
        var only = Assert.Single(SearchScan.Statements(Es, "/* x */ show -- y\n info"));
        Assert.Equal(new[] { "SHOW", "INFO" }, only.LeadWords);
        Assert.Equal("SHOW", only.Verb);
        Assert.True(only.LanguageFromLeadWord);
    }

    [Fact]
    public void A_leading_pipe_has_no_lead_words_and_is_its_own_verb()
    {
        var only = Assert.Single(SearchScan.Statements(Os, "| where a > 1"));
        Assert.Empty(only.LeadWords);
        Assert.Equal("|", only.Verb);
    }

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "FROM logs | WHERE a > 1 | LIMIT 5", "WHERE,LIMIT")]
    [InlineData(SearchEngine.Elasticsearch, "FROM logs | EVAL x = \"a | b\" | KEEP x", "EVAL,KEEP")]
    [InlineData(SearchEngine.Elasticsearch, "FROM logs // | not this\n| keep x", "KEEP")]
    [InlineData(SearchEngine.OpenSearch, "source=logs | fields a, b | head 5", "FIELDS,HEAD")]
    [InlineData(SearchEngine.OpenSearch, "source=logs | where a = '|' | head", "WHERE,HEAD")]
    [InlineData(SearchEngine.OpenSearch, "select a from logs", "")]
    public void Pipe_commands_are_the_first_word_of_each_top_level_segment(SearchEngine engine, string sql, string expected)
    {
        var only = Assert.Single(SearchScan.Statements(engine, sql));
        Assert.Equal(expected, string.Join(",", only.PipeCommands));
    }

    [Fact]
    public void Each_statement_of_a_script_takes_its_own_language()
    {
        const string script = "SELECT 1; FROM logs | LIMIT 1;\nshow info; describe logs";
        var statements = SearchScan.Statements(Es, script);
        Assert.Equal(
            new[] { SearchLanguage.Sql, SearchLanguage.Piped, SearchLanguage.Piped, SearchLanguage.Sql },
            statements.Select(s => s.Language));
        Assert.Equal("SELECT 1; ", statements[0].Span.Text);
        Assert.Equal("FROM logs | LIMIT 1;\n", statements[1].Span.Text);
    }

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
    public void Elasticsearch_splits_by_each_statements_own_lexical_rules(string sql, int count)
        => Assert.Equal(count, SearchScan.Split(Es, sql).Count);

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
    public void Opensearch_splits_by_each_statements_own_lexical_rules(string sql, int count)
        => Assert.Equal(count, SearchScan.Split(Os, sql).Count);

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select 'abc; select 2")]
    [InlineData(SearchEngine.Elasticsearch, "select \"abc; select 2")]
    [InlineData(SearchEngine.Elasticsearch, "select /* abc; select 2")]
    [InlineData(SearchEngine.Elasticsearch, "select /* /* */ abc; select 2")]
    [InlineData(SearchEngine.Elasticsearch, "FROM a | WHERE b == \"x; FROM c")]
    [InlineData(SearchEngine.Elasticsearch, "FROM a | WHERE b == \"\"\"x; FROM c")]
    [InlineData(SearchEngine.Elasticsearch, "FROM a | KEEP `x; FROM c")]
    [InlineData(SearchEngine.OpenSearch, "select `a; select 2")]
    [InlineData(SearchEngine.OpenSearch, "select \"a\\")]
    [InlineData(SearchEngine.OpenSearch, "source=a | where b = 'x; source=c")]
    [InlineData(SearchEngine.OpenSearch, "source=a /* x; source=c")]
    public void An_unterminated_form_runs_to_the_end_without_throwing(SearchEngine engine, string sql)
    {
        var only = Assert.Single(SearchScan.Split(engine, sql));
        Assert.Equal(sql, only.Text);
        Assert.False(SearchScan.Tokens(engine, sql)[^1].Terminated);
    }

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select 1\n\nselect 2", 2)]
    [InlineData(SearchEngine.Elasticsearch, "FROM logs | LIMIT 1\n\nSELECT 1", 2)]
    [InlineData(SearchEngine.Elasticsearch, "SELECT a,\n\nb FROM t", 1)]
    [InlineData(SearchEngine.Elasticsearch, "select a\n\nfrom t", 1)]
    [InlineData(SearchEngine.Elasticsearch, "FROM a\n\n| LIMIT 1", 1)]
    [InlineData(SearchEngine.Elasticsearch, "select (1\n\nselect 2)", 1)]
    [InlineData(SearchEngine.OpenSearch, "source=a\n\nsource=b", 2)]
    [InlineData(SearchEngine.OpenSearch, "select a from t\n\nunion\n\nselect b from u", 1)]
    [InlineData(SearchEngine.OpenSearch, "source=a |\n\nsearch source=b", 1)]
    public void A_blank_line_before_a_statement_opener_splits(SearchEngine engine, string sql, int count)
        => Assert.Equal(count, SearchScan.Split(engine, sql).Count);

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "")]
    [InlineData(SearchEngine.Elasticsearch, "   \n ")]
    [InlineData(SearchEngine.Elasticsearch, "-- only a comment\n")]
    [InlineData(SearchEngine.Elasticsearch, "// only a comment")]
    [InlineData(SearchEngine.Elasticsearch, "/* only */")]
    [InlineData(SearchEngine.OpenSearch, "# only a comment")]
    [InlineData(SearchEngine.OpenSearch, "// only a comment")]
    public void Whitespace_and_comments_alone_are_no_statement(SearchEngine engine, string sql)
        => Assert.Empty(SearchScan.Split(engine, sql));

    /// <summary>
    /// Plain SQL whose lexical forms Elasticsearch SQL and PostgreSQL share (<c>'…'</c>, <c>--</c>,
    /// <c>/* */</c>): the spans must be <see cref="StatementSplitter"/>'s, so the editor's highlight, folding
    /// and run-at-caret behave as on every other engine.
    /// </summary>
    [Theory]
    [InlineData("select 1; select 2")]
    [InlineData("select 1;\nselect 2;\n")]
    [InlineData("-- header\nselect 1;\n\n-- lead\nselect 2")]
    [InlineData("select 1;\n\n-- COMMIT;")]
    [InlineData("select 1; -- note\nselect 2")]
    [InlineData("select a,\n-- inner\nb from t;\nselect 3")]
    [InlineData("select 1\n\nselect 2")]
    [InlineData("  select 1  ;  select 2  ")]
    [InlineData("select 'a;b' from t; /* c; */ select 2")]
    [InlineData("select 1;\n-- one\n-- two\nselect 2;\n\n/* tail */\n")]
    public void Spans_follow_the_postgres_splitters_contract(string sql)
    {
        var expected = StatementSplitter.Split(PostgresDialect.Instance, sql);
        var actual = SearchScan.Split(Es, sql);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_leading_comment_block_starts_the_statement_it_documents()
    {
        const string sql = "source=a;\n# what b is\nsource=b";
        var spans = SearchScan.Split(Os, sql);

        Assert.Equal(2, spans.Count);
        Assert.Equal(sql.IndexOf("# what", StringComparison.Ordinal), spans[1].Start);
    }

    // ---- Strings ----

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select 'abc' from t", "ab", true)]
    [InlineData(SearchEngine.Elasticsearch, "select \"abc\" from t", "ab", false)]
    [InlineData(SearchEngine.Elasticsearch, "select abc from t", "ab", false)]
    [InlineData(SearchEngine.Elasticsearch, "select 'ab", "ab", true)]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | WHERE a == \"abc\"", "ab", true)]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | WHERE a == \"\"\"abc\"\"\"", "ab", true)]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | WHERE a == 'abc'", "ab", false)]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | KEEP `abc`", "ab", false)]
    [InlineData(SearchEngine.OpenSearch, "select \"abc\" from t", "ab", true)]
    [InlineData(SearchEngine.OpenSearch, "select 'abc' from t", "ab", true)]
    [InlineData(SearchEngine.OpenSearch, "select `abc` from t", "ab", false)]
    [InlineData(SearchEngine.OpenSearch, "select 1 # 'abc'", "ab", false)]
    [InlineData(SearchEngine.OpenSearch, "source=t | where a = 'abc'", "ab", true)]
    [InlineData(SearchEngine.OpenSearch, "source=t | where a = \"abc\"", "ab", true)]
    [InlineData(SearchEngine.OpenSearch, "source=t // 'abc'", "ab", false)]
    public void A_string_is_whatever_the_statements_language_calls_one(
        SearchEngine engine, string sql, string marker, bool inside)
        => Assert.Equal(inside, SearchScan.InString(engine, sql, sql.IndexOf(marker, StringComparison.Ordinal) + 1));

    [Fact]
    public void The_opening_quote_itself_is_outside()
    {
        const string sql = "select 'abc'";
        Assert.False(SearchScan.InString(Es, sql, sql.IndexOf('\'')));
        Assert.True(SearchScan.InString(Es, sql, sql.Length));
    }

    // ---- Line comments ----

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select 1 -- x", true)]
    [InlineData(SearchEngine.Elasticsearch, "select 1 // x", false)]
    [InlineData(SearchEngine.Elasticsearch, "select '-- x'", false)]
    [InlineData(SearchEngine.Elasticsearch, "select 1 /* x */", false)]
    [InlineData(SearchEngine.Elasticsearch, "FROM a // x", true)]
    [InlineData(SearchEngine.Elasticsearch, "FROM a -- x", false)]
    [InlineData(SearchEngine.Elasticsearch, "FROM a | WHERE b == \"// x\"", false)]
    [InlineData(SearchEngine.OpenSearch, "select 1 # x", true)]
    [InlineData(SearchEngine.OpenSearch, "select 1 -- x", true)]
    [InlineData(SearchEngine.OpenSearch, "select 1 --x", false)]
    [InlineData(SearchEngine.OpenSearch, "select 1 // x", false)]
    [InlineData(SearchEngine.OpenSearch, "source=a // x", true)]
    [InlineData(SearchEngine.OpenSearch, "source=a # x", false)]
    [InlineData(SearchEngine.OpenSearch, "source=a -- x", false)]
    public void A_line_comment_is_whatever_the_fragments_language_calls_one(SearchEngine engine, string fragment, bool expected)
        => Assert.Equal(expected, SearchScan.EndsWithLineComment(engine, fragment));

    // ---- Code end ----

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select 1;", 9)]
    [InlineData(SearchEngine.Elasticsearch, "select 1 -- trailing\n", 8)]
    [InlineData(SearchEngine.Elasticsearch, "FROM a // trailing", 6)]
    [InlineData(SearchEngine.OpenSearch, "select 1 # trailing", 8)]
    [InlineData(SearchEngine.OpenSearch, "source=a // trailing", 8)]
    [InlineData(SearchEngine.OpenSearch, "# only", null)]
    public void Code_ends_at_the_last_code_token(SearchEngine engine, string sql, int? expected)
        => Assert.Equal(expected, SearchScan.CodeEnd(engine, sql));

    // ---- Redaction ----

    [Theory]
    [InlineData(SearchEngine.Elasticsearch, "select a from t where b = 'x' and c = 42", "select a from t where b = '?' and c = ?")]
    [InlineData(SearchEngine.Elasticsearch, "select \"x\" from t where b = 'it''s'", "select \"x\" from t where b = '?'")]
    [InlineData(SearchEngine.Elasticsearch, "select 1 -- 'x'", "select ? -- 'x'")]
    [InlineData(SearchEngine.Elasticsearch, "select 'abc", "select '?")]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | WHERE b == \"x\" AND c > 1.5", "FROM t | WHERE b == \"?\" AND c > ?")]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | WHERE b == \"\"\"a\"b\"\"\"", "FROM t | WHERE b == \"\"\"?\"\"\"")]
    [InlineData(SearchEngine.Elasticsearch, "FROM t | KEEP `x1` // 'y'", "FROM t | KEEP `x1` // 'y'")]
    [InlineData(SearchEngine.Elasticsearch, "select 'a' from t; FROM t | WHERE b == \"x\"", "select '?' from t; FROM t | WHERE b == \"?\"")]
    [InlineData(SearchEngine.OpenSearch, "select * from t where a = \"x\" and b = 'y' and c = 3", "select * from t where a = \"?\" and b = '?' and c = ?")]
    [InlineData(SearchEngine.OpenSearch, "source=t | where a = 'it\\'s' | head 5", "source=t | where a = '?' | head ?")]
    [InlineData(SearchEngine.OpenSearch, "select `a1` from t # 'x'", "select `a1` from t # 'x'")]
    public void Literals_are_redacted_by_each_statements_language(SearchEngine engine, string sql, string expected)
        => Assert.Equal(expected, SearchScan.RedactLiterals(engine, sql));

    // ---- Write guard: machine-learning commands ----

    /// <summary>Only a command position counts: after a top-level or nested <c>|</c>, or at the head of a
    /// <c>fork</c> branch — never inside a string, a comment or a backticked name.</summary>
    [Theory]
    [InlineData("source=logs | ml action='train'", true)]
    [InlineData("source=logs | where a = 1 | KMeans centroids=2", true)]
    [InlineData("source=logs | fork (head 1) (ad time_field='t')", true)]
    [InlineData("source=logs // | ml action='train'", false)]
    [InlineData("source=logs | where msg = '| ml'", false)]
    [InlineData("source=logs | fields `ml`, ad", false)]
    [InlineData("source=logs | rename ml as kmeans", false)]
    public void An_opensearch_ml_command_is_flagged_only_in_command_position(string sql, bool risky)
    {
        var only = Assert.Single(WriteGuard.Describe(OpenSearchDialect.Instance, sql));
        Assert.Equal(risky, only.IsRisky);
        Assert.Equal(risky, only.FromConservativeDefault);
        Assert.False(only.NamesAWrite);
    }

    [Fact]
    public void Redacting_nothing_returns_the_input()
    {
        Assert.Equal("", SearchScan.RedactLiterals(Es, null));
        Assert.Equal("select a from t", SearchScan.RedactLiterals(Os, "select a from t"));
    }
}
