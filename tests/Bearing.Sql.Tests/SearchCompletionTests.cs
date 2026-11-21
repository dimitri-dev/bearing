using Bearing.Core.Completion;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion on the two search engines (SC5): index and field names complete inside a SQL statement, quoted the
/// engine's way where needed, through the borrowed grammar (<see cref="ElasticsearchDialect.ParseRules"/> /
/// <see cref="OpenSearchDialect.ParseRules"/>); inside an ES|QL or PPL statement nothing is offered at all —
/// <see cref="ISqlDialect.CompletesAt"/> answers before the grammar is asked. Probes at real carets (§9.5).
/// </summary>
public class SearchCompletionTests
{
    private const long LogsId = 31;
    private const long DatedId = 32;

    private static readonly SchemaSnapshot Schema = Build();

    private static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new TableInfo(LogsId, "default", "logs", RelationKind.Table),
            new TableInfo(DatedId, "default", "logs-2024", RelationKind.Table),
        };
        var columns = new[]
        {
            new ColumnInfo(LogsId, 1, "message", "text", true, false),
            new ColumnInfo(LogsId, 2, "user.name", "keyword", true, false),
            new ColumnInfo(LogsId, 3, "@timestamp", "date", true, false),
            new ColumnInfo(DatedId, 1, "level", "keyword", true, false),
        };

        return new SchemaSnapshot("localhost", new[] { "default" }, tables, columns, [], searchPath: new[] { "default" });
    }

    private static CompletionResult At(ISqlDialect dialect, string sql, int? caret = null)
        => new CompletionEngine(() => dialect).Complete(sql, caret ?? sql.Length, Schema);

    private static IReadOnlyList<string> Displays(CompletionResult r) => r.Suggestions.Select(s => s.DisplayText).ToList();

    private static string? Insertion(CompletionResult r, string display)
        => r.Suggestions.FirstOrDefault(s => s.DisplayText == display)?.ReplacementText;

    public static TheoryData<string> Dialects => new() { "elasticsearch", "opensearch" };

    private static ISqlDialect Dialect(string id)
        => id == "elasticsearch" ? ElasticsearchDialect.Instance : OpenSearchDialect.Instance;

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Indices_complete_after_from_in_a_sql_statement(string id)
    {
        var r = At(Dialect(id), "select * from ");
        Assert.Contains("logs", Displays(r));
        Assert.Contains("logs-2024", Displays(r));
    }

    [Fact]
    public void Elasticsearch_double_quotes_an_index_that_needs_it()
    {
        var r = At(ElasticsearchDialect.Instance, "select * from ");
        Assert.StartsWith("\"logs-2024\"", Insertion(r, "logs-2024"));
        Assert.StartsWith("logs", Insertion(r, "logs"));
    }

    [Fact]
    public void Opensearch_backticks_an_index_that_needs_it()
    {
        var r = At(OpenSearchDialect.Instance, "select * from ");
        Assert.StartsWith("`logs-2024`", Insertion(r, "logs-2024"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Fields_complete_in_a_predicate_with_dotted_paths_bare(string id)
    {
        var r = At(Dialect(id), "select * from logs where ");
        Assert.Contains("message", Displays(r));
        Assert.Contains("user.name", Displays(r));
        Assert.Equal("user.name", Insertion(r, "user.name"));
    }

    [Fact]
    public void A_field_that_is_not_a_plain_word_is_quoted_the_engines_way()
    {
        Assert.Equal("\"@timestamp\"", Insertion(At(ElasticsearchDialect.Instance, "select * from logs where "), "@timestamp"));
        Assert.Equal("`@timestamp`", Insertion(At(OpenSearchDialect.Instance, "select * from logs where "), "@timestamp"));
    }

    [Theory]
    [InlineData("FROM logs | WHERE ")]
    [InlineData("FROM ")]
    [InlineData("from logs | keep ")]
    [InlineData("SHOW INFO ")]
    [InlineData("foo | ")]
    public void Nothing_completes_inside_an_esql_statement(string sql)
        => Assert.Empty(At(ElasticsearchDialect.Instance, sql).Suggestions);

    [Theory]
    [InlineData("source=logs | where ")]
    [InlineData("search source=")]
    [InlineData("describe ")]
    [InlineData("| where ")]
    public void Nothing_completes_inside_a_ppl_statement(string sql)
        => Assert.Empty(At(OpenSearchDialect.Instance, sql).Suggestions);

    [Theory]
    [InlineData("elasticsearch", "select * from logs;\nFROM logs | WHERE \n;\nselect * from logs")]
    [InlineData("opensearch", "select * from logs;\nsource=logs | where \n;\nselect * from logs")]
    public void A_piped_statement_between_two_sql_statements_completes_nothing(string id, string script)
    {
        var caret = script.IndexOf("WHERE ", StringComparison.OrdinalIgnoreCase) + "WHERE ".Length;

        Assert.False(Dialect(id).CompletesAt(script, caret));
        Assert.Empty(At(Dialect(id), script, caret).Suggestions);
        Assert.True(Dialect(id).CompletesAt(script, script.Length));
        Assert.True(Dialect(id).CompletesAt(script, "select * from".Length));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void A_caret_inside_a_string_completes_nothing(string id)
        => Assert.Empty(At(Dialect(id), "select * from logs where message = 'abc").Suggestions);
}
