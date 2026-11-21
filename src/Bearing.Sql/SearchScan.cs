using System.Text;

namespace Bearing.Sql;

/// <summary>Which search engine a <see cref="SearchScan"/> reads for. The two speak different languages.</summary>
public enum SearchEngine
{
    /// <summary>Elasticsearch: Elasticsearch SQL and ES|QL.</summary>
    Elasticsearch,

    /// <summary>OpenSearch: OpenSearch SQL and PPL.</summary>
    OpenSearch,
}

/// <summary>Which of its engine's two languages a statement is written in.</summary>
public enum SearchLanguage
{
    /// <summary>Elasticsearch SQL or OpenSearch SQL.</summary>
    Sql,

    /// <summary>The engine's piped language: ES|QL on Elasticsearch, PPL on OpenSearch.</summary>
    Piped,
}

/// <summary>What a <see cref="SearchToken"/> is.</summary>
public enum SearchTokenKind
{
    /// <summary>A bare word: a keyword, a name, a command.</summary>
    Word,

    /// <summary>A number literal.</summary>
    Number,

    /// <summary>A delimited name: <c>"…"</c> in Elasticsearch SQL, <c>`…`</c> everywhere.</summary>
    QuotedName,

    /// <summary>A string literal, in whichever quotes the language reads as one.</summary>
    String,

    /// <summary>A comment running to the end of its line (<c>--</c>, <c>//</c> or <c>#</c>, per language).</summary>
    LineComment,

    /// <summary>A <c>/* … */</c> comment.</summary>
    BlockComment,

    /// <summary>Any other character; <c>||</c> is one token, so it is never taken for a pipe.</summary>
    Punctuation,
}

/// <summary>
/// One token as <see cref="SearchScan"/> read it.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Start">Offset of its first character in the scanned text.</param>
/// <param name="End">Offset just past its last character.</param>
/// <param name="Text">The text it covers, delimiters included.</param>
/// <param name="Terminated">False for a string, name or block comment that ran to the end of the text without
/// closing.</param>
public sealed record SearchToken(SearchTokenKind Kind, int Start, int End, string Text, bool Terminated = true)
{
    /// <summary>Whether the token is code — anything but a comment.</summary>
    public bool IsCode => Kind is not (SearchTokenKind.LineComment or SearchTokenKind.BlockComment);
}

/// <summary>
/// One statement of a script, as <see cref="SearchScan.Statements"/> read it.
/// </summary>
/// <param name="Span">Where it sits in the script — the same span <see cref="ISqlDialect.SplitStatements"/>
/// hands out.</param>
/// <param name="Language">The language it is read and sent in.</param>
/// <param name="LanguageFromLeadWord">True when its lead word(s) decided the language (a row of the table in
/// <see cref="SearchScan"/>); false when the "otherwise" rule did — an unknown first word.</param>
/// <param name="LeadWords">Its first word and, when the next code token is a word, its second — upper-cased.
/// Empty when it does not begin with a word (a leading <c>|</c> or <c>(</c>).</param>
/// <param name="Verb">Its first code token, upper-cased: the lead word, or the punctuation it begins with.</param>
/// <param name="PipeCommands">The first word, upper-cased, of each segment after a top-level <c>|</c> (outside
/// strings, names, comments and parentheses), in order; an empty string for a segment that does not begin with
/// a word. Empty when the statement has no top-level <c>|</c>.</param>
/// <param name="Tokens">Its tokens from its first code token to its end, comments inside it included, read by
/// its own language's lexical rules.</param>
public sealed record SearchStatement(
    StatementSpan Span,
    SearchLanguage Language,
    bool LanguageFromLeadWord,
    IReadOnlyList<string> LeadWords,
    string Verb,
    IReadOnlyList<string> PipeCommands,
    IReadOnlyList<SearchToken> Tokens);

/// <summary>
/// The one hand-written scanner for the four languages Elasticsearch and OpenSearch read — Elasticsearch SQL,
/// ES|QL, OpenSearch SQL and PPL. Pure, no ANTLR: none of the vendored grammars reads these languages, and the
/// PartiQL work showed what borrowing another engine's lexer costs (lesson 2026-11-19).
/// <para>
/// <b>The language of a statement</b> is read from its first word(s), case-insensitively, after leading
/// whitespace and comments (spec, Behaviour 4):
/// </para>
/// <list type="table">
///   <listheader><term>Engine</term><description>SQL / piped / otherwise</description></listheader>
///   <item><term>Elasticsearch</term><description>SQL: <c>SELECT</c>, <c>DESCRIBE</c>, <c>DESC</c>,
///     <c>EXPLAIN</c>, <c>SYS</c>, <c>DEBUG</c>, <c>SHOW</c> (but not <c>SHOW INFO</c>). ES|QL: <c>FROM</c>,
///     <c>ROW</c>, <c>TS</c>, <c>PROMQL</c>, <c>SHOW INFO</c>.</description></item>
///   <item><term>OpenSearch</term><description>SQL: <c>SELECT</c>, <c>DELETE</c>, <c>SHOW TABLES</c>,
///     <c>DESCRIBE TABLES</c>. PPL: <c>SEARCH</c>, <c>SOURCE</c>, <c>INDEX</c>, <c>DESCRIBE</c> (not
///     <c>DESCRIBE TABLES</c>), <c>SHOW</c> (not <c>SHOW TABLES</c>), <c>EXPLAIN</c>, <c>MULTISEARCH</c>,
///     <c>UNION</c>, <c>MAKERESULTS</c>, <c>GRAPHLOOKUP</c>, or a leading <c>|</c>.</description></item>
///   <item><term>Otherwise</term><description>The statement is read by the engine's <b>SQL</b> rules up to its
///     end; if that reading holds a top-level <c>|</c> it is piped (and is then read again, by the piped
///     rules), else it is SQL.</description></item>
/// </list>
/// <para>
/// <b>The language decides the lexical rules, and the rules decide where the statement ends</b>, so the scan
/// works one statement at a time: skip whitespace and comments by the engine's <i>prelude</i> reading (every
/// comment form either of its languages has: <c>--</c>, <c>//</c> and nested <c>/* */</c> on Elasticsearch;
/// <c>#</c>, <c>--</c> followed by whitespace, <c>//</c> and <c>/* */</c> on OpenSearch; strings and names by
/// its SQL rules), read the lead words the same way, decide the language, then read from the first code token
/// to the end of the statement by that language's rules. The space between statements is read by the prelude
/// rules again.
/// </para>
/// <para>
/// <b>Lexical rules per language</b> (research, gathered from the vendors' grammars and docs):
/// </para>
/// <list type="bullet">
///   <item>Elasticsearch SQL: <c>'…'</c> strings with <c>''</c>, no backslash escape; <c>"…"</c> <b>names</b>
///     with <c>""</c>; <c>`…`</c> names (the parser refuses them, the lexer still reads them as one token);
///     <c>--</c> and <c>/* */</c> comments, block comments nest.</item>
///   <item>ES|QL: <c>"…"</c> <b>strings</b> with backslash escapes and <c>"""…"""</c> strings with none;
///     <c>`…`</c> names with <c>``</c>; <c>//</c> and <c>/* */</c> comments, block comments nest (measured
///     live, not in the vendor docs); <c>--</c> is not a
///     comment and <c>'</c> does not open a string.</item>
///   <item>OpenSearch SQL: <c>`…`</c> names; <c>'…'</c> and <c>"…"</c> both an opaque quoted span, read as a
///     string, with doubled-quote and backslash escapes; <c>#</c>, <c>--</c> followed by whitespace (or the
///     end of the text) and <c>/* */</c> (not nested) comments.</item>
///   <item>PPL: <c>'…'</c> and <c>"…"</c> strings with backslash and doubled-quote escapes; <c>`…`</c> names;
///     <c>//</c> and <c>/* */</c> comments (not nested).</item>
///   <item>Everywhere: an unterminated string, name or block comment runs to the end of the text (a string's
///     newline does not end it). Words are letters, digits, <c>_</c> and <c>@</c>, not starting with a digit;
///     a number starts with a digit (or a <c>.</c> before a digit where no name precedes it).</item>
/// </list>
/// <para>
/// <b>Where a statement ends.</b> At a <c>;</c> outside strings, names and comments (it belongs to the
/// statement it ends), or — the editor's convention, the one <see cref="StatementSplitter"/> honours — at a
/// blank line between two code tokens at parenthesis depth 0 when the token after it opens a statement and the
/// one before it does not continue one. Openers: <c>SELECT</c>, <c>SHOW</c>, <c>DESCRIBE</c>, <c>EXPLAIN</c>,
/// <c>SYS</c>, <c>DEBUG</c>, <c>PROMQL</c> on Elasticsearch; <c>SELECT</c>, <c>SHOW</c>, <c>DESCRIBE</c>,
/// <c>EXPLAIN</c>, <c>DELETE</c>, <c>SEARCH</c>, <c>SOURCE</c>, <c>MULTISEARCH</c>, <c>MAKERESULTS</c>,
/// <c>GRAPHLOOKUP</c> on OpenSearch. Deliberately left out: words that also continue a statement (<c>FROM</c>,
/// <c>DESC</c>, <c>UNION</c>, <c>ROW</c>, <c>TS</c>, <c>INDEX</c>) — an unknown leader keeps the lines together,
/// recoverable with an explicit <c>;</c>, rather than risking a false split. Continuers: <c>,</c>, <c>(</c>,
/// <c>|</c>, <c>UNION</c>, <c>INTERSECT</c>, <c>EXCEPT</c>, <c>MINUS</c>.
/// </para>
/// <para>
/// <b>Spans</b> follow <see cref="StatementSplitter"/>'s PostgreSQL contract: the first statement owns the
/// text from offset 0; an own-line comment block directly above a statement (no blank line between) starts
/// it; a statement stops owning text in front of the first own-line comment after its last token, keeps a
/// comment that shares that token's line, and otherwise runs to the next statement's start (the last one, to
/// the end of the text). A buffer of only whitespace and comments has no statement.
/// </para>
/// <para>
/// <c>Bearing.Data</c> re-implements this split and language decision (<c>SearchBatch</c>), because it may
/// not reference this project; a parity test pins the two to one corpus. Keep the rules above crisp.
/// </para>
/// </summary>
public static class SearchScan
{
    /// <summary>The statements of <paramref name="sql"/>, each with its language, in order.</summary>
    public static IReadOnlyList<SearchStatement> Statements(SearchEngine engine, string sql)
        => Scan(engine, sql).Statements;

    /// <summary>Every token of <paramref name="sql"/>, in order: each statement's by its own language, and the
    /// comments between statements by the prelude reading.</summary>
    public static IReadOnlyList<SearchToken> Tokens(SearchEngine engine, string sql)
        => Scan(engine, sql).Tokens;

    /// <summary>The statements' spans — what <see cref="ISqlDialect.SplitStatements"/> returns.</summary>
    public static IReadOnlyList<StatementSpan> Split(SearchEngine engine, string sql)
        => Statements(engine, sql).Select(s => s.Span).ToList();

    /// <summary>
    /// The statement the caret belongs to, attributed as <see cref="StatementSplitter.StatementAt(string,int)"/>
    /// attributes one: through its end and the gap after it, switching at the next statement's first
    /// character. Null when the text has no statement.
    /// </summary>
    public static SearchStatement? StatementAt(SearchEngine engine, string sql, int caret)
    {
        var statements = Statements(engine, sql);
        if (statements.Count == 0)
        {
            return null;
        }

        caret = Math.Clamp(caret, 0, sql.Length);
        for (var i = 0; i < statements.Count; i++)
        {
            if (caret <= statements[i].Span.TrimmedEnd || i == statements.Count - 1 || caret < statements[i + 1].Span.Start)
            {
                return statements[i];
            }
        }

        return statements[^1];
    }

    /// <summary>
    /// Whether <paramref name="offset"/> sits inside a string literal of the statement's own language — past
    /// the opening quote and up to just past the closing one (the <see cref="SqlStringLiterals"/> convention).
    /// A name is not a string: an Elasticsearch SQL <c>"…"</c> still completes, an ES|QL one does not.
    /// </summary>
    public static bool InString(SearchEngine engine, string sql, int offset)
    {
        if (string.IsNullOrEmpty(sql) || offset <= 0 || offset > sql.Length)
        {
            return false;
        }

        foreach (var token in Tokens(engine, sql))
        {
            if (token.Start >= offset)
            {
                break;
            }
            if (token.Kind == SearchTokenKind.String && offset > token.Start && offset <= token.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the last token of <paramref name="fragment"/> is a line comment by the rules of the
    /// language it is read in — so a <c>;</c> appended on the same line would be swallowed by it.</summary>
    public static bool EndsWithLineComment(SearchEngine engine, string fragment)
    {
        var tokens = Tokens(engine, fragment);

        return tokens.Count > 0 && tokens[^1].Kind == SearchTokenKind.LineComment;
    }

    /// <summary>Offset just past the last code token (a <c>;</c> included), or null when there is none.</summary>
    public static int? CodeEnd(SearchEngine engine, string sql)
    {
        int? end = null;
        foreach (var token in Tokens(engine, sql))
        {
            if (token.IsCode)
            {
                end = token.End;
            }
        }

        return end;
    }

    /// <summary>
    /// <paramref name="sql"/> with every string and number literal replaced, each statement by its own
    /// language: a closed string keeps its quotes around a <c>?</c> (<c>'?'</c>, <c>"?"</c>, <c>"""?"""</c>),
    /// an unterminated one keeps its opening quote (<c>'?</c>), a number becomes <c>?</c> — the forms
    /// <see cref="SqlRedactor"/> and the MySQL redactor emit. Names, comments and layout are kept, so an
    /// Elasticsearch SQL <c>"x"</c> (a name) survives and an ES|QL <c>"x"</c> (a string) does not.
    /// </summary>
    public static string RedactLiterals(SearchEngine engine, string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql ?? "";
        }

        var sb = new StringBuilder(sql.Length);
        var copied = 0;
        foreach (var token in Tokens(engine, sql))
        {
            var placeholder = token.Kind switch
            {
                SearchTokenKind.String => StringPlaceholder(token),
                SearchTokenKind.Number => SqlRedactor.NumberPlaceholder,
                _ => null,
            };
            if (placeholder is null)
            {
                continue;
            }

            sb.Append(sql, copied, token.Start - copied).Append(placeholder);
            copied = token.End;
        }

        sb.Append(sql, copied, sql.Length - copied);

        return sb.ToString();
    }

    // ---- Shared by the two dialects ----

    /// <summary>
    /// The write guard for both engines (spec, Behaviour 7). A statement is a read when its lead word(s) are a
    /// known read of its language — a row of the language table, <c>DELETE</c> excepted; an OpenSearch
    /// <c>DELETE</c> names a write; an ES|QL statement with an <c>INTO</c> command names a write; every other
    /// statement is risky by the conservative default (an unknown first word), widening only (§1.2).
    /// <para>
    /// A statement with no code but <c>;</c> — the second statement of <c>select 1;;</c>, or a lone <c>;</c> —
    /// is not a statement and is not described, as the PostgreSQL guard skips a span with no keyword: it sends
    /// nothing (the executor skips it), so describing it as risky only made a stray <c>;;</c> prompt under
    /// "Confirm writes" and be refused on a read-only connection. Anything else that is code — a word, a
    /// parenthesis, a pipe — is still described, so nothing real is un-flagged. <see cref="Statements"/> and
    /// <see cref="Split"/> keep the bare <c>;</c> (their spans follow <see cref="StatementSplitter"/>'s
    /// contract).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<StatementRisk> Describe(SearchEngine engine, string sql)
    {
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return described;
        }

        foreach (var statement in Statements(engine, sql))
        {
            if (statement.Tokens.All(t => !t.IsCode || t is { Kind: SearchTokenKind.Punctuation, Text: ";" }))
            {
                continue;
            }

            var found = new List<string>();
            if (engine == SearchEngine.OpenSearch && statement.Verb == "DELETE")
            {
                found.Add("DELETE");
            }
            if (engine == SearchEngine.Elasticsearch && statement.Language == SearchLanguage.Piped
                && CommandWords(statement).Contains("INTO"))
            {
                found.Add("INTO");
            }

            var risk = new StatementRisk(statement.Span.Text.Trim(), statement.Verb, found);
            if (found.Count == 0 && !statement.LanguageFromLeadWord)
            {
                risk = risk with { RiskyVerbs = [statement.Verb], FromConservativeDefault = true };
            }

            described.Add(risk);
        }

        return described;
    }

    /// <summary>
    /// The upper-cased word in each command position of a piped statement: after every <c>|</c> outside
    /// strings, names and comments (at any parenthesis depth), and at the head of each parenthesised branch of
    /// a <c>FORK</c>. Wider than <see cref="SearchStatement.PipeCommands"/> on purpose — it feeds refusals.
    /// </summary>
    internal static IReadOnlyList<string> CommandWords(SearchStatement statement)
    {
        var words = new List<string>();
        var code = statement.Tokens.Where(t => t.IsCode).ToList();
        var depth = 0;
        int? forkDepth = null;
        for (var i = 0; i < code.Count; i++)
        {
            var token = code[i];
            var next = i + 1 < code.Count ? code[i + 1] : null;
            if (token.Text == "(")
            {
                if (forkDepth == depth && next is { Kind: SearchTokenKind.Word })
                {
                    words.Add(next.Text.ToUpperInvariant());
                }
                depth++;
                continue;
            }
            if (token.Text == ")")
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (token.Text != "|" || next is not { Kind: SearchTokenKind.Word })
            {
                continue;
            }

            var command = next.Text.ToUpperInvariant();
            words.Add(command);
            if (command == "FORK")
            {
                forkDepth = depth;
            }
            else if (forkDepth == depth)
            {
                forkDepth = null;
            }
        }

        return words;
    }

    /// <summary>
    /// The first index name in <paramref name="statement"/> that begins with <c>.</c>, or null: a bare name
    /// (<c>.kibana</c>, <c>.*</c>), a quoted or backticked name whose text — or one of its comma-separated,
    /// <c>cluster:</c>-qualified parts — starts with a dot, and in a piped statement's source segment a string
    /// naming its source that does. A dot that ends a path (<c>a.b</c>, <c>t.*</c>, <c>"a"."b"</c>) is not one,
    /// nor is a string anywhere else (<c>where msg = '.x'</c>). Accident prevention, not a boundary.
    /// </summary>
    internal static string? DotIndexName(SearchEngine engine, SearchStatement statement)
    {
        var code = statement.Tokens.Where(t => t.IsCode).ToList();
        var sourceSegmentEnd = code.Count;
        if (statement.Language == SearchLanguage.Piped)
        {
            var depth = 0;
            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].Text == "(") depth++;
                else if (code[i].Text == ")") depth = Math.Max(0, depth - 1);
                else if (code[i].Text == "|" && depth == 0 && i > 0)
                {
                    sourceSegmentEnd = i;
                    break;
                }
            }
        }

        for (var i = 0; i < code.Count; i++)
        {
            var token = code[i];
            var previous = i > 0 ? code[i - 1] : null;
            var next = i + 1 < code.Count ? code[i + 1] : null;
            var adjacentToPrevious = previous is not null && previous.End == token.Start;

            var startsADottedName = token.Text == "." && next is not null && next.Start == token.End
                && (next.Kind == SearchTokenKind.Word || next.Text == "*");
            if (startsADottedName)
            {
                var endsAPath = adjacentToPrevious
                    && (previous!.Kind is SearchTokenKind.Word or SearchTokenKind.QuotedName or SearchTokenKind.Number
                        || previous.Text is ")" or "]");
                if (!endsAPath)
                {
                    return "." + next!.Text;
                }
            }

            if (token.Kind == SearchTokenKind.QuotedName && !(adjacentToPrevious && previous!.Text == ".")
                && DottedPart(Unquoted(token)) is { } quoted)
            {
                return quoted;
            }

            if (token.Kind == SearchTokenKind.String && i < sourceSegmentEnd
                && statement.Language == SearchLanguage.Piped && NamesTheSource(engine, statement, code, i)
                && DottedPart(Unquoted(token)) is { } named)
            {
                return named;
            }
        }

        return null;
    }

    // ---- The scan ----

    private enum Mode
    {
        ElasticsearchPrelude,
        ElasticsearchSql,
        EsQl,
        OpenSearchPrelude,
        OpenSearchSql,
        Ppl,
    }

    private sealed record Raw(
        int Start, SearchLanguage Language, bool FromLeadWord, IReadOnlyList<string> LeadWords,
        List<SearchToken> Tokens);

    private sealed record ScanResult(IReadOnlyList<SearchStatement> Statements, IReadOnlyList<SearchToken> Tokens);

    private static readonly HashSet<string> ElasticsearchOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "EXPLAIN", "SYS", "DEBUG", "PROMQL",
    };

    private static readonly HashSet<string> OpenSearchOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "EXPLAIN", "DELETE", "SEARCH", "SOURCE", "MULTISEARCH", "MAKERESULTS",
        "GRAPHLOOKUP",
    };

    private static readonly HashSet<string> Continuers = new(StringComparer.OrdinalIgnoreCase)
    {
        ",", "(", "|", "UNION", "INTERSECT", "EXCEPT", "MINUS",
    };

    private static ScanResult Scan(SearchEngine engine, string sql)
    {
        var all = new List<SearchToken>();
        var raws = new List<Raw>();
        if (string.IsNullOrEmpty(sql))
        {
            return new ScanResult([], all);
        }

        var prelude = engine == SearchEngine.Elasticsearch ? Mode.ElasticsearchPrelude : Mode.OpenSearchPrelude;
        var pos = 0;
        var previousCodeEnd = -1;
        while (pos < sql.Length)
        {
            // Leading whitespace and comments, by the prelude reading; an own-line comment block abutting the
            // first code token starts the statement.
            int? blockStart = null;
            var lastCommentEnd = -1;
            SearchToken? first = null;
            var i = pos;
            while (i < sql.Length)
            {
                if (char.IsWhiteSpace(sql[i]))
                {
                    i++;
                    continue;
                }

                var token = Lex(sql, i, prelude);
                if (token.IsCode)
                {
                    first = token;
                    break;
                }

                all.Add(token);
                if (previousCodeEnd < 0 || ContainsNewline(sql, previousCodeEnd, token.Start))
                {
                    if (blockStart is null || HasBlankLine(sql, lastCommentEnd, token.Start))
                    {
                        blockStart = token.Start;
                    }
                    lastCommentEnd = token.End;
                }
                i = token.End;
            }

            if (first is null)
            {
                break;
            }

            int? leading = blockStart is { } b && !HasBlankLine(sql, lastCommentEnd, first.Start) ? b : null;
            var leadWords = LeadWords(sql, first, prelude);
            var decided = Decide(engine, first, leadWords);

            List<SearchToken> tokens;
            int end;
            SearchLanguage language;
            if (decided is { } known)
            {
                language = known;
                (tokens, end) = LexStatement(engine, sql, first.Start, ModeFor(engine, known));
            }
            else
            {
                (tokens, end) = LexStatement(engine, sql, first.Start, ModeFor(engine, SearchLanguage.Sql));
                language = HasTopLevelPipe(tokens) ? SearchLanguage.Piped : SearchLanguage.Sql;
                if (language == SearchLanguage.Piped)
                {
                    (tokens, end) = LexStatement(engine, sql, first.Start, ModeFor(engine, SearchLanguage.Piped));
                }
            }

            all.AddRange(tokens);
            raws.Add(new Raw(leading ?? first.Start, language, decided is not null, leadWords, tokens));
            previousCodeEnd = tokens.Last(t => t.IsCode).End;
            pos = end;
        }

        var statements = new List<SearchStatement>(raws.Count);
        for (var r = 0; r < raws.Count; r++)
        {
            var start = r == 0 ? 0 : raws[r].Start;
            var limit = r == raws.Count - 1 ? sql.Length : raws[r + 1].Start;
            var stop = OwnedEnd(sql, all, raws[r].Tokens.Last(t => t.IsCode).End, limit);
            var raw = raws[r];
            statements.Add(new SearchStatement(
                new StatementSpan(start, stop, sql[start..stop]),
                raw.Language,
                raw.FromLeadWord,
                raw.LeadWords,
                raw.Tokens.First(t => t.IsCode).Text.ToUpperInvariant(),
                PipeCommands(raw.Tokens),
                raw.Tokens));
        }

        return new ScanResult(statements, all);
    }

    /// <summary>
    /// One statement from <paramref name="start"/> by <paramref name="mode"/>'s rules: up to and including a
    /// <c>;</c>, up to the last code token before a blank-line boundary (comments after it are left for the
    /// prelude reading of the next statement), or to the end of the text.
    /// </summary>
    private static (List<SearchToken> Tokens, int End) LexStatement(SearchEngine engine, string sql, int start, Mode mode)
    {
        var tokens = new List<SearchToken>();
        var depth = 0;
        SearchToken? previousCode = null;
        var i = start;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
                continue;
            }

            var token = Lex(sql, i, mode);
            if (token.IsCode)
            {
                if (previousCode is not null && depth == 0 && HasBlankLine(sql, previousCode.End, token.Start)
                    && OpensStatement(engine, token) && !Continuers.Contains(previousCode.Text))
                {
                    var cut = previousCode.End;
                    tokens.RemoveAll(t => t.Start >= cut);

                    return (tokens, cut);
                }

                if (token.Text == "(") depth++;
                else if (token.Text == ")" && depth > 0) depth--;
                previousCode = token;
            }

            tokens.Add(token);
            if (token.Text == ";")
            {
                return (tokens, token.End);
            }
            i = token.End;
        }

        return (tokens, sql.Length);
    }

    private static bool OpensStatement(SearchEngine engine, SearchToken token)
        => token.Kind == SearchTokenKind.Word
           && (engine == SearchEngine.Elasticsearch ? ElasticsearchOpeners : OpenSearchOpeners).Contains(token.Text);

    private static Mode ModeFor(SearchEngine engine, SearchLanguage language) => (engine, language) switch
    {
        (SearchEngine.Elasticsearch, SearchLanguage.Sql) => Mode.ElasticsearchSql,
        (SearchEngine.Elasticsearch, _) => Mode.EsQl,
        (_, SearchLanguage.Sql) => Mode.OpenSearchSql,
        _ => Mode.Ppl,
    };

    /// <summary>The first word and, when the next code token is a word, the second — by the prelude reading.</summary>
    private static IReadOnlyList<string> LeadWords(string sql, SearchToken first, Mode prelude)
    {
        if (first.Kind != SearchTokenKind.Word)
        {
            return [];
        }

        var words = new List<string> { first.Text.ToUpperInvariant() };
        var i = first.End;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
                continue;
            }

            var token = Lex(sql, i, prelude);
            if (!token.IsCode)
            {
                i = token.End;
                continue;
            }
            if (token.Kind == SearchTokenKind.Word)
            {
                words.Add(token.Text.ToUpperInvariant());
            }
            break;
        }

        return words;
    }

    /// <summary>The language table, or null for "otherwise".</summary>
    private static SearchLanguage? Decide(SearchEngine engine, SearchToken first, IReadOnlyList<string> words)
    {
        var lead = words.Count > 0 ? words[0] : null;
        var second = words.Count > 1 ? words[1] : null;
        if (engine == SearchEngine.Elasticsearch)
        {
            return lead switch
            {
                "SELECT" or "DESCRIBE" or "DESC" or "EXPLAIN" or "SYS" or "DEBUG" => SearchLanguage.Sql,
                "SHOW" => second == "INFO" ? SearchLanguage.Piped : SearchLanguage.Sql,
                "FROM" or "ROW" or "TS" or "PROMQL" => SearchLanguage.Piped,
                _ => null,
            };
        }

        if (lead is null)
        {
            return first.Text == "|" ? SearchLanguage.Piped : null;
        }

        return lead switch
        {
            "SELECT" or "DELETE" => SearchLanguage.Sql,
            "SHOW" or "DESCRIBE" => second == "TABLES" ? SearchLanguage.Sql : SearchLanguage.Piped,
            "SEARCH" or "SOURCE" or "INDEX" or "EXPLAIN" or "MULTISEARCH" or "UNION" or "MAKERESULTS"
                or "GRAPHLOOKUP" => SearchLanguage.Piped,
            _ => null,
        };
    }

    private static bool HasTopLevelPipe(IReadOnlyList<SearchToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (!token.IsCode) continue;
            if (token.Text == "(") depth++;
            else if (token.Text == ")" && depth > 0) depth--;
            else if (token.Text == "|" && depth == 0) return true;
        }

        return false;
    }

    private static IReadOnlyList<string> PipeCommands(IReadOnlyList<SearchToken> tokens)
    {
        var commands = new List<string>();
        var code = tokens.Where(t => t.IsCode).ToList();
        var depth = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].Text == "(") depth++;
            else if (code[i].Text == ")" && depth > 0) depth--;
            else if (code[i].Text == "|" && depth == 0)
            {
                commands.Add(i + 1 < code.Count && code[i + 1].Kind == SearchTokenKind.Word
                    ? code[i + 1].Text.ToUpperInvariant()
                    : "");
            }
        }

        return commands;
    }

    /// <summary><see cref="StatementSplitter"/>'s rule: past the last token, any comment sharing that token's
    /// line, and the whitespace after — but in front of the first comment on a line of its own.</summary>
    private static int OwnedEnd(string sql, IReadOnlyList<SearchToken> all, int contentEnd, int limit)
    {
        foreach (var token in all)
        {
            if (token.Start >= limit)
            {
                break;
            }
            if (token.Start < contentEnd || token.IsCode)
            {
                continue;
            }
            if (ContainsNewline(sql, contentEnd, token.Start))
            {
                return token.Start;
            }
            contentEnd = token.End;
        }

        return limit;
    }

    // ---- The lexer ----

    /// <summary>The token starting at <paramref name="i"/>, which is not whitespace, by <paramref name="mode"/>'s
    /// rules.</summary>
    private static SearchToken Lex(string s, int i, Mode mode)
    {
        var c = s[i];
        var next = i + 1 < s.Length ? s[i + 1] : '\0';

        // Comments.
        if (c == '/' && next == '*')
        {
            // Every Elasticsearch language nests block comments — ES|QL measured live (SearchLiveTests): `FROM i
            // /* a /* b */ c */ | LIMIT 1` runs. OpenSearch SQL and PPL do not: the same text is a syntax error.
            return BlockComment(s, i, nested: mode is Mode.ElasticsearchSql or Mode.ElasticsearchPrelude or Mode.EsQl);
        }
        if (c == '-' && next == '-' && DashDashIsComment(s, i, mode))
        {
            return LineComment(s, i);
        }
        if (c == '/' && next == '/' && mode is not (Mode.ElasticsearchSql or Mode.OpenSearchSql))
        {
            return LineComment(s, i);
        }
        if (c == '#' && mode is Mode.OpenSearchSql or Mode.OpenSearchPrelude)
        {
            return LineComment(s, i);
        }

        // Quoted spans.
        if (c == '`')
        {
            return Quoted(s, i, SearchTokenKind.QuotedName, "`", doubling: true, backslash: false);
        }
        if (c == '"')
        {
            return mode switch
            {
                Mode.ElasticsearchSql or Mode.ElasticsearchPrelude
                    => Quoted(s, i, SearchTokenKind.QuotedName, "\"", doubling: true, backslash: false),
                Mode.EsQl when string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0
                    => Quoted(s, i, SearchTokenKind.String, "\"\"\"", doubling: false, backslash: false),
                Mode.EsQl => Quoted(s, i, SearchTokenKind.String, "\"", doubling: false, backslash: true),
                _ => Quoted(s, i, SearchTokenKind.String, "\"", doubling: true, backslash: true),
            };
        }
        if (c == '\'' && mode != Mode.EsQl)
        {
            var backslash = mode is not (Mode.ElasticsearchSql or Mode.ElasticsearchPrelude);
            return Quoted(s, i, SearchTokenKind.String, "'", doubling: true, backslash);
        }

        // Words and numbers.
        if (IsWordStart(c))
        {
            var end = i + 1;
            while (end < s.Length && IsWordPart(s[end])) end++;
            return Token(s, SearchTokenKind.Word, i, end);
        }
        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && !NamePrecedes(s, i)))
        {
            return Number(s, i);
        }

        if (c == '|' && next == '|')
        {
            return Token(s, SearchTokenKind.Punctuation, i, i + 2);
        }

        return Token(s, SearchTokenKind.Punctuation, i, i + 1);
    }

    private static bool DashDashIsComment(string s, int i, Mode mode) => mode switch
    {
        Mode.ElasticsearchSql or Mode.ElasticsearchPrelude => true,
        Mode.OpenSearchSql or Mode.OpenSearchPrelude => i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2]),
        _ => false,
    };

    private static SearchToken LineComment(string s, int i)
    {
        var end = i;
        while (end < s.Length && s[end] is not ('\n' or '\r')) end++;

        return Token(s, SearchTokenKind.LineComment, i, end);
    }

    private static SearchToken BlockComment(string s, int i, bool nested)
    {
        var depth = 0;
        var j = i;
        while (j < s.Length)
        {
            if (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '*' && (nested || depth == 0))
            {
                depth++;
                j += 2;
                continue;
            }
            if (s[j] == '*' && j + 1 < s.Length && s[j + 1] == '/')
            {
                depth--;
                j += 2;
                if (depth == 0)
                {
                    return Token(s, SearchTokenKind.BlockComment, i, j);
                }
                continue;
            }
            j++;
        }

        return Token(s, SearchTokenKind.BlockComment, i, s.Length, terminated: false);
    }

    /// <summary>A span from <paramref name="delimiter"/> to the next one: a doubled delimiter continues it when
    /// <paramref name="doubling"/>, a backslash takes the next character with it when
    /// <paramref name="backslash"/>.</summary>
    private static SearchToken Quoted(string s, int i, SearchTokenKind kind, string delimiter, bool doubling, bool backslash)
    {
        var j = i + delimiter.Length;
        while (j < s.Length)
        {
            if (backslash && s[j] == '\\')
            {
                j += 2;
                continue;
            }
            if (string.CompareOrdinal(s, j, delimiter, 0, delimiter.Length) == 0)
            {
                if (doubling && j + 1 < s.Length && s[j + 1] == delimiter[0])
                {
                    j += 2;
                    continue;
                }

                return Token(s, kind, i, j + delimiter.Length);
            }
            j++;
        }

        return Token(s, kind, i, s.Length, terminated: false);
    }

    private static SearchToken Number(string s, int i)
    {
        var j = i;
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        if (j < s.Length && s[j] == '.' && (j + 1 >= s.Length || !IsWordStart(s[j + 1])))
        {
            j++;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        }
        if (j + 1 < s.Length && s[j] is 'e' or 'E'
            && (char.IsAsciiDigit(s[j + 1]) || (s[j + 1] is '+' or '-' && j + 2 < s.Length && char.IsAsciiDigit(s[j + 2]))))
        {
            j += 2;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        }

        return Token(s, SearchTokenKind.Number, i, j);
    }

    private static SearchToken Token(string s, SearchTokenKind kind, int start, int end, bool terminated = true)
        => new(kind, start, end, s[start..end], terminated);

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '@';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';

    /// <summary>Whether the character before <paramref name="i"/> ends a name or a value, so a <c>.</c> at
    /// <paramref name="i"/> continues a path rather than starting a number.</summary>
    private static bool NamePrecedes(string s, int i)
        => i > 0 && (IsWordPart(s[i - 1]) || s[i - 1] is '"' or '`' or ')' or ']');

    // ---- Helpers ----

    private static string StringPlaceholder(SearchToken token)
    {
        var quote = token.Text.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"\"" : token.Text[..1];

        return token.Terminated ? quote + "?" + quote : quote + "?";
    }

    private static string Unquoted(SearchToken token)
    {
        var quote = token.Text.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"\"" : token.Text[..1];
        var inner = token.Text[quote.Length..];
        if (token.Terminated && inner.Length >= quote.Length)
        {
            inner = inner[..^quote.Length];
        }

        return quote.Length == 1 ? inner.Replace(quote + quote, quote) : inner;
    }

    /// <summary>The first comma-separated part of a delimited name that begins with a dot (after an optional
    /// <c>cluster:</c> prefix), or null.</summary>
    private static string? DottedPart(string name)
    {
        foreach (var raw in name.Split(','))
        {
            var part = raw.Trim();
            var colon = part.LastIndexOf(':');
            if (colon >= 0)
            {
                part = part[(colon + 1)..];
            }
            if (part.StartsWith('.'))
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the string at <paramref name="index"/> names the piped statement's source: on Elasticsearch
    /// anywhere in a <c>FROM</c> or <c>TS</c> source segment (it holds index patterns only); on OpenSearch as
    /// the value of <c>source=</c> / <c>index=</c>, or after a <c>,</c> in the source segment — so a filter's
    /// string (<c>search source=logs msg='.x'</c>) or a <c>ROW</c> value is not taken for an index.
    /// </summary>
    private static bool NamesTheSource(SearchEngine engine, SearchStatement statement, List<SearchToken> code, int index)
    {
        if (engine == SearchEngine.Elasticsearch)
        {
            return statement.LeadWords.Count > 0 && statement.LeadWords[0] is "FROM" or "TS";
        }

        var previous = index > 0 ? code[index - 1] : null;
        if (previous?.Text == ",")
        {
            return true;
        }

        return previous?.Text == "=" && index > 1 && code[index - 2] is { Kind: SearchTokenKind.Word } word
               && word.Text.ToUpperInvariant() is "SOURCE" or "INDEX";
    }

    private static bool ContainsNewline(string sql, int from, int to)
        => from >= 0 && to <= sql.Length && from < to && sql.IndexOf('\n', from, to - from) >= 0;

    private static bool HasBlankLine(string sql, int from, int to)
    {
        if (from < 0 || to > sql.Length || from >= to)
        {
            return false;
        }

        var newlines = 0;
        for (var i = from; i < to; i++)
        {
            if (sql[i] == '\n' && ++newlines >= 2)
            {
                return true;
            }
        }

        return false;
    }
}
