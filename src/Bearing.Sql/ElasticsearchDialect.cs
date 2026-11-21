using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// The Elasticsearch dialect: <b>two languages in one buffer</b> — Elasticsearch SQL (sent to <c>/_sql</c>) and
/// ES|QL (sent to <c>/_query</c>), told apart per statement by its first word (<see cref="SearchScan"/>, spec
/// Behaviour 4). Every lexical question — where a statement ends, what is a string, what a comment, what a
/// literal — is answered by <see cref="SearchScan"/> in the statement's own language, never by the PostgreSQL
/// lexer: <c>"x"</c> is a name in Elasticsearch SQL and a string in ES|QL, and <c>//</c> ends a line only in
/// ES|QL.
/// <para>
/// <b>Most of the interface answers "not on this engine".</b> Results continue from the server's cursor, so
/// there is no offset paging or count; there is no grid editing, no transaction, no plan Bearing reads, and no
/// script variable. Each of those members answers null, empty or false — never an approximation in another
/// engine's syntax (§1.1).
/// </para>
/// </summary>
public sealed partial class ElasticsearchDialect : ISqlDialect
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static ElasticsearchDialect Instance { get; } = new();

    /// <summary>Matches the provider's id, which lives in <c>Bearing.Data</c> and so cannot be referenced from
    /// here (§2.2).</summary>
    public string Id => "elasticsearch";

    // ---- Identifiers ----

    /// <summary>A double-quoted name with <c>""</c> for a quote inside — Elasticsearch SQL's form.</summary>
    public string Quote(string identifier) => PgIdentifier.Quote(identifier);

    /// <inheritdoc cref="NeedsQuoting"/>
    public string QuoteIfNeeded(string identifier) => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>
    /// False for a plain word, and for a <b>dotted path of plain words</b> (<c>user.name</c>,
    /// <c>title.keyword</c>) — how a mapped field is written. A plain word is lower-case letters, digits and
    /// <c>_</c>, not starting with a digit (the form <see cref="PgIdentifier"/> leaves bare, upper case quoted
    /// because nothing here vouches for how the server treats it), and is not in <see cref="Keywords"/>.
    /// PostgreSQL's reserved list is deliberately not used: it reserves <c>user</c>, which would quote every
    /// <c>user.*</c> field for a keyword Elasticsearch SQL does not have. Anything else — a <c>-</c>, a
    /// <c>*</c>, a leading <c>.</c> or digit, a space, an upper-case letter, <c>@</c> — is quoted whole
    /// (<c>"logs-2024"</c>). Over-quoting costs a pair of quotes; under-quoting a parse error.
    /// <para>
    /// That the bare dotted form resolves on the server is a spec assumption, pinned by a live test in batch
    /// B9 — not known. (The vendor documentation says an <i>index</i> name holding a dot needs quotes; this
    /// cannot tell an index from a field, and a field path is what completion inserts most.)
    /// </para>
    /// </summary>
    public bool NeedsQuoting(string identifier)
        => identifier.Length == 0
           || !DottedPathRegex().IsMatch(identifier)
           || identifier.Split('.').Any(part => !PlainWordRegex().IsMatch(part) || Keywords.Contains(part));

    /// <inheritdoc/>
    public string Unquote(string identifier) => PgIdentifier.Unquote(identifier);

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

    /// <summary>Grid editing is not offered on Elasticsearch (the grid is locked first), and neither language
    /// writes a document — so there is no honest text to return.</summary>
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

    /// <summary><c>INTO</c>, the only command that names a write — an ES|QL pipe command (a dev-build gate in the
    /// grammar today). Every statement whose lead word is not a known read is risky too, through the conservative
    /// default in <see cref="DescribeStatements"/>.</summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INTO",
    };

    /// <summary>The reads an exposed connection runs: Elasticsearch SQL's <c>SELECT</c>, <c>SHOW</c>,
    /// <c>DESCRIBE</c> and <c>DESC</c>, and ES|QL's <c>FROM</c>, <c>ROW</c> and <c>TS</c>.</summary>
    public IReadOnlySet<string> ExternalReadVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "DESC", "FROM", "ROW", "TS",
    };

    /// <inheritdoc/>
    /// <remarks>Elasticsearch has no plan Bearing can read.</remarks>
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
        => SearchDialects.NameScan(SearchEngine.Elasticsearch, statement);

    /// <summary>
    /// A refusal sentence for an exposed connection, or null: an ES|QL statement whose pipe command calls an
    /// inference endpoint (<c>COMPLETION</c>; <c>RERANK</c> may) or writes (<c>INTO</c>), or any statement
    /// naming an index whose name begins with <c>.</c> (<see cref="SearchScan.DotIndexName"/>). Accident
    /// prevention, not a boundary: a role or API key without the privilege is.
    /// </summary>
    public string? ExternalDeniedStatement(string statement)
    {
        foreach (var s in SearchScan.Statements(SearchEngine.Elasticsearch, statement))
        {
            if (s.Language == SearchLanguage.Piped)
            {
                foreach (var command in SearchScan.CommandWords(s))
                {
                    var why = command switch
                    {
                        "COMPLETION" => "it sends rows to an inference endpoint, outside the data",
                        "RERANK" => "it may call an inference endpoint, outside the data",
                        "INTO" => "it writes to an index, and an exposed connection runs reads only",
                        _ => null,
                    };
                    if (why is not null)
                    {
                        return SearchDialects.CommandRefusal(command, why);
                    }
                }
            }

            if (SearchScan.DotIndexName(SearchEngine.Elasticsearch, s) is { } index)
            {
                return SearchDialects.DotIndexRefusal(index);
            }
        }

        return null;
    }

    /// <summary>
    /// Per statement, in its own language: a read when its lead word(s) are a known read of that language
    /// (<see cref="SearchScan"/>'s table); an ES|QL <c>INTO</c> command names a write; any other statement —
    /// an unknown first word — is risky by the conservative default (<see cref="StatementRisk.FromConservativeDefault"/>).
    /// </summary>
    public IReadOnlyList<StatementRisk> DescribeStatements(string sql) => SearchScan.Describe(SearchEngine.Elasticsearch, sql);

    /// <summary>Always null: Bearing holds no transaction here, and neither language has one.</summary>
    public string? TransactionControl(IReadOnlyList<string> words) => null;

    /// <summary>False: nothing commits implicitly where no transaction is held.</summary>
    public bool CommitsImplicitly(IReadOnlyList<string> words) => false;

    /// <summary>False: decline the row-impact count — the PostgreSQL lexer does not read these languages, and
    /// nothing here writes rows to count.</summary>
    public bool ReadsLikePostgresForCounting(string statement, IReadOnlyList<Antlr4.Runtime.IToken> postgresTokens)
        => false;

    // ---- Statement splitting ----

    /// <summary><see cref="SearchScan"/>'s split: <c>;</c> outside each statement's own strings, names and
    /// comments, and the editor's blank-line convention.</summary>
    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => SearchScan.Split(SearchEngine.Elasticsearch, sql);

    /// <summary>True when the fragment ends in a line comment of the language it is read in — <c>--</c> in
    /// Elasticsearch SQL, <c>//</c> in ES|QL — so a run-all puts its <c>;</c> on a new line.</summary>
    public bool EndsWithLineComment(string fragment) => SearchScan.EndsWithLineComment(SearchEngine.Elasticsearch, fragment);

    /// <inheritdoc/>
    public int? CodeEnd(string sql) => SearchScan.CodeEnd(SearchEngine.Elasticsearch, sql);

    /// <inheritdoc/>
    public string? FormatRefusal => "the formatter reads PostgreSQL, not Elasticsearch SQL or ES|QL";

    /// <summary>Inside <c>'…'</c> in Elasticsearch SQL (a <c>"…"</c> name still completes); inside <c>"…"</c>
    /// or <c>"""…"""</c> in ES|QL.</summary>
    public bool InStringLiteral(string sql, int offset) => SearchScan.InString(SearchEngine.Elasticsearch, sql, offset);

    /// <summary>
    /// False when the caret is inside an ES|QL statement: completion reads with a SQL grammar, and offering SQL
    /// keywords inside a piped query is noise. True everywhere else.
    /// </summary>
    public bool CompletesAt(string sql, int caret)
        => SearchScan.StatementAt(SearchEngine.Elasticsearch, sql, caret)?.Language != SearchLanguage.Piped;

    /// <summary>Strings and numbers of each statement by its own language: an ES|QL <c>"x"</c> is redacted, an
    /// Elasticsearch SQL <c>"x"</c> is a name and is kept.</summary>
    public string RedactLiterals(string? sql) => SearchScan.RedactLiterals(SearchEngine.Elasticsearch, sql);

    /// <summary>Empty: script variables are not offered on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<SqlVariableRef> FindVariables(string sql) => [];

    /// <summary>Empty: <c>@set</c> lines are not recognised on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<ScriptDirective> FindDirectives(string sql) => [];

    // ---- Completion ----

    /// <summary>The PostgreSQL grammar, asked only inside an Elasticsearch SQL statement
    /// (<see cref="CompletesAt"/>): index and field names complete; keyword completion is Postgres' (a known
    /// limitation — there is no Elasticsearch SQL grammar here).</summary>
    public ISqlParseRules ParseRules => PgParseRules.Instance;

    /// <summary>
    /// Words Elasticsearch SQL's grammar reads as keywords, reserved or not, plus the standard SQL reserved words
    /// around them — written from the grammar as best known, not measured, so the list errs long: a word here
    /// that the server would have read as a name costs a pair of quotes, a missing one a parse error.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyze", "analyzed", "and", "any", "as", "asc", "between", "by", "case", "cast", "catalog",
        "catalogs", "columns", "convert", "current_date", "current_time", "current_timestamp", "day", "days",
        "debug", "desc", "describe", "distinct", "else", "end", "escape", "except", "executable", "exists",
        "explain", "extract", "false", "fetch", "first", "for", "format", "from", "frozen", "full", "functions",
        "graphviz", "group", "having", "hour", "hours", "in", "include", "inner", "intersect", "interval", "is",
        "join", "last", "left", "like", "limit", "mapped", "match", "minus", "minute", "minutes", "month", "months",
        "natural", "not", "null", "nulls", "offset", "on", "optimized", "or", "order", "outer", "over", "parsed",
        "physical", "pivot", "plan", "query", "right", "rlike", "schemas", "second", "seconds", "select", "show",
        "sys", "tables", "then", "to", "top", "true", "type", "types", "union", "using", "verify", "when", "where",
        "with", "year", "years",
    };

    private const string GridEditingWithheld = "Grid editing isn't available on Elasticsearch connections.";

    /// <summary>Plain words joined by single dots: no leading, trailing or doubled dot.</summary>
    [GeneratedRegex(@"^[^.]+(\.[^.]+)*$")]
    private static partial Regex DottedPathRegex();

    /// <summary>One plain word: lower-case letters, digits and <c>_</c>, no leading digit.</summary>
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$")]
    private static partial Regex PlainWordRegex();
}
