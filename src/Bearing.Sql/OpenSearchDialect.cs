using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// The OpenSearch dialect: <b>two languages in one buffer</b> — OpenSearch SQL (sent to <c>/_plugins/_sql</c>)
/// and PPL (sent to <c>/_plugins/_ppl</c>), told apart per statement by its first word (<see cref="SearchScan"/>,
/// spec Behaviour 4). Every lexical question is answered by <see cref="SearchScan"/> in the statement's own
/// language, never by the PostgreSQL or MySQL lexer: <c>#</c> ends a line only in OpenSearch SQL, <c>//</c>
/// only in PPL.
/// <para>
/// <b>Most of the interface answers "not on this engine".</b> Results continue from the server's cursor, so
/// there is no offset paging or count; there is no grid editing, no transaction, no plan Bearing reads, and no
/// script variable. Each of those members answers null, empty or false — never an approximation in another
/// engine's syntax (§1.1).
/// </para>
/// </summary>
public sealed partial class OpenSearchDialect : ISqlDialect
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static OpenSearchDialect Instance { get; } = new();

    /// <summary>Matches the provider's id, which lives in <c>Bearing.Data</c> and so cannot be referenced from
    /// here (§2.2).</summary>
    public string Id => "opensearch";

    // ---- Identifiers ----

    /// <summary>A backticked name with <c>``</c> for a backtick inside. Never <c>"…"</c>: OpenSearch SQL also
    /// reads that as a string.</summary>
    public string Quote(string identifier) => MySqlIdentifier.Quote(identifier);

    /// <inheritdoc cref="NeedsQuoting"/>
    public string QuoteIfNeeded(string identifier) => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>
    /// False for a plain word, and for a <b>dotted path of plain words</b> (<c>user.name</c>,
    /// <c>title.keyword</c>) — how a mapped field is written. A plain word is what <see cref="MySqlIdentifier"/>
    /// leaves bare (letters, digits, <c>_</c>, no leading digit, not reserved; case is not a reason). Anything
    /// else — a <c>-</c>, a <c>*</c>, a leading <c>.</c> or digit, a space, an <c>@</c> — is backticked whole.
    /// <para>
    /// Measured against OpenSearch 3.10.0 (<c>SearchLiveTests</c>): a bare dotted name runs both as a field path
    /// and as an index name, and a backticked whole name runs as either too. (The one shape that refused a
    /// backticked dotted index name, a <c>LIMIT</c> sent with <c>fetch_size</c>, went to the legacy engine; Bearing
    /// no longer sends <c>fetch_size</c> with a <c>LIMIT</c>.) Unlike Elasticsearch, a dotted path therefore stays
    /// bare here.
    /// </para>
    /// </summary>
    public bool NeedsQuoting(string identifier)
        => identifier.Length == 0
           || !DottedPathRegex().IsMatch(identifier)
           || identifier.Split('.').Any(MySqlIdentifier.NeedsQuoting);

    /// <inheritdoc/>
    public string Unquote(string identifier) => MySqlIdentifier.Unquote(identifier);

    // ---- Paging and counting ----

    /// <summary>Null: a result continues from the server's cursor, so a null here retires offset paging.</summary>
    public string? TryAppendPage(string sql, int offset, int limit) => null;

    /// <summary>Null: nothing is paged from the outside; see <see cref="TryAppendPage"/>.</summary>
    public string? Wrap(string sql, int offset, int limit) => null;

    /// <summary>Null: the total is reported unavailable.</summary>
    public string? CountWrap(string sql) => null;

    /// <inheritdoc/>
    public string SelectTopRows(string qualifiedTable, int rows) => $"select * from {qualifiedTable} limit {rows}";

    // ---- DML ----

    /// <summary>Grid editing is not offered on OpenSearch (the grid is locked first), and neither language
    /// inserts or updates a document — so there is no honest text to return.</summary>
    public string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning)
        => throw new NotSupportedException(GridEditingWithheld);

    /// <inheritdoc cref="InsertStatement"/>
    public string UpdateStatement(
        string qualifiedTable, string setList, string whereClause, IReadOnlyList<string>? returningColumns)
        => throw new NotSupportedException(GridEditingWithheld);

    /// <summary>Null: with no grid editing there is no cell to put an expression in.</summary>
    public string? TryEditExpression(string? text) => null;

    /// <inheritdoc cref="ISqlDialect.EditExpressions"/>
    public IReadOnlyCollection<string> EditExpressions => [];

    /// <inheritdoc cref="ISqlDialect.OfferedEditExpressions"/>
    public IReadOnlyList<string> OfferedEditExpressions => [];

    // ---- Write guard ----

    /// <summary>True: <see cref="SearchScan"/> reads both languages, and anything it does not recognise as a
    /// read is risky by construction (<see cref="DescribeStatements"/>).</summary>
    public bool HasDialectAwareGuard => true;

    /// <summary><c>DELETE</c>: OpenSearch SQL's opt-in delete on 2.x. Every statement whose lead word is not a
    /// known read is risky too, through the conservative default in <see cref="DescribeStatements"/>.</summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DELETE",
    };

    /// <summary>The reads an exposed connection runs: OpenSearch SQL's <c>SELECT</c>, <c>SHOW</c> and
    /// <c>DESCRIBE</c>, and PPL's <c>SEARCH</c>, <c>SOURCE</c> and <c>INDEX</c> (<c>source=logs</c> leads with
    /// <c>SOURCE</c>).</summary>
    public IReadOnlySet<string> ExternalReadVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "SEARCH", "SOURCE", "INDEX",
    };

    /// <inheritdoc/>
    /// <remarks>OpenSearch has no plan Bearing can read.</remarks>
    public bool SupportsExplainPlan => false;

    /// <summary>Empty: the shapes this engine refuses are commands and index names, not functions — see
    /// <see cref="ExternalDeniedStatement"/>.</summary>
    public IReadOnlySet<string> ExternalDeniedFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty: index names beginning with <c>.</c> are refused by <see cref="ExternalDeniedStatement"/>,
    /// which reads them as the statement's language does.</summary>
    public IReadOnlySet<string> ExternalDeniedRelations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names each statement calls (a word or name followed by <c>(</c>) and mentions, read by
    /// <see cref="SearchScan"/> in the statement's own language — never by the PostgreSQL lexer, which does not
    /// read these languages. Both sets are real answers ("nothing denied here"); both deny lists are empty.
    /// </summary>
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
        => SearchDialects.NameScan(SearchEngine.OpenSearch, statement);

    /// <summary>
    /// A refusal sentence for an exposed connection, or null: a PPL statement using <c>ml</c>, <c>kmeans</c> or
    /// <c>ad</c> (they run machine-learning jobs) or <c>rest</c> (it reads the cluster's management endpoints)
    /// as a pipe command — or <c>rest</c> as its first word — or any statement naming an index whose name begins
    /// with <c>.</c> (<see cref="SearchScan.DotIndexName"/>). Accident prevention, not a boundary: a role
    /// without the privilege is.
    /// </summary>
    public string? ExternalDeniedStatement(string statement)
    {
        foreach (var s in SearchScan.Statements(SearchEngine.OpenSearch, statement))
        {
            // `rest` leads in either reading: an unknown first word with no top-level pipe reads as SQL.
            List<string> commands = s.Language == SearchLanguage.Piped ? [.. SearchScan.CommandWords(s)] : [];
            if (s.LeadWords.Count > 0 && s.LeadWords[0] == "REST")
            {
                commands.Insert(0, "REST");
            }

            foreach (var command in commands)
            {
                var why = command switch
                {
                    "ML" or "KMEANS" or "AD" => "it runs a machine-learning job on the cluster",
                    "REST" => "it reads the cluster's management endpoints rather than an index",
                    _ => null,
                };
                if (why is not null)
                {
                    return SearchDialects.CommandRefusal(command.ToLowerInvariant(), why);
                }
            }

            if (SearchScan.DotIndexName(SearchEngine.OpenSearch, s) is { } index)
            {
                return SearchDialects.DotIndexRefusal(index);
            }
        }

        return null;
    }

    /// <summary>
    /// Per statement, in its own language: a read when its lead word(s) are a known read of that language
    /// (<see cref="SearchScan"/>'s table, <c>DELETE</c> excepted); <c>DELETE</c> names a write; any other
    /// statement — an unknown first word — is risky by the conservative default
    /// (<see cref="StatementRisk.FromConservativeDefault"/>).
    /// </summary>
    public IReadOnlyList<StatementRisk> DescribeStatements(string sql) => SearchScan.Describe(SearchEngine.OpenSearch, sql);

    /// <summary>Always null: Bearing holds no transaction here, and neither language has one.</summary>
    public string? TransactionControl(IReadOnlyList<string> words) => null;

    /// <summary>False: nothing commits implicitly where no transaction is held.</summary>
    public bool CommitsImplicitly(IReadOnlyList<string> words) => false;

    /// <summary>False: decline the row-impact count — the PostgreSQL lexer does not read these languages
    /// (<c>#</c> comments, backslash escapes), and a declined count is always safe (#112).</summary>
    public bool ReadsLikePostgresForCounting(string statement, IReadOnlyList<Antlr4.Runtime.IToken> postgresTokens)
        => false;

    // ---- Statement splitting ----

    /// <summary><see cref="SearchScan"/>'s split: <c>;</c> outside each statement's own strings, names and
    /// comments, and the editor's blank-line convention.</summary>
    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => SearchScan.Split(SearchEngine.OpenSearch, sql);

    /// <summary>True when the fragment ends in a line comment of the language it is read in — <c>#</c> or
    /// <c>-- </c> in OpenSearch SQL, <c>//</c> in PPL — so a run-all puts its <c>;</c> on a new line.</summary>
    public bool EndsWithLineComment(string fragment) => SearchScan.EndsWithLineComment(SearchEngine.OpenSearch, fragment);

    /// <inheritdoc/>
    public int? CodeEnd(string sql) => SearchScan.CodeEnd(SearchEngine.OpenSearch, sql);

    /// <inheritdoc/>
    public string? FormatRefusal => "the formatter reads PostgreSQL, not OpenSearch SQL or PPL";

    /// <summary>Inside <c>'…'</c> or <c>"…"</c>, in both languages.</summary>
    public bool InStringLiteral(string sql, int offset) => SearchScan.InString(SearchEngine.OpenSearch, sql, offset);

    /// <summary>
    /// False when the caret is inside a PPL statement: completion reads with a SQL grammar, and offering SQL
    /// keywords inside a piped query is noise. True everywhere else.
    /// </summary>
    public bool CompletesAt(string sql, int caret)
        => SearchScan.StatementAt(SearchEngine.OpenSearch, sql, caret)?.Language != SearchLanguage.Piped;

    /// <summary>Strings (<c>'…'</c> and <c>"…"</c>) and numbers of each statement by its own language.</summary>
    public string RedactLiterals(string? sql) => SearchScan.RedactLiterals(SearchEngine.OpenSearch, sql);

    /// <summary>Empty: script variables are not offered on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<SqlVariableRef> FindVariables(string sql) => [];

    /// <summary>Empty: <c>@set</c> lines are not recognised on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<ScriptDirective> FindDirectives(string sql) => [];

    // ---- Completion ----

    /// <summary>The MySQL grammar, asked only inside an OpenSearch SQL statement (<see cref="CompletesAt"/>):
    /// index and field names complete, backticked where needed; keyword completion is MySQL's (a known
    /// limitation — there is no OpenSearch SQL grammar here).</summary>
    public ISqlParseRules ParseRules => MySqlParseRules.Instance;

    private const string GridEditingWithheld = "Grid editing isn't available on OpenSearch connections.";

    /// <summary>Plain words joined by single dots: no leading, trailing or doubled dot.</summary>
    [GeneratedRegex(@"^[^.]+(\.[^.]+)*$")]
    private static partial Regex DottedPathRegex();
}

/// <summary>What <see cref="ElasticsearchDialect"/> and <see cref="OpenSearchDialect"/> share beyond
/// <see cref="SearchScan"/>: the name scan and the refusal sentences.</summary>
internal static class SearchDialects
{
    /// <summary>
    /// Called and mentioned names of every statement, read by <see cref="SearchScan"/>: words and delimited
    /// names (delimiters removed), lower-cased comparisons; a name immediately followed by <c>(</c> is called;
    /// a dotted pair also yields <c>a.b</c>. Strings are not names.
    /// </summary>
    public static (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) NameScan(SearchEngine engine, string statement)
    {
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in SearchScan.Statements(engine, statement))
        {
            string? previous = null;
            var afterDot = false;
            foreach (var token in s.Tokens.Where(t => t.IsCode))
            {
                if (token.Text == "(" && previous is not null)
                {
                    called.Add(previous);
                }
                if (token.Text == ".")
                {
                    afterDot = previous is not null;
                    continue;
                }

                var name = token.Kind switch
                {
                    SearchTokenKind.Word => token.Text,
                    SearchTokenKind.QuotedName when token.Terminated && token.Text.Length >= 2
                        => token.Text[1..^1].Replace($"{token.Text[0]}{token.Text[0]}", token.Text[..1]),
                    _ => null,
                };
                if (name is { Length: > 0 })
                {
                    mentioned.Add(name);
                    if (afterDot && previous is not null)
                    {
                        mentioned.Add($"{previous}.{name}");
                    }
                }

                afterDot = false;
                previous = name is { Length: > 0 } ? name : null;
            }
        }

        return (called, mentioned);
    }

    /// <summary>The refusal for a pipe command an exposed connection does not run.</summary>
    public static string CommandRefusal(string command, string why)
        => $"'{command}' is not available through an exposed connection: {why}. "
           + "This stops accidents and is not a boundary — a role without the privilege is.";

    /// <summary>The refusal for an index name beginning with a dot.</summary>
    public static string DotIndexRefusal(string index)
        => $"'{index}' is not readable through an exposed connection: a name beginning with '.' is, by convention, "
           + "a hidden or system index. This stops accidents and is not a boundary — a role without read access "
           + "to it is.";
}
