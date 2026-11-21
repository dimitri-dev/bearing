using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bearing.Data.Search;

/// <summary>
/// What "read on from here" needs for an Elasticsearch or OpenSearch SQL result, held inside the opaque
/// <c>QueryResult.Continuation</c> string: which engine and language (so the cursor goes back to the endpoint
/// that issued it, <see cref="Endpoint"/>), the server's cursor, the column layout the first page fixed, and
/// which statement of the run this is. It holds no statement text and no credential: a continuation sends the
/// cursor alone, never the statement again.
/// <para>
/// <b>In memory only — never logged, persisted or shown (§1.3).</b> The cursor is a server handle on a
/// running read; nothing here ever puts the token or the cursor in a message.
/// </para>
/// </summary>
internal sealed record SearchContinuation(
    SearchEngine Engine,
    SearchLanguage Language,
    string Cursor,
    SearchLayout Layout,
    int StatementIndex)
{
    /// <summary>Marks a token as this class's: a DynamoDB token (bare base64url) or anything else lacks it.</summary>
    internal const string Prefix = "search1.";

    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>The endpoint the cursor came from and goes back to.</summary>
    public SearchEndpoint Endpoint => SearchProtocol.EndpointFor(Engine, Language);

    /// <summary>
    /// The token as an opaque string: <see cref="Prefix"/> then base64url JSON. Only a SQL result pages, so a
    /// piped language, a blank cursor or a negative index is refused (<see cref="ArgumentException"/>) — a token
    /// <see cref="Decode"/> would refuse is never written.
    /// </summary>
    public string Encode()
    {
        if (Language != SearchLanguage.Sql || string.IsNullOrEmpty(Cursor) || StatementIndex < 0)
        {
            throw new ArgumentException("Only a SQL result with a cursor continues.");
        }

        var dto = new Dto
        {
            V = Version,
            Engine = Engine.ToString(),
            Language = Language.ToString(),
            Cursor = Cursor,
            Columns = Layout.Columns
                .Select(c => new ColumnDto { Name = c.Name, Type = c.TypeName, Kind = c.Kind.ToString() })
                .ToList(),
            Index = StatementIndex,
        };

        return Prefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(dto, Json));
    }

    /// <summary>
    /// The token <paramref name="text"/> encodes. Anything that is not exactly a token this class wrote — no
    /// prefix (a DynamoDB token), not base64url, not JSON, a missing or mistyped field, another version, a
    /// piped language — throws <see cref="SearchContinuationException"/>; there is no partial result. The
    /// exception names nothing of the input.
    /// </summary>
    public static SearchContinuation Decode(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new SearchContinuationException();
        }

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(FromBase64Url(text[Prefix.Length..]), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new SearchContinuationException();
        }

        if (dto is null
            || dto.V != Version
            || !TryEnum<SearchEngine>(dto.Engine, out var engine)
            || !TryEnum<SearchLanguage>(dto.Language, out var language)
            || language != SearchLanguage.Sql
            || string.IsNullOrEmpty(dto.Cursor)
            || dto.Columns is null
            || dto.Index is null or < 0)
        {
            throw new SearchContinuationException();
        }

        var columns = new List<SearchLayoutColumn>(dto.Columns.Count);
        foreach (var column in dto.Columns)
        {
            if (column?.Name is null
                || column.Type is null
                || !TryEnum<SearchCellKind>(column.Kind, out var kind))
            {
                throw new SearchContinuationException();
            }

            columns.Add(new SearchLayoutColumn(column.Name, column.Type, kind));
        }

        return new SearchContinuation(engine, language, dto.Cursor, new SearchLayout(columns), dto.Index.Value);
    }

    private static bool TryEnum<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;

        // Names only: Enum.TryParse would also take "0" or "1, 2", which this class never writes.
        return text is not null
               && Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)
               && Enum.TryParse(text, ignoreCase: false, out value);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        if (text.Contains('+') || text.Contains('/') || text.Contains('='))
        {
            throw new FormatException();
        }

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }

    // Nullable on purpose: a token missing a field must read as garbage, not as a default.
    private sealed class Dto
    {
        [JsonPropertyName("v")]
        public int? V { get; set; }

        [JsonPropertyName("e")]
        public string? Engine { get; set; }

        [JsonPropertyName("l")]
        public string? Language { get; set; }

        [JsonPropertyName("u")]
        public string? Cursor { get; set; }

        [JsonPropertyName("c")]
        public List<ColumnDto>? Columns { get; set; }

        [JsonPropertyName("i")]
        public int? Index { get; set; }
    }

    private sealed class ColumnDto
    {
        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("t")]
        public string? Type { get; set; }

        [JsonPropertyName("k")]
        public string? Kind { get; set; }
    }
}

/// <summary>A continuation string that is not one this provider issued. The message is fixed text: the input
/// may carry a server cursor, so it is never echoed.</summary>
internal sealed class SearchContinuationException : FormatException
{
    public SearchContinuationException()
        : base("The continuation token is not valid.")
    {
    }
}
