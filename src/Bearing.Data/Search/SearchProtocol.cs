using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bearing.Data.Search;

/// <summary>
/// Where a statement of one language goes on one engine, and how that endpoint pages.
/// </summary>
/// <param name="Engine">The server.</param>
/// <param name="Language">The language the endpoint reads.</param>
/// <param name="Path">Path and query, already escaped: <c>/_sql?format=json</c>, <c>/_query?format=json</c>,
/// <c>/_plugins/_sql?format=jdbc</c>, <c>/_plugins/_ppl?format=jdbc</c>.</param>
/// <param name="LanguageName">"SQL", "ES|QL" or "PPL" — what leads a server error from this endpoint.</param>
/// <param name="Pages">Whether the endpoint answers with a cursor and takes one back (the two SQL endpoints).
/// ES|QL and PPL have no paging: an answer is every row the server sent.</param>
internal sealed record SearchEndpoint(
    SearchEngine Engine, SearchLanguage Language, string Path, string LanguageName, bool Pages);

/// <summary>One column as a first page declares it: its name and the server's own type name.</summary>
internal sealed record SearchColumn(string Name, string TypeName);

/// <summary>
/// One answer from any of the four endpoints, in one neutral shape.
/// </summary>
/// <param name="Columns">The declared columns — always on a first page, never on a continuation page.</param>
/// <param name="Rows">The rows as the server's JSON values, one element per column, detached from the parsed
/// document.</param>
/// <param name="Cursor">The server's cursor when more rows wait; null when the answer is complete (absent, null
/// or empty in the body, and always on an endpoint that does not page).</param>
/// <param name="Notice">What the server said about the answer (<see cref="SearchProtocol.Notice"/>), or null.</param>
internal sealed record SearchPage(
    IReadOnlyList<SearchColumn>? Columns, IReadOnlyList<JsonElement[]> Rows, string? Cursor, string? Notice);

/// <summary>
/// The four endpoints Bearing sends statements to, as request bodies out and one neutral page in (spec,
/// Behaviour 4 and 5). Pure: no HTTP here — the executor sends what this writes through
/// <see cref="SearchConnectionFactory.SendAsync"/> and hands back what came.
/// <list type="table">
///   <listheader><term>Endpoint</term><description>First page / continuation</description></listheader>
///   <item><term>Elasticsearch SQL <c>/_sql?format=json</c></term><description><c>{"query", "fetch_size",
///     "page_timeout"}</c> / <c>{"cursor"}</c>; answer <c>columns</c>, <c>rows</c>, <c>cursor</c>.</description></item>
///   <item><term>ES|QL <c>/_query?format=json</c></term><description><c>{"query"}</c> / none; answer
///     <c>columns</c>, <c>values</c>, <c>is_partial</c>.</description></item>
///   <item><term>OpenSearch SQL <c>/_plugins/_sql?format=jdbc</c></term><description><c>{"query",
///     "fetch_size"}</c> for a plain select, <c>{"query"}</c> otherwise (<see cref="AsksForPages"/>) /
///     <c>{"cursor"}</c>; answer <c>schema</c>, <c>datarows</c>, <c>cursor</c>.</description></item>
///   <item><term>PPL <c>/_plugins/_ppl?format=jdbc</c></term><description><c>{"query"}</c> / none; answer
///     <c>schema</c>, <c>datarows</c>.</description></item>
/// </list>
/// <para>
/// Bodies are written with <see cref="Utf8JsonWriter"/>, so a statement's quotes, backslashes and newlines are
/// escaped by the writer, never by string concatenation. No other field is sent — in particular not
/// <c>field_multi_value_leniency</c> (it would return one arbitrary value of an array field, which is a
/// silent alteration) and not <c>time_zone</c> (the server's default is UTC, which is what the values are read
/// as).
/// </para>
/// </summary>
internal static partial class SearchProtocol
{
    /// <summary>
    /// How long Elasticsearch keeps a SQL cursor between pages. The server's default is 45 seconds — too short
    /// for a person reading a grid before pressing "Load more" — so the spec chose 5 minutes (Decision 5).
    /// OpenSearch has no such field: its cursor lifetime is a cluster setting a request cannot change.
    /// </summary>
    public const string PageTimeout = "5m";

    /// <summary>Rows asked for when the caller has no cap — the servers' own default page.</summary>
    public const int DefaultFetchSize = 1_000;

    /// <summary>The most rows one page may ask for: both servers' ceiling on a page.</summary>
    public const int MaxFetchSize = 10_000;

    /// <summary>The longest warning text kept from one header or body entry; a longer one is cut with "…".</summary>
    internal const int MaxNoticeLength = 500;

    /// <summary>
    /// What leads an ES|QL page's notice when the answer carries <c>"is_partial": true</c> (<see cref="IsPartial"/>),
    /// ahead of any warning text. It says what the server said — that it marked the answer partial — and what that
    /// can mean for the rows; it does not guess which shard or why.
    /// </summary>
    internal const string PartialAnswerSentence =
        "Elasticsearch marked this answer as partial (is_partial), so rows may be missing.";

    private static readonly SearchEndpoint ElasticsearchSql =
        new(SearchEngine.Elasticsearch, SearchLanguage.Sql, "/_sql?format=json", "SQL", Pages: true);

    private static readonly SearchEndpoint EsQl =
        new(SearchEngine.Elasticsearch, SearchLanguage.Piped, "/_query?format=json", "ES|QL", Pages: false);

    private static readonly SearchEndpoint OpenSearchSql =
        new(SearchEngine.OpenSearch, SearchLanguage.Sql, "/_plugins/_sql?format=jdbc", "SQL", Pages: true);

    private static readonly SearchEndpoint Ppl =
        new(SearchEngine.OpenSearch, SearchLanguage.Piped, "/_plugins/_ppl?format=jdbc", "PPL", Pages: false);

    /// <summary>
    /// One RFC 7234 warning value: a three-digit code, an agent, and a quoted text with backslash escapes —
    /// <c>299 Elasticsearch-9.1.0-abc123 "No limit defined, adding default limit of [1000]"</c> — optionally
    /// followed by a quoted date. One header line may carry several, separated by commas.
    /// </summary>
    [GeneratedRegex("""(?<code>\d{3})\s+(?<agent>[^\s"]+)\s+"(?<text>(?:[^"\\]|\\.)*)"(?:\s+"[^"]*")?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex WarningValue();

    /// <summary>The endpoint statements of <paramref name="language"/> go to on <paramref name="engine"/>.</summary>
    public static SearchEndpoint EndpointFor(SearchEngine engine, SearchLanguage language) => (engine, language) switch
    {
        (SearchEngine.Elasticsearch, SearchLanguage.Sql) => ElasticsearchSql,
        (SearchEngine.Elasticsearch, _) => EsQl,
        (_, SearchLanguage.Sql) => OpenSearchSql,
        _ => Ppl,
    };

    /// <summary>The page size to ask for: <paramref name="maxRows"/> clamped to 1..<see cref="MaxFetchSize"/>,
    /// or <see cref="DefaultFetchSize"/> when the caller has no cap.</summary>
    public static int FetchSize(int? maxRows)
        => maxRows is { } rows ? Math.Clamp(rows, 1, MaxFetchSize) : DefaultFetchSize;

    // ---- Requests ------------------------------------------------------------------------------------

    /// <summary>
    /// The body that runs <paramref name="query"/> (already without its trailing <c>;</c>) on
    /// <paramref name="endpoint"/>. Elasticsearch SQL carries <c>fetch_size</c> (<see cref="FetchSize"/>) and
    /// <c>page_timeout</c> for every statement — it pages every shape itself. OpenSearch SQL carries
    /// <c>fetch_size</c> only for a plain select (<see cref="AsksForPages"/>); any other statement, and every
    /// piped one, carries the query alone and is answered whole.
    /// </summary>
    public static byte[] FirstPageBody(SearchEndpoint endpoint, string query, int? maxRows)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(query);

        return Write(writer =>
        {
            writer.WriteString("query", query);
            if (!AsksForPages(endpoint, query))
            {
                return;
            }

            writer.WriteNumber("fetch_size", FetchSize(maxRows));
            if (endpoint.Engine == SearchEngine.Elasticsearch)
            {
                writer.WriteString("page_timeout", PageTimeout);
            }
        });
    }

    /// <summary>
    /// Whether the first request for <paramref name="query"/> asks <paramref name="endpoint"/> for pages
    /// (<c>fetch_size</c>): always on Elasticsearch SQL; on OpenSearch SQL only when the statement reads as a
    /// plain select (<see cref="SearchBatch.IsPlainSelect(SearchEngine, SearchLanguage, string)"/>), because
    /// there <c>fetch_size</c> sends every other shape to the legacy engine, which mislabels column types
    /// (measured against OpenSearch 3.10.0). Never on a piped endpoint. A statement sent without it comes back
    /// whole, with no cursor.
    /// </summary>
    public static bool AsksForPages(SearchEndpoint endpoint, string query)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(query);

        return endpoint.Pages
               && (endpoint.Engine == SearchEngine.Elasticsearch
                   || SearchBatch.IsPlainSelect(endpoint.Engine, endpoint.Language, query));
    }

    /// <summary>
    /// The body that asks <paramref name="endpoint"/> for the page after <paramref name="cursor"/> — the cursor
    /// alone; the statement is not sent again. Only the SQL endpoints page: asking a piped one is a programming
    /// error and throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static byte[] ContinuationBody(SearchEndpoint endpoint, string cursor)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        if (!endpoint.Pages)
        {
            throw new InvalidOperationException($"{endpoint.LanguageName} has no cursor to continue.");
        }

        return Write(writer => writer.WriteString("cursor", cursor));
    }

    // ---- Responses -----------------------------------------------------------------------------------

    /// <summary>
    /// A first page: the declared columns (required), the rows (each as wide as the columns), the cursor, and
    /// the notice. Elasticsearch's <c>columns[]</c> / OpenSearch's <c>schema[]</c> give each column's
    /// <c>name</c> and <c>type</c>; OpenSearch's non-empty <c>alias</c>, when it sends one, is the name shown.
    /// Anything not that shape throws <see cref="SearchProtocolException"/> — never an empty result.
    /// </summary>
    public static SearchPage ParseFirstPage(SearchEndpoint endpoint, SearchResponse response)
        => Parse(endpoint, response, expectedWidth: null);

    /// <summary>
    /// A continuation page: rows and cursor only — the layout was fixed by the first page and travels in the
    /// continuation, so columns in the body are not read. Every row must be <paramref name="columnCount"/>
    /// wide; anything else throws <see cref="SearchProtocolException"/>.
    /// </summary>
    public static SearchPage ParseContinuationPage(SearchEndpoint endpoint, SearchResponse response, int columnCount)
        => Parse(endpoint, response, columnCount);

    /// <summary>
    /// What the server said about an answer, as one line: the text of every <c>Warning</c> header value
    /// (<see cref="WarningTexts"/>), then every entry of the body's <c>warnings[]</c>, de-duplicated in order
    /// and joined with a space. Null when there is none. It goes to <c>QueryResult.Notice</c>, never
    /// <c>Message</c> — which on a result that continues means "the request budget cut this page short".
    /// </summary>
    public static string? Notice(IReadOnlyList<string> warningHeaders, IEnumerable<string> bodyWarnings)
    {
        ArgumentNullException.ThrowIfNull(warningHeaders);
        ArgumentNullException.ThrowIfNull(bodyWarnings);

        var texts = new List<string>();
        foreach (var header in warningHeaders)
        {
            texts.AddRange(WarningTexts(header));
        }
        texts.AddRange(bodyWarnings);

        var distinct = texts
            .Select(t => Bounded(t.Trim()))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return distinct.Count == 0 ? null : string.Join(" ", distinct);
    }

    /// <summary>
    /// The texts of one raw <c>Warning</c> header value: the quoted part of each <c>code agent "text"</c>
    /// entry, unquoted, with <c>\"</c> and <c>\\</c> unescaped and percent-escapes decoded (Elasticsearch
    /// percent-encodes characters outside printable ASCII in a warning). A value without that shape is taken
    /// whole, trimmed.
    /// </summary>
    public static IReadOnlyList<string> WarningTexts(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        var matches = WarningValue().Matches(header);
        if (matches.Count == 0)
        {
            return [header.Trim()];
        }

        return matches.Select(m => PercentDecode(Unescape(m.Groups["text"].Value))).ToList();
    }

    private static SearchPage Parse(SearchEndpoint endpoint, SearchResponse response, int? expectedWidth)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccess)
        {
            throw new SearchProtocolException(endpoint, $"it answered HTTP {response.StatusCode}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response.Body);
        }
        catch (JsonException)
        {
            throw new SearchProtocolException(endpoint, "the answer is not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SearchProtocolException(endpoint, "the answer is not a JSON object");
            }

            IReadOnlyList<SearchColumn>? columns = null;
            if (expectedWidth is null)
            {
                columns = ReadColumns(endpoint, root);
            }

            var width = expectedWidth ?? columns!.Count;
            var rows = ReadRows(endpoint, root, width);
            var cursor = endpoint.Pages ? ReadCursor(endpoint, root) : null;
            var notice = Notice(response.Warnings, BodyWarnings(root));
            if (endpoint is { Engine: SearchEngine.Elasticsearch, Language: SearchLanguage.Piped } && IsPartial(root))
            {
                notice = notice is null ? PartialAnswerSentence : $"{PartialAnswerSentence} {notice}";
            }

            return new SearchPage(columns, rows, cursor, notice);
        }
    }

    private static List<SearchColumn> ReadColumns(SearchEndpoint endpoint, JsonElement root)
    {
        var key = endpoint.Engine == SearchEngine.Elasticsearch ? "columns" : "schema";
        if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new SearchProtocolException(endpoint, $"the answer has no \"{key}\" array");
        }

        var columns = new List<SearchColumn>(array.GetArrayLength());
        foreach (var column in array.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object
                || !column.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !column.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                throw new SearchProtocolException(endpoint, $"a \"{key}\" entry has no name or type");
            }

            var shown = name.GetString()!;
            if (endpoint.Engine == SearchEngine.OpenSearch
                && column.TryGetProperty("alias", out var alias)
                && alias.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(alias.GetString()))
            {
                shown = alias.GetString()!;
            }

            columns.Add(new SearchColumn(shown, type.GetString()!));
        }

        return columns;
    }

    private static List<JsonElement[]> ReadRows(SearchEndpoint endpoint, JsonElement root, int width)
    {
        var key = (endpoint.Engine, endpoint.Language) switch
        {
            (SearchEngine.Elasticsearch, SearchLanguage.Sql) => "rows",
            (SearchEngine.Elasticsearch, _) => "values",
            _ => "datarows",
        };
        if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new SearchProtocolException(endpoint, $"the answer has no \"{key}\" array");
        }

        // One detached copy of the array, so the rows outlive the parsed document without a copy per value.
        var detached = array.Clone();
        var rows = new List<JsonElement[]>(detached.GetArrayLength());
        foreach (var row in detached.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                throw new SearchProtocolException(endpoint, $"a \"{key}\" entry is not an array");
            }
            if (row.GetArrayLength() != width)
            {
                throw new SearchProtocolException(
                    endpoint, $"a row has {row.GetArrayLength()} values for {width} columns");
            }

            rows.Add(row.EnumerateArray().ToArray());
        }

        return rows;
    }

    /// <summary>
    /// ES|QL's <c>"is_partial": true</c>. Measured against Elasticsearch 9.5.6: with
    /// <c>esql.query.allow_partial_results</c> at its default (true), a query over a shard that cannot be read
    /// answers HTTP 200 with <c>is_partial</c> true and <b>no</b> <c>Warning</c> header — so without this the
    /// answer would look whole. Absent, false or not a boolean reads as not partial: only the server's own
    /// <c>true</c> says rows may be missing.
    /// </summary>
    private static bool IsPartial(JsonElement root)
        => root.TryGetProperty("is_partial", out var partial) && partial.ValueKind == JsonValueKind.True;

    private static string? ReadCursor(SearchEndpoint endpoint, JsonElement root)
    {
        if (!root.TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (cursor.ValueKind != JsonValueKind.String)
        {
            throw new SearchProtocolException(endpoint, "its \"cursor\" is not a string");
        }

        var text = cursor.GetString();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>OpenSearch's body <c>warnings[]</c> (3.x): a string entry as itself, an object entry by its
    /// <c>message</c> (or <c>reason</c>) string, anything else as its JSON text. Elasticsearch sends none.</summary>
    private static IEnumerable<string> BodyWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var texts = new List<string>();
        foreach (var warning in warnings.EnumerateArray())
        {
            switch (warning.ValueKind)
            {
                case JsonValueKind.String:
                    texts.Add(warning.GetString()!);
                    break;
                case JsonValueKind.Object when (TextOf(warning, "message") ?? TextOf(warning, "reason")) is { } text:
                    texts.Add(text);
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    texts.Add(warning.GetRawText());
                    break;
            }
        }

        return texts;

        static string? TextOf(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Unescape(string quoted)
    {
        if (!quoted.Contains('\\'))
        {
            return quoted;
        }

        var sb = new StringBuilder(quoted.Length);
        for (var i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] == '\\' && i + 1 < quoted.Length)
            {
                i++;
            }
            sb.Append(quoted[i]);
        }

        return sb.ToString();
    }

    // Uri.UnescapeDataString leaves a '%' that does not start a valid escape as it is, so text that was never
    // encoded passes through unchanged unless it happens to hold a valid %XX sequence.
    private static string PercentDecode(string text) => text.Contains('%') ? Uri.UnescapeDataString(text) : text;

    private static string Bounded(string text)
        => text.Length <= MaxNoticeLength ? text : text[..MaxNoticeLength] + "…";
}

/// <summary>
/// An answer from a search endpoint that is not the shape Bearing reads — not JSON, no column list, a row of
/// the wrong width, a cursor that is not a string. A typed failure naming the endpoint, never an empty result.
/// The message names the shape only: no part of the body is quoted, so no value reaches it.
/// </summary>
internal sealed class SearchProtocolException : Exception
{
    public SearchProtocolException(SearchEndpoint endpoint, string problem)
        : base($"{SearchErrors.EngineName(endpoint.Engine)} {endpoint.LanguageName} ({EndpointPath(endpoint)}) answered in a shape Bearing does not read: {problem}.")
    {
        Endpoint = endpoint;
    }

    /// <summary>The endpoint that answered.</summary>
    public SearchEndpoint Endpoint { get; }

    private static string EndpointPath(SearchEndpoint endpoint)
    {
        var query = endpoint.Path.IndexOf('?', StringComparison.Ordinal);

        return query < 0 ? endpoint.Path : endpoint.Path[..query];
    }
}
