using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bearing.Core.Data;

namespace Bearing.Data.Search;

/// <summary>What a grid column holds, chosen once from the declared type and the first page
/// (<see cref="SearchValues.FirstPage"/>), then carried in the continuation to every later page.</summary>
internal enum SearchCellKind
{
    /// <summary><see cref="bool"/> — JSON <c>true</c> / <c>false</c>.</summary>
    Boolean,

    /// <summary><see cref="long"/> — a JSON integer that fits Int64.</summary>
    Integer,

    /// <summary><see cref="decimal"/> — a JSON integer (<c>unsigned_long</c> can exceed Int64).</summary>
    UnsignedLong,

    /// <summary><see cref="double"/> — a JSON number.</summary>
    Float,

    /// <summary><see cref="string"/> — a JSON string only (text-like types, <c>date_nanos</c>).</summary>
    Text,

    /// <summary><see cref="byte"/>[] — a base64 JSON string.</summary>
    Binary,

    /// <summary><see cref="DateTime"/> with <c>Kind = Utc</c> — ISO-8601 with <c>Z</c> or an offset (converted to
    /// UTC), or OpenSearch's <c>yyyy-MM-dd HH:mm:ss[.fraction]</c> (UTC with no suffix).</summary>
    DateTime,

    /// <summary><see cref="DateOnly"/> — <c>yyyy-MM-dd</c>.</summary>
    Date,

    /// <summary><see cref="TimeOnly"/> — <c>HH:mm:ss[.fraction]</c>.</summary>
    Time,

    /// <summary><see cref="string"/> — anything: a JSON string as itself, a number or boolean as its JSON text, an
    /// object or array as its JSON text. Every type without a row of its own, and every column a first-page value
    /// did not fit.</summary>
    Json,
}

/// <summary>One column of a layout: the name shown, the server's type name, and the cell kind chosen.</summary>
internal sealed record SearchLayoutColumn(string Name, string TypeName, SearchCellKind Kind)
{
    /// <summary>The descriptor a result carries: the server's type name as <c>DataTypeName</c>, the kind's CLR
    /// type, no base-table origin (nothing here is editable).</summary>
    public ColumnDescriptor Descriptor => new(Name, TypeName, SearchValues.ClrType(Kind));
}

/// <summary>The columns a result is fixed to: decided by the first page, carried by the continuation (§9.10 —
/// the grid never rebuilds its columns mid-result).</summary>
internal sealed record SearchLayout(IReadOnlyList<SearchLayoutColumn> Columns)
{
    public IReadOnlyList<ColumnDescriptor> Descriptors() => Columns.Select(c => c.Descriptor).ToList();
}

/// <summary>
/// Elasticsearch and OpenSearch JSON values as grid cells (spec, Behaviour 5). Pure: declared columns and JSON
/// rows in, a layout and cells out.
/// <para>
/// <b>Reads are lossless.</b> A value is never altered to fit, and never dropped:
/// </para>
/// <list type="bullet">
///   <item>JSON <c>null</c> is null in any column.</item>
///   <item><b>First page</b>: a column whose declared type suggests a kind keeps it only when every non-null
///     value on the page fits; one misfit — an array where ES|QL returned a multivalued field, an integer past
///     Int64, a string where a number was declared, an unparseable date — makes the whole column
///     <see cref="SearchCellKind.Json"/> (text) for this result.</item>
///   <item><b>Later page</b>: the layout is fixed, so a value that does not fit its column throws
///     <see cref="SearchValueMisfitException"/> naming the column — the executor turns it into that page's
///     error. Nothing is substituted.</item>
///   <item>Numbers are read invariantly from the JSON text: a <c>double</c> keeps the server's value, a
///     <c>decimal</c> is never taken through <c>double</c>.</item>
///   <item>A date-time column with no non-null value on the first page is text: the declared names do not say
///     which shape a later value will take (ES|QL's <c>date</c> is ISO-8601, OpenSearch's is
///     <c>yyyy-MM-dd</c>), and guessing would fail a later page that the text column takes whole.</item>
/// </list>
/// </summary>
internal static partial class SearchValues
{
    /// <summary>The longest excerpt of a value quoted in a misfit message.</summary>
    internal const int ExcerptLength = 80;

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "byte", "short", "integer", "long",
    };

    private static readonly HashSet<string> FloatTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "float", "half_float", "scaled_float", "double",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword", "text", "ip", "version", "constant_keyword", "wildcard", "match_only_text", "string",
        "semantic_text", "search_as_you_type", "counted_keyword", "char", "varchar", "date_nanos",
    };

    private static readonly HashSet<string> DateTimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "datetime", "timestamp",
    };

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private static readonly string[] SpaceFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] TimeFormats =
    [
        "HH:mm:ss",
        "HH:mm:ss.FFFFFFF",
    ];

    private static readonly JsonWriterOptions JsonText = new()
    {
        // Readable text, not \u-escaped: the cell is shown and copied as written. The values are unchanged.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d{1,7})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceDateTime();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyShape();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(\.\d{1,7})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeShape();

    [GeneratedRegex(@"^-?\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerText();

    /// <summary>The CLR type a column of <paramref name="kind"/> carries.</summary>
    public static Type ClrType(SearchCellKind kind) => kind switch
    {
        SearchCellKind.Boolean => typeof(bool),
        SearchCellKind.Integer => typeof(long),
        SearchCellKind.UnsignedLong => typeof(decimal),
        SearchCellKind.Float => typeof(double),
        SearchCellKind.Binary => typeof(byte[]),
        SearchCellKind.DateTime => typeof(DateTime),
        SearchCellKind.Date => typeof(DateOnly),
        SearchCellKind.Time => typeof(TimeOnly),
        _ => typeof(string),
    };

    /// <summary>
    /// The kind a declared type suggests before any value is seen. <c>date</c> / <c>datetime</c> /
    /// <c>timestamp</c> suggest <see cref="SearchCellKind.DateTime"/> (the first page may settle on
    /// <see cref="SearchCellKind.Date"/>); every name without a row of the type table is
    /// <see cref="SearchCellKind.Json"/>.
    /// </summary>
    public static SearchCellKind Suggested(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        if (typeName.Equals("boolean", StringComparison.OrdinalIgnoreCase)) return SearchCellKind.Boolean;
        if (IntegerTypes.Contains(typeName)) return SearchCellKind.Integer;
        if (typeName.Equals("unsigned_long", StringComparison.OrdinalIgnoreCase)) return SearchCellKind.UnsignedLong;
        if (FloatTypes.Contains(typeName)) return SearchCellKind.Float;
        if (TextTypes.Contains(typeName)) return SearchCellKind.Text;
        if (typeName.Equals("binary", StringComparison.OrdinalIgnoreCase)) return SearchCellKind.Binary;
        if (DateTimeTypes.Contains(typeName)) return SearchCellKind.DateTime;
        if (typeName.Equals("time", StringComparison.OrdinalIgnoreCase)) return SearchCellKind.Time;

        return SearchCellKind.Json;
    }

    /// <summary>
    /// A first page: the layout (each column's kind from its declared type and every non-null value on the page,
    /// falling back to <see cref="SearchCellKind.Json"/> on any misfit) and the rows converted to it. Every row
    /// must be as wide as <paramref name="columns"/> (<see cref="SearchProtocol.ParseFirstPage"/> checks).
    /// </summary>
    public static (SearchLayout Layout, IReadOnlyList<object?[]> Rows) FirstPage(
        IReadOnlyList<SearchColumn> columns, IReadOnlyList<JsonElement[]> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var layout = new List<SearchLayoutColumn>(columns.Count);
        for (var c = 0; c < columns.Count; c++)
        {
            var values = rows.Select(r => r[c]).Where(v => v.ValueKind != JsonValueKind.Null).ToList();
            layout.Add(new SearchLayoutColumn(columns[c].Name, columns[c].TypeName, Settle(columns[c].TypeName, values)));
        }

        var fixedLayout = new SearchLayout(layout);

        return (fixedLayout, Convert(fixedLayout, rows));
    }

    /// <summary>
    /// A later page, against the layout the continuation carried. A value that does not fit its column throws
    /// <see cref="SearchValueMisfitException"/>; a row of the wrong width throws <see cref="ArgumentException"/>
    /// (<see cref="SearchProtocol.ParseContinuationPage"/> rejects those first).
    /// </summary>
    public static IReadOnlyList<object?[]> LaterPage(SearchLayout layout, IReadOnlyList<JsonElement[]> rows)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);

        return Convert(layout, rows);
    }

    /// <summary>One value as a cell of <paramref name="kind"/>, or false when it does not fit. JSON null fits every
    /// kind, as null.</summary>
    public static bool TryCell(SearchCellKind kind, JsonElement value, out object? cell)
    {
        cell = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        switch (kind)
        {
            case SearchCellKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                cell = value.GetBoolean();
                return true;
            case SearchCellKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer):
                cell = integer;
                return true;
            case SearchCellKind.UnsignedLong when value.ValueKind == JsonValueKind.Number
                                                  && IntegerText().IsMatch(value.GetRawText())
                                                  && value.TryGetDecimal(out var unsigned):
                cell = unsigned;
                return true;
            case SearchCellKind.Float when value.ValueKind == JsonValueKind.Number
                                           && value.TryGetDouble(out var number) && double.IsFinite(number):
                cell = number;
                return true;
            case SearchCellKind.Text when value.ValueKind == JsonValueKind.String:
                cell = value.GetString();
                return true;
            case SearchCellKind.Binary when value.ValueKind == JsonValueKind.String && TryBase64(value.GetString()!, out var bytes):
                cell = bytes;
                return true;
            case SearchCellKind.DateTime when value.ValueKind == JsonValueKind.String && TryDateTime(value.GetString()!, out var instant):
                cell = instant;
                return true;
            case SearchCellKind.Date when value.ValueKind == JsonValueKind.String && TryDate(value.GetString()!, out var date):
                cell = date;
                return true;
            case SearchCellKind.Time when value.ValueKind == JsonValueKind.String && TryTime(value.GetString()!, out var time):
                cell = time;
                return true;
            case SearchCellKind.Json:
                cell = AsText(value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The "everything else" rendering: a JSON string as itself, a number or boolean as its JSON text,
    /// an object or array as its compact JSON text (values unchanged).</summary>
    public static string AsText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()!;
            case JsonValueKind.Object or JsonValueKind.Array:
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, JsonText))
                {
                    value.WriteTo(writer);
                }

                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
            default:
                return value.GetRawText();
        }
    }

    private static SearchCellKind Settle(string typeName, List<JsonElement> values)
    {
        var suggested = Suggested(typeName);
        if (suggested is SearchCellKind.DateTime or SearchCellKind.Time && values.Count == 0)
        {
            return SearchCellKind.Json;
        }
        if (suggested == SearchCellKind.DateTime)
        {
            if (values.All(v => TryCell(SearchCellKind.DateTime, v, out _))) return SearchCellKind.DateTime;
            if (values.All(v => TryCell(SearchCellKind.Date, v, out _))) return SearchCellKind.Date;

            return SearchCellKind.Json;
        }

        return values.All(v => TryCell(suggested, v, out _)) ? suggested : SearchCellKind.Json;
    }

    private static List<object?[]> Convert(SearchLayout layout, IReadOnlyList<JsonElement[]> rows)
    {
        var columns = layout.Columns;
        var converted = new List<object?[]>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Length != columns.Count)
            {
                throw new ArgumentException(
                    $"A row has {row.Length} values for {columns.Count} columns.", nameof(rows));
            }

            var cells = new object?[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                if (!TryCell(columns[c].Kind, row[c], out cells[c]))
                {
                    throw new SearchValueMisfitException(columns[c], Excerpt(row[c]));
                }
            }
            converted.Add(cells);
        }

        return converted;
    }

    private static bool TryBase64(string text, out byte[] bytes)
    {
        var buffer = new byte[text.Length * 3 / 4 + 3];
        if (System.Convert.TryFromBase64String(text, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }

    private static bool TryDateTime(string text, out DateTime instant)
    {
        if (IsoDateTime().IsMatch(text)
            && DateTimeOffset.TryParseExact(text, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        {
            instant = offset.UtcDateTime;
            return true;
        }
        if (SpaceDateTime().IsMatch(text)
            && System.DateTime.TryParseExact(text, SpaceFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            instant = System.DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return true;
        }

        instant = default;
        return false;
    }

    private static bool TryDate(string text, out DateOnly date)
    {
        date = default;

        return DateOnlyShape().IsMatch(text)
               && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryTime(string text, out TimeOnly time)
    {
        time = default;

        return TimeShape().IsMatch(text)
               && TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static string Excerpt(JsonElement value)
    {
        var text = value.GetRawText();

        return text.Length <= ExcerptLength ? text : text[..ExcerptLength] + "…";
    }
}

/// <summary>
/// A value on a later page that does not fit the column the first page fixed — the page fails rather than show
/// it altered (spec, Behaviour 5). The message names the column, its declared type and a bounded excerpt of the
/// value as the server sent it; the value is not changed.
/// </summary>
internal sealed class SearchValueMisfitException : Exception
{
    public SearchValueMisfitException(SearchLayoutColumn column, string excerpt)
        : base($"Column \"{column.Name}\" ({column.TypeName}): this page holds a value that does not fit the column the first page set up, so the page was not read: {excerpt}")
    {
        Column = column.Name;
        TypeName = column.TypeName;
        Excerpt = excerpt;
    }

    /// <summary>The column's name.</summary>
    public string Column { get; }

    /// <summary>The server's declared type name.</summary>
    public string TypeName { get; }

    /// <summary>At most <see cref="SearchValues.ExcerptLength"/> characters of the value's JSON text.</summary>
    public string Excerpt { get; }
}
