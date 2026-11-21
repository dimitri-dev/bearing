using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The text half of Elasticsearch support (SC4): quoting (a dotted name quoted whole), the paging, counting
/// and editing hooks that must answer "not on this engine", the write guard over Elasticsearch SQL and ES|QL,
/// and what an exposed connection runs and refuses (spec Behaviour 7 and 11).
/// </summary>
public class ElasticsearchDialectTests
{
    private static readonly ISqlDialect Es = ElasticsearchDialect.Instance;

    private static string? External(string sql) => ExternalSqlPolicy.Refuse(Es, sql);

    [Fact]
    public void Id_matches_the_provider_id() => Assert.Equal("elasticsearch", Es.Id);

    // ---- Identifiers ----

    [Theory]
    [InlineData("logs", "logs")]
    [InlineData("user.name", "\"user.name\"")]
    [InlineData("title.keyword", "\"title.keyword\"")]
    [InlineData("a.b.c", "\"a.b.c\"")]
    [InlineData("logs-2026.11.21", "\"logs-2026.11.21\"")]
    [InlineData("user_name", "user_name")]
    [InlineData("logs-2024", "\"logs-2024\"")]
    [InlineData("logs*", "\"logs*\"")]
    [InlineData("Logs", "\"Logs\"")]
    [InlineData("@timestamp", "\"@timestamp\"")]
    [InlineData(".kibana", "\".kibana\"")]
    [InlineData("a..b", "\"a..b\"")]
    [InlineData("a.", "\"a.\"")]
    [InlineData("1abc", "\"1abc\"")]
    [InlineData("my index", "\"my index\"")]
    [InlineData("select", "\"select\"")]
    [InlineData("match", "\"match\"")]
    [InlineData("user.select", "\"user.select\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("", "\"\"")]
    public void Plain_words_stay_bare_and_anything_else_dotted_paths_included_is_quoted_whole(string name, string expected)
    {
        Assert.Equal(expected, Es.QuoteIfNeeded(name));
        Assert.Equal(expected != name, Es.NeedsQuoting(name));
    }

    [Fact]
    public void Quote_and_unquote_round_trip()
    {
        Assert.Equal("\"a\"\"b\"", Es.Quote("a\"b"));
        Assert.Equal("a\"b", Es.Unquote("\"a\"\"b\""));
        Assert.Equal("\"logs\"", Es.Quote("logs"));
    }

    // ---- Not on this engine ----

    [Fact]
    public void Select_top_rows_ends_with_a_limit()
        => Assert.Equal("select * from \"logs-1\" limit 100", Es.SelectTopRows("\"logs-1\"", 100));

    [Fact]
    public void Paging_counting_and_scan_hooks_answer_null()
    {
        Assert.Null(Es.TryAppendPage("select * from logs", 0, 100));
        Assert.Null(Es.Wrap("select * from logs", 0, 100));
        Assert.Null(Es.CountWrap("select * from logs"));
        Assert.Null(Es.ScanTargets("select * from logs"));
        Assert.False(Es.ReadsLikePostgresForCounting("select * from logs", []));
    }

    [Fact]
    public void Editing_plans_and_scripts_are_not_offered()
    {
        Assert.Throws<NotSupportedException>(() => Es.InsertStatement("\"t\"", "a", "@p0", true));
        Assert.Throws<NotSupportedException>(() => Es.UpdateStatement("\"t\"", "a = @p0", "id = @p1", null));
        Assert.Null(Es.TryEditExpression("now()"));
        Assert.Empty(Es.EditExpressions);
        Assert.Empty(Es.OfferedEditExpressions);
        Assert.False(Es.SupportsExplainPlan);
        Assert.NotNull(Es.FormatRefusal);
        Assert.Empty(Es.FindVariables("select * from logs where a = :x"));
        Assert.Empty(Es.FindDirectives("@set x = 1\nselect * from logs where a = :x"));
        Assert.Null(Es.TransactionControl(["BEGIN"]));
        Assert.False(Es.CommitsImplicitly(["CREATE"]));
    }

    [Fact]
    public void Completion_reads_with_the_postgres_grammar()
        => Assert.Same(PgParseRules.Instance, Es.ParseRules);

    // ---- Write guard ----

    [Theory]
    [InlineData("SELECT * FROM logs")]
    [InlineData("show tables")]
    [InlineData("DESCRIBE logs")]
    [InlineData("desc logs")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SYS COLUMNS")]
    [InlineData("DEBUG SELECT 1")]
    [InlineData("FROM logs | WHERE a > 1 | LIMIT 5")]
    [InlineData("ROW a = 1")]
    [InlineData("TS metrics | LIMIT 1")]
    [InlineData("PROMQL up")]
    [InlineData("SHOW INFO")]
    [InlineData("FROM logs | WHERE msg == \"| INTO x\"")]
    [InlineData("FROM logs // | INTO x")]
    [InlineData("SELECT \"into\" FROM logs")]
    public void A_known_read_is_not_risky(string sql)
    {
        Assert.True(Es.HasDialectAwareGuard);
        var only = Assert.Single(WriteGuard.Describe(Es, sql));
        Assert.False(only.IsRisky);
        Assert.True(only.GuardIsDialectAware);
    }

    [Theory]
    [InlineData("FROM logs | INTO other")]
    [InlineData("FROM logs | where a > 1 | into other")]
    [InlineData("FROM logs | FORK (WHERE a | INTO x) (WHERE b)")]
    public void An_esql_into_command_names_a_write(string sql)
    {
        var only = Assert.Single(WriteGuard.Describe(Es, sql));
        Assert.True(only.NamesAWrite);
        Assert.False(only.FromConservativeDefault);
        Assert.Equal("INTO", only.Label);
    }

    [Theory]
    [InlineData("foo bar", "FOO")]
    [InlineData("foo | bar", "FOO")]
    [InlineData("(SELECT 1)", "(")]
    [InlineData("SET x = 1", "SET")]
    [InlineData("DELETE FROM logs", "DELETE")]
    public void An_unknown_first_word_is_risky_by_the_conservative_default(string sql, string verb)
    {
        var only = Assert.Single(WriteGuard.Describe(Es, sql));
        Assert.True(only.IsRisky);
        Assert.True(only.FromConservativeDefault);
        Assert.False(only.NamesAWrite);
        Assert.Equal(verb, only.Verb);
    }

    [Fact]
    public void A_mixed_batch_is_classified_statement_by_statement()
    {
        var described = WriteGuard.Describe(Es, "SELECT 1; FROM logs | INTO x; foo; FROM logs | LIMIT 1");
        Assert.Equal(new[] { false, true, true, false }, described.Select(s => s.IsRisky));
        Assert.Equal(new[] { "SELECT", "FROM", "FOO", "FROM" }, described.Select(s => s.Verb));
    }

    /// <summary>A statement with no code but <c>;</c> sends nothing, so it is not described (follow-up F1):
    /// a stray <c>;;</c> neither prompts nor is refused. The real statements around it still are.</summary>
    [Theory]
    [InlineData("select 1;;", new[] { "SELECT" })]
    [InlineData(";", new string[0])]
    [InlineData("; select 1", new[] { "SELECT" })]
    [InlineData("select 1; ; from x | limit 1", new[] { "SELECT", "FROM" })]
    [InlineData("select 1; /* c */ ; // d\n;", new[] { "SELECT" })]
    [InlineData("select 1; ; foo", new[] { "SELECT", "FOO" })]
    public void A_statement_of_only_semicolons_is_not_described(string sql, string[] verbs)
    {
        var described = WriteGuard.Describe(Es, sql);
        Assert.Equal(verbs, described.Select(s => s.Verb));
        Assert.Equal(verbs.Select(v => v == "FOO"), described.Select(s => s.IsRisky));
    }

    [Fact]
    public void A_stray_double_semicolon_runs_on_an_exposed_connection() => Assert.Null(External("select 1;;"));

    [Fact]
    public void A_lone_semicolon_is_no_statement_to_run()
        => Assert.Equal("There is no statement here to run.", External(";"));

    // ---- Exposed connections ----

    [Theory]
    [InlineData("SELECT * FROM logs")]
    [InlineData("show tables")]
    [InlineData("DESCRIBE logs")]
    [InlineData("DESC logs")]
    [InlineData("FROM logs | LIMIT 5")]
    [InlineData("ROW a = 1")]
    [InlineData("TS metrics | LIMIT 1")]
    [InlineData("SHOW INFO")]
    [InlineData("SELECT user.name FROM logs WHERE msg = '.x'")]
    [InlineData("SELECT a.b.c, t.* FROM logs t")]
    [InlineData("FROM logs | WHERE msg == \".x\" AND a > .5")]
    [InlineData("FROM logs | KEEP completion, rerank")]
    [InlineData("FROM logs | EVAL x = 0.5")]
    [InlineData("ROW a = \".x\"")]
    public void A_read_on_the_allow_list_runs(string sql) => Assert.Null(External(sql));

    [Theory]
    [InlineData("EXPLAIN SELECT 1", "not a read")]
    [InlineData("SYS COLUMNS", "not a read")]
    [InlineData("PROMQL up", "not a read")]
    [InlineData("foo bar", "'FOO' is not a read")]
    [InlineData("FROM logs | INTO other", "'INTO' writes")]
    public void Anything_else_is_refused(string sql, string why)
    {
        var refusal = External(sql);
        Assert.NotNull(refusal);
        Assert.Contains(why, refusal);
    }

    [Theory]
    [InlineData("FROM logs | COMPLETION answer = prompt WITH { \"inference_id\": \"x\" }", "'COMPLETION'", "inference endpoint")]
    [InlineData("FROM logs | RERANK \"q\" ON title", "'RERANK'", "may call")]
    [InlineData("FROM logs | FORK (WHERE a | COMPLETION x) (WHERE b)", "'COMPLETION'", "inference")]
    [InlineData("FROM logs | FORK (COMPLETION x) (WHERE b)", "'COMPLETION'", "inference")]
    [InlineData("SELECT * FROM \".security\"", "'.security'", "hidden or system index")]
    [InlineData("SELECT * FROM .kibana", "'.kibana'", "hidden or system index")]
    [InlineData("FROM .kibana | LIMIT 1", "'.kibana'", "hidden or system index")]
    [InlineData("FROM \".kibana\" | LIMIT 1", "'.kibana'", "hidden or system index")]
    [InlineData("FROM \"logs,.security\"", "'.security'", "hidden or system index")]
    [InlineData("FROM logs,.kibana*", "'.kibana'", "hidden or system index")]
    [InlineData("FROM remote:.kibana", "'.kibana'", "hidden or system index")]
    [InlineData("FROM logs | LOOKUP JOIN .enrich ON id", "'.enrich'", "hidden or system index")]
    public void Inference_commands_and_dot_indices_are_refused_by_name(string sql, string named, string why)
    {
        var refusal = External(sql);
        Assert.NotNull(refusal);
        Assert.Contains(named, refusal);
        Assert.Contains(why, refusal);
        Assert.Contains("not a boundary", refusal);
        Assert.DoesNotContain("credentials", refusal);
    }

    [Fact]
    public void An_into_command_is_also_denied_by_name_for_a_direct_caller()
    {
        var denied = Es.ExternalDeniedStatement("FROM logs | INTO other");
        Assert.NotNull(denied);
        Assert.Contains("'INTO'", denied);
    }

    [Fact]
    public void The_name_scan_reads_this_engines_languages()
    {
        var (called, mentioned) = Es.ExternalNameScan("SELECT count(a) FROM \"logs\" WHERE b = 'pg_authid'");
        Assert.Contains("count", called);
        Assert.Contains("logs", mentioned);
        Assert.Contains("a", mentioned);
        Assert.DoesNotContain("pg_authid", mentioned);
        Assert.Empty(Es.ExternalDeniedFunctions);
        Assert.Empty(Es.ExternalDeniedRelations);
    }

    // ---- Completion gate ----

    [Theory]
    [InlineData("SELECT * FROM ", true)]
    [InlineData("FROM logs | WHERE ", false)]
    [InlineData("", true)]
    public void Completion_answers_only_inside_a_sql_statement(string sql, bool expected)
        => Assert.Equal(expected, Es.CompletesAt(sql, sql.Length));
}
