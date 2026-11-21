using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The text half of OpenSearch support (SC4): backtick quoting with bare dotted field paths, the paging,
/// counting and editing hooks that must answer "not on this engine", the write guard over OpenSearch SQL and
/// PPL, and what an exposed connection runs and refuses (spec Behaviour 7 and 11).
/// </summary>
public class OpenSearchDialectTests
{
    private static readonly ISqlDialect Os = OpenSearchDialect.Instance;

    private static string? External(string sql) => ExternalSqlPolicy.Refuse(Os, sql);

    [Fact]
    public void Id_matches_the_provider_id() => Assert.Equal("opensearch", Os.Id);

    // ---- Identifiers ----

    [Theory]
    [InlineData("logs", "logs")]
    [InlineData("Logs", "Logs")]
    [InlineData("user.name", "user.name")]
    [InlineData("title.keyword", "title.keyword")]
    [InlineData("logs-2024", "`logs-2024`")]
    [InlineData("logs*", "`logs*`")]
    [InlineData("@timestamp", "`@timestamp`")]
    [InlineData(".kibana", "`.kibana`")]
    [InlineData("a..b", "`a..b`")]
    [InlineData("1abc", "`1abc`")]
    [InlineData("my index", "`my index`")]
    [InlineData("select", "`select`")]
    [InlineData("user.order", "`user.order`")]
    [InlineData("a`b", "`a``b`")]
    [InlineData("", "``")]
    public void Plain_words_and_dotted_paths_stay_bare_and_anything_else_is_backticked_whole(string name, string expected)
    {
        Assert.Equal(expected, Os.QuoteIfNeeded(name));
        Assert.Equal(expected != name, Os.NeedsQuoting(name));
    }

    [Fact]
    public void Quote_and_unquote_round_trip()
    {
        Assert.Equal("`a``b`", Os.Quote("a`b"));
        Assert.Equal("a`b", Os.Unquote("`a``b`"));
    }

    // ---- Not on this engine ----

    [Fact]
    public void Select_top_rows_ends_with_a_limit()
        => Assert.Equal("select * from `logs-1` limit 100", Os.SelectTopRows("`logs-1`", 100));

    [Fact]
    public void Paging_counting_and_scan_hooks_answer_null()
    {
        Assert.Null(Os.TryAppendPage("select * from logs", 0, 100));
        Assert.Null(Os.Wrap("select * from logs", 0, 100));
        Assert.Null(Os.CountWrap("select * from logs"));
        Assert.Null(Os.ScanTargets("select * from logs"));
        Assert.False(Os.ReadsLikePostgresForCounting("select * from logs", []));
    }

    [Fact]
    public void Editing_plans_and_scripts_are_not_offered()
    {
        Assert.Throws<NotSupportedException>(() => Os.InsertStatement("`t`", "a", "@p0", true));
        Assert.Throws<NotSupportedException>(() => Os.UpdateStatement("`t`", "a = @p0", "id = @p1", null));
        Assert.Null(Os.TryEditExpression("now()"));
        Assert.Empty(Os.EditExpressions);
        Assert.Empty(Os.OfferedEditExpressions);
        Assert.False(Os.SupportsExplainPlan);
        Assert.NotNull(Os.FormatRefusal);
        Assert.Empty(Os.FindVariables("select * from logs where a = :x"));
        Assert.Empty(Os.FindDirectives("@set x = 1\nselect * from logs where a = :x"));
        Assert.Null(Os.TransactionControl(["BEGIN"]));
        Assert.False(Os.CommitsImplicitly(["CREATE"]));
    }

    [Fact]
    public void Completion_reads_with_the_mysql_grammar()
        => Assert.Same(MySqlParseRules.Instance, Os.ParseRules);

    // ---- Write guard ----

    [Theory]
    [InlineData("SELECT * FROM logs")]
    [InlineData("SHOW TABLES LIKE 'logs%'")]
    [InlineData("DESCRIBE TABLES LIKE 'logs'")]
    [InlineData("describe logs")]
    [InlineData("show datasources")]
    [InlineData("search source=logs")]
    [InlineData("source=logs | head 5")]
    [InlineData("index=logs | fields a")]
    [InlineData("explain source=logs")]
    [InlineData("multisearch [search source=a] [search source=b]")]
    [InlineData("union [source=a] [source=b]")]
    [InlineData("makeresults count=1")]
    [InlineData("graphlookup x")]
    [InlineData("| source=logs")]
    [InlineData("select * from logs where note = 'delete from logs'")]
    [InlineData("select `delete` from logs")]
    public void A_known_read_is_not_risky(string sql)
    {
        Assert.True(Os.HasDialectAwareGuard);
        var only = Assert.Single(WriteGuard.Describe(Os, sql));
        Assert.False(only.IsRisky);
    }

    [Theory]
    [InlineData("DELETE FROM logs WHERE a = 1")]
    [InlineData("-- a note\ndelete from logs")]
    public void Delete_names_a_write(string sql)
    {
        var only = Assert.Single(WriteGuard.Describe(Os, sql));
        Assert.True(only.NamesAWrite);
        Assert.False(only.FromConservativeDefault);
        Assert.Equal("DELETE", only.Label);
    }

    [Theory]
    [InlineData("foo bar", "FOO")]
    [InlineData("foo | bar", "FOO")]
    [InlineData("rest /_cat/indices", "REST")]
    [InlineData("update logs set a = 1", "UPDATE")]
    [InlineData("(select 1)", "(")]
    public void An_unknown_first_word_is_risky_by_the_conservative_default(string sql, string verb)
    {
        var only = Assert.Single(WriteGuard.Describe(Os, sql));
        Assert.True(only.IsRisky);
        Assert.True(only.FromConservativeDefault);
        Assert.Equal(verb, only.Verb);
    }

    /// <summary>A PPL machine-learning command (<c>ml action='train'</c> stores a model on the cluster) makes a
    /// known read risky — by the conservative default, because Bearing cannot tell which call writes (§1.2).</summary>
    [Theory]
    [InlineData("source=logs | ml action='train' algorithm='rcf'", "ML")]
    [InlineData("source=logs | kmeans centroids=3", "KMEANS")]
    [InlineData("source=logs | AD time_field='t'", "AD")]
    [InlineData("search source=logs | where a > 1 | ml action='predict'", "ML")]
    [InlineData("source=logs | fork (where a = 1 | head 1) (kmeans centroids=2)", "KMEANS")]
    [InlineData("source=logs | ml action='train' | kmeans centroids=2 | ml action='x'", "ML + KMEANS")]
    public void A_machine_learning_command_is_risky_by_the_conservative_default(string sql, string label)
    {
        var only = Assert.Single(WriteGuard.Describe(Os, sql));
        Assert.True(only.IsRisky);
        Assert.True(only.FromConservativeDefault);
        Assert.False(only.NamesAWrite);
        Assert.Equal(label, only.Label);
    }

    [Theory]
    [InlineData("source=logs | fields ml, ad")]
    [InlineData("source=ml | head 5")]
    [InlineData("source=logs | where ad = 1 | stats count() by kmeans")]
    [InlineData("source=logs | where msg = '| ml action=train'")]
    [InlineData("SELECT ml, kmeans, ad FROM logs")]
    [InlineData("SELECT * FROM ml")]
    public void A_field_or_index_named_like_a_machine_learning_command_is_still_a_read(string sql)
    {
        var only = Assert.Single(WriteGuard.Describe(Os, sql));
        Assert.False(only.IsRisky);
    }

    [Fact]
    public void A_machine_learning_command_is_still_refused_on_an_exposed_connection_in_the_dialects_words()
    {
        var refusal = External("source=logs | ml action='train' algorithm='rcf'");
        Assert.NotNull(refusal);
        Assert.Contains("'ml'", refusal);
        Assert.Contains("machine-learning", refusal);
        Assert.DoesNotContain("writes", refusal);
    }

    [Fact]
    public void A_mixed_batch_is_classified_statement_by_statement()
    {
        var described = WriteGuard.Describe(Os, "select 1; delete from logs; source=logs | head 1; foo");
        Assert.Equal(new[] { false, true, false, true }, described.Select(s => s.IsRisky));
    }

    /// <summary>A statement with no code but <c>;</c> sends nothing, so it is not described (follow-up F1):
    /// a stray <c>;;</c> neither prompts nor is refused. The real statements around it still are.</summary>
    [Theory]
    [InlineData("select 1;;", new[] { "SELECT" })]
    [InlineData(";", new string[0])]
    [InlineData("; select 1", new[] { "SELECT" })]
    [InlineData("select 1; ; source=x | head 1", new[] { "SELECT", "SOURCE" })]
    [InlineData("select 1; /* c */ ; -- d\n;", new[] { "SELECT" })]
    [InlineData("select 1; ; foo", new[] { "SELECT", "FOO" })]
    public void A_statement_of_only_semicolons_is_not_described(string sql, string[] verbs)
    {
        var described = WriteGuard.Describe(Os, sql);
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
    [InlineData("SHOW TABLES LIKE 'l%'")]
    [InlineData("DESCRIBE TABLES LIKE 'logs'")]
    [InlineData("describe logs")]
    [InlineData("source=logs | head 5")]
    [InlineData("source = logs")]
    [InlineData("search source=logs")]
    [InlineData("index=logs")]
    [InlineData("source=logs | fields ml, ad")]
    [InlineData("SELECT ml, kmeans FROM logs")]
    [InlineData("SELECT user.name FROM logs WHERE a = '.x'")]
    [InlineData("source=logs | where msg = '.x'")]
    [InlineData("search source=logs msg='.x'")]
    public void A_read_on_the_allow_list_runs(string sql) => Assert.Null(External(sql));

    [Theory]
    [InlineData("delete from logs", "'DELETE' writes")]
    [InlineData("explain source=logs", "not a read")]
    [InlineData("show datasources", null)]
    [InlineData("foo bar", "'FOO' is not a read")]
    [InlineData("| source=logs", "not a read")]
    public void Anything_else_is_refused_or_read(string sql, string? why)
    {
        var refusal = External(sql);
        if (why is null)
        {
            Assert.Null(refusal);
            return;
        }

        Assert.NotNull(refusal);
        Assert.Contains(why, refusal);
    }

    [Theory]
    [InlineData("source=logs | ml action='train' algorithm='rcf'", "'ml'", "machine-learning")]
    [InlineData("source=logs | kmeans centroids=3", "'kmeans'", "machine-learning")]
    [InlineData("source=logs | AD time_field='t'", "'ad'", "machine-learning")]
    [InlineData("source=logs | rest", "'rest'", "management endpoints")]
    [InlineData("SELECT * FROM .kibana", "'.kibana'", "hidden or system index")]
    [InlineData("SELECT * FROM `.kibana`", "'.kibana'", "hidden or system index")]
    [InlineData("SELECT * FROM `logs,.security`", "'.security'", "hidden or system index")]
    [InlineData("source=.kibana | head 1", "'.kibana'", "hidden or system index")]
    [InlineData("source='.kibana'", "'.kibana'", "hidden or system index")]
    [InlineData("source=logs,.security", "'.security'", "hidden or system index")]
    [InlineData("search source=\".opendistro\"", "'.opendistro'", "hidden or system index")]
    public void Ml_commands_and_dot_indices_are_refused_by_name(string sql, string named, string why)
    {
        var refusal = External(sql);
        Assert.NotNull(refusal);
        Assert.Contains(named, refusal);
        Assert.Contains(why, refusal);
        Assert.Contains("not a boundary", refusal);
        Assert.DoesNotContain("credentials", refusal);
    }

    [Fact]
    public void Rest_as_the_first_word_is_denied_by_name_for_a_direct_caller()
    {
        var denied = Os.ExternalDeniedStatement("rest /_cat/indices");
        Assert.NotNull(denied);
        Assert.Contains("'rest'", denied);
    }

    [Fact]
    public void The_name_scan_reads_this_engines_languages()
    {
        var (called, mentioned) = Os.ExternalNameScan("SELECT count(a) FROM `logs` WHERE b = \"pg_authid\"");
        Assert.Contains("count", called);
        Assert.Contains("logs", mentioned);
        Assert.DoesNotContain("pg_authid", mentioned);
    }

    // ---- Completion gate ----

    [Theory]
    [InlineData("SELECT * FROM ", true)]
    [InlineData("source=logs | where ", false)]
    [InlineData("search ", false)]
    public void Completion_answers_only_inside_a_sql_statement(string sql, bool expected)
        => Assert.Equal(expected, Os.CompletesAt(sql, sql.Length));
}
