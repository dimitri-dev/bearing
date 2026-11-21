using Xunit;
using DataBatch = Bearing.Data.Search.SearchBatch;
using DataEngine = Bearing.Data.Search.SearchEngine;
using DataLanguage = Bearing.Data.Search.SearchLanguage;
using SqlEngine = Bearing.Sql.SearchEngine;
using SqlLanguage = Bearing.Sql.SearchLanguage;
using SqlScan = Bearing.Sql.SearchScan;
using SqlTokenKind = Bearing.Sql.SearchTokenKind;

namespace Bearing.Data.Tests;

/// <summary>
/// The executor's split (<c>Bearing.Data</c>'s <see cref="DataBatch"/>) and the write guard's
/// (<c>Bearing.Sql</c>'s <see cref="SqlScan"/>) side by side over one corpus, for both engines (SC2): the same
/// number of statements, the same text for each — the scan's statement from its first code token to its last,
/// without a terminating <c>;</c> — the same language and the same verb. The write guard and the <c>bearing</c>
/// allow-list judge what <c>SearchScan</c> sees; the executor sends what <c>SearchBatch</c> sees, so any
/// disagreement here is a statement sent unjudged or to the wrong endpoint.
/// <para>
/// The corpus is every input of <c>SearchScanTests</c>, plus scripts mixing statements and languages, strings
/// holding <c>;</c> and <c>|</c>, every comment form on the wrong engine and in the wrong language, unterminated
/// forms, line endings and blank-line boundaries.
/// </para>
/// </summary>
public class SearchScanParityTests
{
    public static readonly string[] Corpus =
    [
        // SearchScanTests — language, Elasticsearch.
        "SELECT * FROM logs", "select * from logs", "DESCRIBE logs", "desc logs", "EXPLAIN SELECT 1", "SYS COLUMNS",
        "debug select 1", "SHOW TABLES", "show functions", "SHOW", "-- a note\nSELECT 1",
        "/* a /* nested */ note */ SELECT 1", "// a note\nselect 1",
        "SHOW INFO", "show info", "SHOW /* which */ INFO", "FROM logs | LIMIT 5", "from logs", "ROW a = 1",
        "TS metrics | LIMIT 1", "PROMQL up", "-- a note\nFROM logs", "// a note\nFrom logs",
        "foo | bar", "foo bar", "foo 'a | b'", "foo \"a | b\"", "foo -- |\n", "foo /* | */ bar", "foo (a | b)",
        "foo || bar",

        // SearchScanTests — language, OpenSearch.
        "delete from logs where a = 1", "SHOW TABLES LIKE 'logs%'", "show tables like 'l%'",
        "DESCRIBE TABLES LIKE 'logs'", "describe tables like 'logs' columns like 'a%'", "# a note\nselect 1",
        "search source=logs", "source=logs | head 5", "source = logs", "SOURCE=logs", "index=logs", "describe logs",
        "show datasources", "explain source=logs | head 1", "multisearch [search source=a] [search source=b]",
        "union [source=a] [source=b]", "makeresults count=1", "graphlookup x", "| source=logs",
        "// a note\nsource=logs", "foo '|'", "foo \"|\"", "foo `a|b`", "foo # |\n", "foo -- |", "foo --| bar",
        "/* x */ show -- y\n info", "| where a > 1",

        // SearchScanTests — pipe commands.
        "FROM logs | WHERE a > 1 | LIMIT 5", "FROM logs | EVAL x = \"a | b\" | KEEP x",
        "FROM logs // | not this\n| keep x", "source=logs | fields a, b | head 5",
        "source=logs | where a = '|' | head", "select a from logs",
        "SELECT 1; FROM logs | LIMIT 1;\nshow info; describe logs",

        // SearchScanTests — splitting.
        "select 'a;b' from t; select 2", "select 'it''s;' from t", "select \"a;b\" from t; select 2",
        "select `a;b` from t", "select 1 -- ;\nfrom t", "select 1 /* ; /* ; */ ; */ from t",
        "SELECT 1 /* /* */ ; SELECT 2", "SELECT 1 // ; SELECT 2", "select 'a\\'; select 2",
        "FROM logs | WHERE a == \"x;y\"; FROM b", "FROM logs | WHERE a == \"x\\\";y\"",
        "FROM logs | WHERE a == \"\"\"x;\"y\"\"\"; FROM b", "FROM logs // ;\n| LIMIT 1", "FROM logs /* ; */ | LIMIT 1",
        "FROM logs /* /* */ ; FROM b", "FROM logs /* a /* b */ ; c */ | LIMIT 1; FROM b",
        "FROM logs | WHERE a == 'x; FROM b", "FROM logs -- ; FROM b",
        "FROM logs | KEEP `a;b`", "select 'a;b', \"c;d\" from t; select 2", "select 'it\\'s;' from t",
        "select \"it\\\";\" from t", "select 1 # ;\nfrom t", "select 1 --;\nselect 2", "select 1 /* /* */ ; select 2",
        "select 1 // ; select 2", "source=logs | where a = 'x;y'", "source=logs | where a = \"x;y\"",
        "source=logs | where a = 'it\\'s;'", "source=logs // ;\n| head 1", "source=logs /* ; */ | head 1",
        "source=logs # ; source=b", "source=logs -- ; source=b", "source=logs | fields `a;b`",

        // SearchScanTests — unterminated forms.
        "select 'abc; select 2", "select \"abc; select 2", "select /* abc; select 2", "select /* /* */ abc; select 2",
        "FROM a | WHERE b == \"x; FROM c", "FROM a | WHERE b == \"\"\"x; FROM c", "FROM a | KEEP `x; FROM c",
        "select `a; select 2", "select \"a\\", "source=a | where b = 'x; source=c", "source=a /* x; source=c",

        // SearchScanTests — blank lines, comments alone, spans.
        "select 1\n\nselect 2", "FROM logs | LIMIT 1\n\nSELECT 1", "SELECT a,\n\nb FROM t", "select a\n\nfrom t",
        "FROM a\n\n| LIMIT 1", "select (1\n\nselect 2)", "source=a\n\nsource=b",
        "select a from t\n\nunion\n\nselect b from u", "source=a |\n\nsearch source=b",
        "", "   \n ", "-- only a comment\n", "// only a comment", "/* only */", "# only a comment",
        "select 1; select 2", "select 1;\nselect 2;\n", "-- header\nselect 1;\n\n-- lead\nselect 2",
        "select 1;\n\n-- COMMIT;", "select 1; -- note\nselect 2", "select a,\n-- inner\nb from t;\nselect 3",
        "  select 1  ;  select 2  ", "select 'a;b' from t; /* c; */ select 2",
        "select 1;\n-- one\n-- two\nselect 2;\n\n/* tail */\n", "source=a;\n# what b is\nsource=b",

        // SearchScanTests — strings, line comments, code end, redaction.
        "select 'abc' from t", "select \"abc\" from t", "select abc from t", "select 'ab",
        "FROM t | WHERE a == \"abc\"", "FROM t | WHERE a == \"\"\"abc\"\"\"", "FROM t | WHERE a == 'abc'",
        "FROM t | KEEP `abc`", "select `abc` from t", "select 1 # 'abc'", "source=t | where a = 'abc'",
        "source=t | where a = \"abc\"", "source=t // 'abc'", "select 1 -- x", "select 1 // x", "select '-- x'",
        "select 1 /* x */", "FROM a // x", "FROM a -- x", "FROM a | WHERE b == \"// x\"", "select 1 # x",
        "select 1 --x", "source=a // x", "source=a # x", "source=a -- x", "select 1;", "select 1 -- trailing\n",
        "FROM a // trailing", "select 1 # trailing", "source=a // trailing", "# only",
        "select a from t where b = 'x' and c = 42", "select \"x\" from t where b = 'it''s'", "select 1 -- 'x'",
        "FROM t | WHERE b == \"x\" AND c > 1.5", "FROM t | WHERE b == \"\"\"a\"b\"\"\"", "FROM t | KEEP `x1` // 'y'",
        "select 'a' from t; FROM t | WHERE b == \"x\"",
        "select * from t where a = \"x\" and b = 'y' and c = 3", "source=t | where a = 'it\\'s' | head 5",
        "select `a1` from t # 'x'",

        // Scripts mixing statements and languages.
        "SELECT a FROM t; FROM logs | WHERE b == \"x;y\" | LIMIT 3; SHOW INFO; foo | bar; foo bar",
        "select 1;\nsource=a | head 1;\nshow tables like 'a%';\ndescribe logs;\n| where x = 1;\nfoo | bar",
        "FROM a | WHERE msg == \"a | b; c\" | KEEP msg\n\nSELECT 'x|y;z' FROM t",
        "source=a | where msg = 'a | b; c' | fields msg\n\nselect \"x|y;z\" from t",
        "-- lead\nSELECT 1 -- tail;\n; /* gap; */ FROM b /* mid */ | LIMIT 2 // end;\n",
        "# lead\nselect 1 # tail;\n; /* gap; */ source=b /* mid */ | head 2 // end;\n",
        "select 1;;select 2", ";", ";;", " ; ; ", "select 1; ;\n;", "|", "| |", "||", "(", ")", "(|)",

        // Each comment form on both engines and in both languages.
        "# hash\nselect 1", "#no space\nFROM a", "--no space\nselect 1", "-- \nsource=a", "--", "--\nselect 1",
        "select 1 --", "FROM a | WHERE x == 1 -- not a comment in ES|QL\n| LIMIT 1",
        "source=a | where x = 1 # not a comment in PPL\n| head 1", "FROM a /* a /* b */ c */ | LIMIT 1",
        "SELECT 1 /* a /* b */ c */ FROM t", "source=a /* a /* b */ c */ | head", "select 1 /* a /* b */ c */ from t",
        "// c1\n// c2\nFROM a\n// c3\n| LIMIT 1", "/**/select 1", "/*/ select 1", "/* unterminated",
        "select 1 /* unterminated", "FROM a | WHERE b == 1 /* unterminated ;",

        // Strings and names holding ; and |.
        "select 'a;b|c', \"d;e|f\", `g;h|i` from t", "FROM a | EVAL x = \"\"\"a;b|c\"\"\" | KEEP `y;z|w`",
        "source=a | eval x = \"a;b|c\", y = 'd;e|f' | fields `g;h|i`", "foo 'it''s | ;' | bar",
        "foo \"a\"\"b | ;\" bar", "FROM a | WHERE b == \"esc\\\\\"; FROM c", "select 'trailing backslash\\",
        "select \"\"\"\"", "FROM a | WHERE b == \"\"\"\"\"\"", "FROM a | WHERE b == \"\"\"x\"\"\"\"; FROM c",

        // Line endings, whitespace and numbers.
        "select 1\r\n\r\nselect 2", "FROM a\r\n\r\n| LIMIT 1", "source=a\r\n\r\nsource=b", "select 1\n \t\nselect 2",
        "\t\tselect\t1\t;\tselect 2", "select .5, a.5, 1e10, 1.e5 from t", "FROM a | EVAL x = .5 | LIMIT 1",
        "select a\n\n,b from t", "select a from t\n\nUNION\n\nselect b from u", "select a\n\nINTERSECT select b",
        "DEBUG\n\nSYS tables", "describe a\n\ndesc b", "PROMQL up\n\nPROMQL down", "source=a\n\ngraphlookup x",
        "explain select 1\n\nexplain source=a", "makeresults count=1\n\nmultisearch [source=a]",
        "select (\n\nselect 1\n\n)", "FROM a | WHERE b IN (1,\n\n2)\n\nFROM c", "Ünïcode | bar", "@var | x",
        "select 'ü;ß' from tâble; FROM `ö;` | LIMIT 1",
    ];

    public static TheoryData<SqlEngine, string> Cases()
    {
        var data = new TheoryData<SqlEngine, string>();
        foreach (var engine in new[] { SqlEngine.Elasticsearch, SqlEngine.OpenSearch })
        {
            foreach (var script in Corpus)
            {
                data.Add(engine, script);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Both_scanners_read_the_same_statements_in_the_same_languages(SqlEngine engine, string script)
    {
        var disagreements = Disagreements(engine, script);

        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    [Fact]
    public void The_whole_corpus_has_no_disagreement()
    {
        var disagreements = new List<string>();
        foreach (var engine in new[] { SqlEngine.Elasticsearch, SqlEngine.OpenSearch })
        {
            foreach (var script in Corpus)
            {
                disagreements.AddRange(Disagreements(engine, script));
            }
        }

        Assert.True(disagreements.Count == 0, $"{disagreements.Count} disagreements:\n" + string.Join("\n", disagreements));
        Assert.True(Corpus.Length > 200);
    }

    private static List<string> Disagreements(SqlEngine engine, string script)
    {
        var scanned = SqlScan.Statements(engine, script);
        var batch = DataBatch.Statements(
            engine == SqlEngine.Elasticsearch ? DataEngine.Elasticsearch : DataEngine.OpenSearch, script);
        var where = $"[{engine}] {Escape(script)}";

        if (scanned.Count != batch.Count)
        {
            return [$"{where}: {scanned.Count} statements in SearchScan, {batch.Count} in SearchBatch"];
        }

        var found = new List<string>();
        for (var i = 0; i < scanned.Count; i++)
        {
            var scan = scanned[i];
            var sent = batch[i];
            var code = scan.Tokens.Where(t => t.IsCode).ToList();
            if (code.Count > 0 && code[^1].Kind == SqlTokenKind.Punctuation && code[^1].Text == ";")
            {
                code.RemoveAt(code.Count - 1);
            }

            var expectedText = code.Count == 0 ? "" : script[code[0].Start..code[^1].End];
            var expectedLanguage = scan.Language == SqlLanguage.Sql ? DataLanguage.Sql : DataLanguage.Piped;

            if (sent.Text != expectedText)
                found.Add($"{where} #{i}: text \"{Escape(expectedText)}\" vs \"{Escape(sent.Text)}\"");
            if (sent.Language != expectedLanguage)
                found.Add($"{where} #{i}: language {scan.Language} vs {sent.Language}");
            if (sent.Verb != scan.Verb)
                found.Add($"{where} #{i}: verb {scan.Verb} vs {sent.Verb}");
            if (sent.Start != scan.Tokens.First(t => t.IsCode).Start)
                found.Add($"{where} #{i}: start {scan.Tokens.First(t => t.IsCode).Start} vs {sent.Start}");

            // What is sent lies inside the span the editor highlights and the guard judged.
            if (sent.Start < scan.Span.Start || sent.Start + sent.Text.Length > scan.Span.End)
                found.Add($"{where} #{i}: sent text outside the span {scan.Span.Start}..{scan.Span.End}");
        }

        return found;
    }

    private static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
