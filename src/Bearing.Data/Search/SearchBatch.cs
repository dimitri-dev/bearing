namespace Bearing.Data.Search;

/// <summary>
/// Which of its engine's two languages a statement is sent in. <c>Data</c>'s own copy: the <c>Sql</c> project
/// has one of the same name, and the two projects may not reference each other (§2.2).
/// </summary>
internal enum SearchLanguage
{
    /// <summary>Elasticsearch SQL or OpenSearch SQL.</summary>
    Sql,

    /// <summary>The engine's piped language: ES|QL on Elasticsearch, PPL on OpenSearch.</summary>
    Piped,
}

/// <summary>
/// One statement of a script as the executor sends it.
/// </summary>
/// <param name="Start">Offset of its first code token in the script.</param>
/// <param name="Text">What is sent: from its first code token to its last one, the terminating <c>;</c> left
/// out — so no comment in front of it (a <c>--</c> or <c>#</c> the prelude reads as a comment is code to
/// ES|QL or PPL) and none after it. Comments between its tokens are kept as typed. Empty for a bare <c>;</c>,
/// which is a statement to the editor's split and has nothing to send.</param>
/// <param name="Language">The language it is read and sent in.</param>
/// <param name="Verb">Its first code token, upper-cased — the lead word, or the punctuation it begins with — for
/// messages.</param>
internal sealed record SearchBatchStatement(int Start, string Text, SearchLanguage Language, string Verb)
{
    /// <summary>True for a bare <c>;</c>: there is nothing to send.</summary>
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// <c>Bearing.Data</c>'s own statement split and language decision for Elasticsearch and OpenSearch scripts —
/// a copy of <c>Bearing.Sql.SearchScan</c>'s, because this project may not reference that one (§2.2).
/// <para>
/// <b>It must split no coarser than, and decide no differently from, <c>SearchScan</c>.</b> The write guard
/// and the <c>bearing</c> allow-list judge the statements <c>SearchScan</c> sees; the executor sends the
/// statements this class sees. If the two drifted, a write could ride behind a read the guard had already
/// cleared, or a statement be sent to the other language's endpoint (lesson 2026-11-19: two copies drifted
/// within a day). <c>SearchScanParityTests</c> pins both to one corpus — change one, change both. The rules,
/// which <c>SearchScan</c>'s type documentation states in full:
/// </para>
/// <list type="bullet">
///   <item><b>Language</b> from the first word(s), case-insensitively, after whitespace and comments.
///     Elasticsearch: SQL for <c>SELECT DESCRIBE DESC EXPLAIN SYS DEBUG SHOW</c> (not <c>SHOW INFO</c>); ES|QL
///     for <c>FROM ROW TS PROMQL</c> and <c>SHOW INFO</c>. OpenSearch: SQL for <c>SELECT DELETE</c>,
///     <c>SHOW TABLES</c>, <c>DESCRIBE TABLES</c>; PPL for <c>SEARCH SOURCE INDEX EXPLAIN MULTISEARCH UNION
///     MAKERESULTS GRAPHLOOKUP</c>, any other <c>SHOW</c> / <c>DESCRIBE</c>, and a leading <c>|</c>. Otherwise
///     the statement is read by SQL rules and is piped when that reading holds a <c>|</c> (not <c>||</c>) at
///     parenthesis depth 0 — and is then read again by the piped rules.</item>
///   <item><b>Prelude</b> (the space before and between statements, and the lead words): <c>--</c>,
///     <c>//</c> and nested <c>/* */</c> on Elasticsearch; <c>#</c>, <c>--</c> followed by whitespace or the
///     end, <c>//</c> and non-nested <c>/* */</c> on OpenSearch; strings and names by the engine's SQL
///     rules.</item>
///   <item><b>Per language</b>: Elasticsearch SQL — <c>'…'</c> strings (<c>''</c>, no backslash),
///     <c>"…"</c> and <c>`…`</c> names, <c>--</c> and nested <c>/* */</c>. ES|QL — <c>"…"</c> strings with
///     backslash escapes, <c>"""…"""</c> with none, <c>`…`</c> names, <c>//</c> and nested
///     <c>/* */</c> (measured live); no <c>--</c>, no <c>'</c> strings. OpenSearch SQL — <c>'…'</c> and <c>"…"</c> strings
///     (doubled quote and backslash), <c>`…`</c> names, <c>#</c>, <c>--</c> + whitespace, non-nested
///     <c>/* */</c>. PPL — <c>'…'</c> and <c>"…"</c> strings (backslash and doubled quote), <c>`…`</c> names,
///     <c>//</c> and non-nested <c>/* */</c>. An unterminated string, name or comment runs to the end.</item>
///   <item><b>End of a statement</b>: a <c>;</c> outside strings, names and comments; or a blank line at depth
///     0 before an opener word (Elasticsearch <c>SELECT SHOW DESCRIBE EXPLAIN SYS DEBUG PROMQL</c>; OpenSearch
///     <c>SELECT SHOW DESCRIBE EXPLAIN DELETE SEARCH SOURCE MULTISEARCH MAKERESULTS GRAPHLOOKUP</c>) unless the
///     code token before it is <c>, ( | UNION INTERSECT EXCEPT MINUS</c>.</item>
///   <item>A script of only whitespace and comments has no statement. A bare <c>;</c> is one (empty) — kept so
///     that statement <i>N</i> here is statement <i>N</i> of the editor's split.</item>
/// </list>
/// </summary>
internal static class SearchBatch
{
    /// <summary>The statements of <paramref name="script"/>, in order, each with its language.</summary>
    public static IReadOnlyList<SearchBatchStatement> Statements(SearchEngine engine, string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<SearchBatchStatement>();
        var prelude = engine == SearchEngine.Elasticsearch ? Mode.ElasticsearchPrelude : Mode.OpenSearchPrelude;
        var pos = 0;
        while (pos < script.Length)
        {
            Token? first = null;
            var i = pos;
            while (i < script.Length)
            {
                if (char.IsWhiteSpace(script[i]))
                {
                    i++;
                    continue;
                }

                var token = Lex(script, i, prelude);
                if (token.IsCode)
                {
                    first = token;
                    break;
                }
                i = token.End;
            }

            if (first is null)
            {
                break;
            }

            var leadWords = LeadWords(script, first, prelude);
            var decided = Decide(engine, first, leadWords);

            List<Token> tokens;
            int end;
            SearchLanguage language;
            if (decided is { } known)
            {
                language = known;
                (tokens, end) = LexStatement(engine, script, first.Start, ModeFor(engine, known));
            }
            else
            {
                (tokens, end) = LexStatement(engine, script, first.Start, ModeFor(engine, SearchLanguage.Sql));
                language = HasTopLevelPipe(tokens) ? SearchLanguage.Piped : SearchLanguage.Sql;
                if (language == SearchLanguage.Piped)
                {
                    (tokens, end) = LexStatement(engine, script, first.Start, ModeFor(engine, SearchLanguage.Piped));
                }
            }

            // The terminating ';' is never sent (spec, Behaviour 4); a bare ';' leaves nothing.
            var code = tokens.Where(t => t.IsCode).ToList();
            var head = code[0];
            if (code[^1].Text == ";")
            {
                code.RemoveAt(code.Count - 1);
            }
            var text = code.Count == 0 ? "" : script[head.Start..code[^1].End];

            statements.Add(new SearchBatchStatement(head.Start, text, language, head.Text.ToUpperInvariant()));
            pos = end;
        }

        return statements;
    }

    /// <inheritdoc cref="IsPlainSelect(SearchEngine, SearchLanguage, string)"/>
    public static bool IsPlainSelect(SearchEngine engine, SearchBatchStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return IsPlainSelect(engine, statement.Language, statement.Text);
    }

    /// <summary>
    /// Whether a SQL statement reads as a <b>plain select</b> — the shape OpenSearch's main SQL engine pages with
    /// a cursor. Read from its code tokens by this class's own lexer, in the statement's language (strings,
    /// quoted names and comments do not count; words compare case-insensitively): the first word is
    /// <c>SELECT</c>; a <c>FROM</c> stands at parenthesis depth 0; and none of these appear — the words
    /// <c>LIMIT OFFSET GROUP HAVING JOIN UNION INTERSECT EXCEPT MINUS OVER DISTINCT</c>, a second <c>SELECT</c>
    /// (a subquery), or an aggregate call (<see cref="AggregateFunctions"/> followed by <c>(</c>). False for a
    /// piped statement, an empty one, and anything else.
    /// <para>
    /// Why it matters (measured against OpenSearch 3.10.0, <c>SearchLiveTests</c>): with <c>fetch_size</c> in the
    /// body, a shape the main engine cannot page — a <c>LIMIT</c>, an aggregate — is answered by the legacy
    /// engine, whose rows are right but whose column types are not (a keyword key and <c>count(*)</c> both come
    /// back as <c>double</c>), and which refuses a backticked dotted index name. Without <c>fetch_size</c> the
    /// main engine answers with the real types. So <see cref="SearchProtocol.FirstPageBody"/> sends
    /// <c>fetch_size</c> to OpenSearch SQL only for a plain select. A wrong "plain" costs only the legacy
    /// engine's type labels; a wrong "not plain" costs only paging — the answer then arrives whole, capped by
    /// the cluster's <c>plugins.query.size_limit</c> — never rows the server sent.
    /// </para>
    /// </summary>
    public static bool IsPlainSelect(SearchEngine engine, SearchLanguage language, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (language != SearchLanguage.Sql)
        {
            return false;
        }

        var mode = ModeFor(engine, SearchLanguage.Sql);
        var code = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var token = Lex(text, i, mode);
            if (token.IsCode)
            {
                code.Add(token);
            }
            i = token.End;
        }

        if (code.Count == 0 || !IsWord(code[0], "SELECT"))
        {
            return false;
        }

        var depth = 0;
        var topLevelFrom = false;
        for (var i = 0; i < code.Count; i++)
        {
            var token = code[i];
            if (token.Text == "(")
            {
                depth++;
                continue;
            }
            if (token.Text == ")")
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (token.Kind != Kind.Word)
            {
                continue;
            }

            if ((i > 0 && IsWord(token, "SELECT")) || NotPlainWords.Contains(token.Text))
            {
                return false;
            }
            if (AggregateFunctions.Contains(token.Text) && i + 1 < code.Count && code[i + 1].Text == "(")
            {
                return false;
            }
            if (depth == 0 && IsWord(token, "FROM"))
            {
                topLevelFrom = true;
            }
        }

        return topLevelFrom;
    }

    /// <summary>The language's name as the user knows it — "SQL", "ES|QL" or "PPL" — which leads a server's
    /// error message, so a parse error says which language the statement was read as.</summary>
    public static string LanguageName(SearchEngine engine, SearchLanguage language) => (engine, language) switch
    {
        (_, SearchLanguage.Sql) => "SQL",
        (SearchEngine.Elasticsearch, _) => "ES|QL",
        _ => "PPL",
    };

    // ---- The scan (SearchScan's, line for line where it decides a boundary or a language) ----

    private enum Kind
    {
        Word,
        Number,
        QuotedName,
        String,
        LineComment,
        BlockComment,
        Punctuation,
    }

    private enum Mode
    {
        ElasticsearchPrelude,
        ElasticsearchSql,
        EsQl,
        OpenSearchPrelude,
        OpenSearchSql,
        Ppl,
    }

    private sealed record Token(Kind Kind, int Start, int End, string Text)
    {
        public bool IsCode => Kind is not (Kind.LineComment or Kind.BlockComment);
    }

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

    /// <summary>Words that make a statement not a plain select (<see cref="IsPlainSelect(SearchEngine, SearchLanguage, string)"/>).</summary>
    private static readonly HashSet<string> NotPlainWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIMIT", "OFFSET", "GROUP", "HAVING", "JOIN", "UNION", "INTERSECT", "EXCEPT", "MINUS", "OVER", "DISTINCT",
    };

    /// <summary>Aggregate functions: one of these followed by <c>(</c> makes a statement not a plain select.</summary>
    internal static readonly IReadOnlySet<string> AggregateFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "STDDEV", "STDDEV_POP", "STDDEV_SAMP", "VARIANCE", "VAR_POP",
        "VAR_SAMP", "PERCENTILE", "PERCENTILE_APPROX", "TAKE", "FIRST", "LAST", "EARLIEST", "LATEST",
    };

    private static bool IsWord(Token token, string word)
        => token.Kind == Kind.Word && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>One statement from <paramref name="start"/> by <paramref name="mode"/>'s rules: up to and
    /// including a <c>;</c>, up to the last code token before a blank-line boundary, or to the end.</summary>
    private static (List<Token> Tokens, int End) LexStatement(SearchEngine engine, string sql, int start, Mode mode)
    {
        var tokens = new List<Token>();
        var depth = 0;
        Token? previousCode = null;
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

    private static bool OpensStatement(SearchEngine engine, Token token)
        => token.Kind == Kind.Word
           && (engine == SearchEngine.Elasticsearch ? ElasticsearchOpeners : OpenSearchOpeners).Contains(token.Text);

    private static Mode ModeFor(SearchEngine engine, SearchLanguage language) => (engine, language) switch
    {
        (SearchEngine.Elasticsearch, SearchLanguage.Sql) => Mode.ElasticsearchSql,
        (SearchEngine.Elasticsearch, _) => Mode.EsQl,
        (_, SearchLanguage.Sql) => Mode.OpenSearchSql,
        _ => Mode.Ppl,
    };

    /// <summary>The first word and, when the next code token is a word, the second — by the prelude reading.</summary>
    private static IReadOnlyList<string> LeadWords(string sql, Token first, Mode prelude)
    {
        if (first.Kind != Kind.Word)
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
            if (token.Kind == Kind.Word)
            {
                words.Add(token.Text.ToUpperInvariant());
            }
            break;
        }

        return words;
    }

    /// <summary>The language table, or null for "otherwise".</summary>
    private static SearchLanguage? Decide(SearchEngine engine, Token first, IReadOnlyList<string> words)
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

    private static bool HasTopLevelPipe(IReadOnlyList<Token> tokens)
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

    // ---- The lexer ----

    /// <summary>The token starting at <paramref name="i"/>, which is not whitespace, by <paramref name="mode"/>'s
    /// rules.</summary>
    private static Token Lex(string s, int i, Mode mode)
    {
        var c = s[i];
        var next = i + 1 < s.Length ? s[i + 1] : '\0';

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

        if (c == '`')
        {
            return Quoted(s, i, Kind.QuotedName, "`", doubling: true, backslash: false);
        }
        if (c == '"')
        {
            return mode switch
            {
                Mode.ElasticsearchSql or Mode.ElasticsearchPrelude
                    => Quoted(s, i, Kind.QuotedName, "\"", doubling: true, backslash: false),
                Mode.EsQl when string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0
                    => Quoted(s, i, Kind.String, "\"\"\"", doubling: false, backslash: false),
                Mode.EsQl => Quoted(s, i, Kind.String, "\"", doubling: false, backslash: true),
                _ => Quoted(s, i, Kind.String, "\"", doubling: true, backslash: true),
            };
        }
        if (c == '\'' && mode != Mode.EsQl)
        {
            var backslash = mode is not (Mode.ElasticsearchSql or Mode.ElasticsearchPrelude);
            return Quoted(s, i, Kind.String, "'", doubling: true, backslash);
        }

        if (IsWordStart(c))
        {
            var end = i + 1;
            while (end < s.Length && IsWordPart(s[end])) end++;
            return Make(s, Kind.Word, i, end);
        }
        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && !NamePrecedes(s, i)))
        {
            return Number(s, i);
        }

        if (c == '|' && next == '|')
        {
            return Make(s, Kind.Punctuation, i, i + 2);
        }

        return Make(s, Kind.Punctuation, i, i + 1);
    }

    private static bool DashDashIsComment(string s, int i, Mode mode) => mode switch
    {
        Mode.ElasticsearchSql or Mode.ElasticsearchPrelude => true,
        Mode.OpenSearchSql or Mode.OpenSearchPrelude => i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2]),
        _ => false,
    };

    private static Token LineComment(string s, int i)
    {
        var end = i;
        while (end < s.Length && s[end] is not ('\n' or '\r')) end++;

        return Make(s, Kind.LineComment, i, end);
    }

    private static Token BlockComment(string s, int i, bool nested)
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
                    return Make(s, Kind.BlockComment, i, j);
                }
                continue;
            }
            j++;
        }

        return Make(s, Kind.BlockComment, i, s.Length);
    }

    /// <summary>A span from <paramref name="delimiter"/> to the next one: a doubled delimiter continues it when
    /// <paramref name="doubling"/>, a backslash takes the next character with it when
    /// <paramref name="backslash"/>. Unterminated, it runs to the end of the text.</summary>
    private static Token Quoted(string s, int i, Kind kind, string delimiter, bool doubling, bool backslash)
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

                return Make(s, kind, i, j + delimiter.Length);
            }
            j++;
        }

        return Make(s, kind, i, s.Length);
    }

    private static Token Number(string s, int i)
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

        return Make(s, Kind.Number, i, j);
    }

    private static Token Make(string s, Kind kind, int start, int end) => new(kind, start, end, s[start..end]);

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '@';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';

    private static bool NamePrecedes(string s, int i)
        => i > 0 && (IsWordPart(s[i - 1]) || s[i - 1] is '"' or '`' or ')' or ']');

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
