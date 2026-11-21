using System.Text.Json;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <see cref="SearchValues"/>: one row per line of the spec's type table, and the rules that keep reads
/// lossless (Behaviour 5, SC9) — a first-page misfit turns the whole column into text, a later-page misfit fails
/// the page naming the column, and no value is ever altered or dropped.
/// </summary>
public class SearchValuesTests
{
    // ---- The type table ----

    public static TheoryData<string, string, Type, object?> TypeTable() => new()
    {
        { "boolean", "true", typeof(bool), true },
        { "byte", "-7", typeof(long), -7L },
        { "short", "1123", typeof(long), 1123L },
        { "integer", "2147483647", typeof(long), 2147483647L },
        { "long", "9223372036854775807", typeof(long), long.MaxValue },
        { "unsigned_long", "18446744073709551615", typeof(decimal), 18446744073709551615m },
        { "float", "1.100000023841858", typeof(double), 1.100000023841858 },
        { "half_float", "0.5", typeof(double), 0.5 },
        { "scaled_float", "12.34", typeof(double), 12.34 },
        { "double", "1.7976931348623157E308", typeof(double), double.MaxValue },
        { "keyword", "\"error\"", typeof(string), "error" },
        { "text", "\"Peter F. Hamilton\"", typeof(string), "Peter F. Hamilton" },
        { "ip", "\"192.168.0.1\"", typeof(string), "192.168.0.1" },
        { "version", "\"1.2.3-beta\"", typeof(string), "1.2.3-beta" },
        { "constant_keyword", "\"c\"", typeof(string), "c" },
        { "wildcard", "\"w*\"", typeof(string), "w*" },
        { "match_only_text", "\"m\"", typeof(string), "m" },
        { "string", "\"s\"", typeof(string), "s" },
        { "binary", "\"AQID/w==\"", typeof(byte[]), new byte[] { 1, 2, 3, 255 } },
        { "date", "\"2023-01-15T00:00:00.000Z\"", typeof(DateTime), new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Utc) },
        { "datetime", "\"2000-03-15T10:20:30.123Z\"", typeof(DateTime), new DateTime(2000, 3, 15, 10, 20, 30, 123, DateTimeKind.Utc) },
        { "timestamp", "\"2024-02-29 23:59:58.5\"", typeof(DateTime), new DateTime(2024, 2, 29, 23, 59, 58, 500, DateTimeKind.Utc) },
        { "date", "\"2024-02-29\"", typeof(DateOnly), new DateOnly(2024, 2, 29) },
        { "time", "\"13:45:07.25\"", typeof(TimeOnly), new TimeOnly(13, 45, 7, 250) },
        { "date_nanos", "\"2023-01-15T00:00:00.123456789Z\"", typeof(string), "2023-01-15T00:00:00.123456789Z" },
        { "object", """{"a":{"b":[1,"x"]}}""", typeof(string), """{"a":{"b":[1,"x"]}}""" },
        { "nested", """[{"a":1},{"a":2}]""", typeof(string), """[{"a":1},{"a":2}]""" },
        { "struct", """{"é":"ü"}""", typeof(string), """{"é":"ü"}""" },
        { "array", "[1,2]", typeof(string), "[1,2]" },
        { "geo_point", "\"POINT (-71.34 41.12)\"", typeof(string), "POINT (-71.34 41.12)" },
        { "geo_shape", """{"type":"Point","coordinates":[1.5,2]}""", typeof(string), """{"type":"Point","coordinates":[1.5,2]}""" },
        { "interval", "\"P1D\"", typeof(string), "P1D" },
        { "null", "\"n\"", typeof(string), "n" },
        { "unsupported", "12", typeof(string), "12" },
        { "something_new", "true", typeof(string), "true" },
    };

    [Theory]
    [MemberData(nameof(TypeTable))]
    public void Each_declared_type_reads_as_its_row_of_the_table(string type, string json, Type clr, object? expected)
    {
        var (layout, rows) = SearchValues.FirstPage([new SearchColumn("c", type)], [[Json(json)]]);

        var column = Assert.Single(layout.Columns);
        Assert.Equal(clr, column.Descriptor.ClrType);
        Assert.Equal(type, column.Descriptor.DataTypeName);
        Assert.Equal("c", column.Descriptor.Name);
        Assert.False(column.Descriptor.HasBaseColumn);
        Assert.Equal(expected, rows[0][0]);
        if (expected is DateTime)
        {
            Assert.Equal(DateTimeKind.Utc, ((DateTime)rows[0][0]!).Kind);
        }
    }

    [Theory]
    [InlineData("BOOLEAN", typeof(bool))]
    [InlineData("Long", typeof(long))]
    [InlineData("KEYWORD", typeof(string))]
    public void Type_names_are_read_case_insensitively(string type, Type clr)
        => Assert.Equal(clr, SearchValues.ClrType(SearchValues.Suggested(type)));

    // ---- Null ----

    [Fact]
    public void Null_is_null_in_any_column_and_does_not_decide_the_type()
    {
        var (layout, rows) = SearchValues.FirstPage(
            [new SearchColumn("n", "long"), new SearchColumn("k", "keyword"), new SearchColumn("o", "object")],
            [[Json("null"), Json("null"), Json("null")], [Json("5"), Json("null"), Json("""{"a":1}""")]]);

        Assert.Equal([typeof(long), typeof(string), typeof(string)], layout.Columns.Select(c => c.Descriptor.ClrType));
        Assert.Equal([null, null, null], rows[0]);
        Assert.Equal(5L, rows[1][0]);
        Assert.Null(rows[1][1]);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("timestamp")]
    [InlineData("time")]
    public void A_date_or_time_column_with_no_value_on_the_first_page_is_text(string type)
    {
        var (layout, _) = SearchValues.FirstPage([new SearchColumn("d", type)], [[Json("null")]]);

        Assert.Equal(SearchCellKind.Json, Assert.Single(layout.Columns).Kind);
    }

    // ---- Dates ----

    [Theory]
    [InlineData("2024-01-15T10:00:00+02:00", 2024, 1, 15, 8, 0, 0)]
    [InlineData("2024-01-15T00:30:00-01:00", 2024, 1, 15, 1, 30, 0)]
    [InlineData("2024-01-01T00:30:00+01:00", 2023, 12, 31, 23, 30, 0)]
    [InlineData("2024-01-15T10:00:00Z", 2024, 1, 15, 10, 0, 0)]
    [InlineData("2024-01-15 10:00:00", 2024, 1, 15, 10, 0, 0)]
    public void An_iso_offset_is_converted_to_utc(string text, int y, int mo, int d, int h, int mi, int s)
    {
        var (layout, rows) = SearchValues.FirstPage([new SearchColumn("t", "date")], [[Json($"\"{text}\"")]]);

        Assert.Equal(SearchCellKind.DateTime, Assert.Single(layout.Columns).Kind);
        var value = Assert.IsType<DateTime>(rows[0][0]);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), value);
    }

    [Fact]
    public void A_seven_digit_fraction_is_kept_to_the_tick()
    {
        var (_, rows) = SearchValues.FirstPage([new SearchColumn("t", "datetime")], [[Json("\"2024-01-15T10:00:00.1234567Z\"")]]);

        Assert.Equal(new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567), rows[0][0]);
    }

    // ---- First page: a misfit makes the whole column text ----

    public static TheoryData<string, string, string, string> FirstPageMisfits() => new()
    {
        // type, a value that fits, a value that does not, how the misfit reads as text
        { "integer", "1", "[1,2]", "[1,2]" },
        { "long", "1", "9223372036854775808", "9223372036854775808" },
        { "long", "1", "1.5", "1.5" },
        { "long", "1", "\"12\"", "12" },
        { "unsigned_long", "1", "1.5", "1.5" },
        { "unsigned_long", "1", "1e3", "1e3" },
        { "unsigned_long", "1", "123456789012345678901234567890", "123456789012345678901234567890" },
        { "double", "1.5", "\"NaN\"", "NaN" },
        { "double", "1.5", "1e400", "1e400" },
        { "boolean", "true", "\"true\"", "true" },
        { "boolean", "true", "1", "1" },
        { "keyword", "\"a\"", "[\"a\",\"b\"]", "[\"a\",\"b\"]" },
        { "keyword", "\"a\"", "42", "42" },
        { "binary", "\"AQ==\"", "\"not base64!\"", "not base64!" },
        { "binary", "\"AQ==\"", "{\"x\":1}", "{\"x\":1}" },
        { "date", "\"2024-01-15T10:00:00Z\"", "\"yesterday\"", "yesterday" },
        { "date", "\"2024-01-15T10:00:00Z\"", "\"2024-01-15T10:00:00.123456789Z\"", "2024-01-15T10:00:00.123456789Z" },
        { "date", "\"2024-01-15T10:00:00Z\"", "\"2024-01-15T10:00:00\"", "2024-01-15T10:00:00" },
        { "date", "\"2024-01-15T10:00:00Z\"", "1705312800000", "1705312800000" },
        { "timestamp", "\"2024-01-15 10:00:00\"", "\"2024-02-30 10:00:00\"", "2024-02-30 10:00:00" },
        { "date", "\"2024-01-15\"", "\"2024-01-15T10:00:00Z\"", "2024-01-15T10:00:00Z" },
        { "time", "\"10:00:00\"", "\"10:00:00Z\"", "10:00:00Z" },
        { "time", "\"10:00:00\"", "\"25:00:00\"", "25:00:00" },
    };

    [Theory]
    [MemberData(nameof(FirstPageMisfits))]
    public void A_first_page_misfit_makes_the_whole_column_text(string type, string fits, string misfit, string asText)
    {
        var (layout, rows) = SearchValues.FirstPage(
            [new SearchColumn("c", type), new SearchColumn("other", "long")],
            [[Json(fits), Json("1")], [Json(misfit), Json("2")], [Json("null"), Json("3")]]);

        var column = layout.Columns[0];
        Assert.Equal(SearchCellKind.Json, column.Kind);
        Assert.Equal(typeof(string), column.Descriptor.ClrType);
        Assert.Equal(type, column.Descriptor.DataTypeName);
        Assert.Equal(SearchValues.AsText(Json(fits)), rows[0][0]);
        Assert.Equal(asText, rows[1][0]);
        Assert.Null(rows[2][0]);

        // The neighbouring column keeps its own type.
        Assert.Equal(typeof(long), layout.Columns[1].Descriptor.ClrType);
        Assert.Equal([1L, 2L, 3L], rows.Select(r => r[1]));
    }

    [Fact]
    public void A_text_column_renders_every_value_by_the_everything_else_rule()
    {
        var (_, rows) = SearchValues.FirstPage(
            [new SearchColumn("v", "keyword")],
            [[Json("\"plain\"")], [Json("12.50")], [Json("false")], [Json("""{ "a" : [ 1 , "éé" ] }""")]]);

        Assert.Equal(["plain", "12.50", "false", """{"a":[1,"éé"]}"""], rows.Select(r => r[0]));
    }

    [Fact]
    public void A_date_column_whose_every_value_is_a_date_is_a_date_only()
    {
        var (layout, rows) = SearchValues.FirstPage(
            [new SearchColumn("d", "date")],
            [[Json("\"2024-01-15\"")], [Json("null")], [Json("\"1999-12-31\"")]]);

        Assert.Equal(typeof(DateOnly), Assert.Single(layout.Columns).Descriptor.ClrType);
        Assert.Equal([new DateOnly(2024, 1, 15), null, new DateOnly(1999, 12, 31)], rows.Select(r => r[0]));
    }

    [Fact]
    public void A_decimal_is_read_from_the_json_text_not_through_a_double()
    {
        var (_, rows) = SearchValues.FirstPage([new SearchColumn("u", "unsigned_long")], [[Json("9007199254740993")]]);

        // 2^53 + 1 is not a double; through one it would read 9007199254740992.
        Assert.Equal(9007199254740993m, rows[0][0]);
    }

    // ---- Later page: a misfit fails the page ----

    [Fact]
    public void A_later_page_converts_against_the_carried_layout()
    {
        var layout = new SearchLayout(
        [
            new SearchLayoutColumn("n", "long", SearchCellKind.Integer),
            new SearchLayoutColumn("d", "datetime", SearchCellKind.DateTime),
            new SearchLayoutColumn("j", "object", SearchCellKind.Json),
        ]);

        var rows = SearchValues.LaterPage(layout, [[Json("7"), Json("\"2024-01-15T10:00:00+01:00\""), Json("[1]")]]);

        Assert.Equal(7L, rows[0][0]);
        Assert.Equal(new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc), rows[0][1]);
        Assert.Equal("[1]", rows[0][2]);
    }

    [Theory]
    [InlineData("long", nameof(SearchCellKind.Integer), "[1,2]")]
    [InlineData("long", nameof(SearchCellKind.Integer), "\"7\"")]
    [InlineData("unsigned_long", nameof(SearchCellKind.UnsignedLong), "1.25")]
    [InlineData("double", nameof(SearchCellKind.Float), "true")]
    [InlineData("keyword", nameof(SearchCellKind.Text), "{\"k\":1}")]
    [InlineData("date", nameof(SearchCellKind.Date), "\"2024-01-15T10:00:00Z\"")]
    [InlineData("datetime", nameof(SearchCellKind.DateTime), "\"2024-01-15\"")]
    [InlineData("boolean", nameof(SearchCellKind.Boolean), "\"yes\"")]
    public void A_later_page_misfit_names_the_column_and_changes_nothing(string type, string kindName, string json)
    {
        var layout = new SearchLayout(
            [new SearchLayoutColumn("fine", "long", SearchCellKind.Integer), new SearchLayoutColumn("user.age", type, Enum.Parse<SearchCellKind>(kindName))]);

        var error = Assert.Throws<SearchValueMisfitException>(
            () => SearchValues.LaterPage(layout, [[Json("1"), Json("null")], [Json("3"), Json(json)]]));

        Assert.Equal("user.age", error.Column);
        Assert.Equal(type, error.TypeName);
        Assert.Equal(json, error.Excerpt);
        Assert.Contains("\"user.age\"", error.Message);
        Assert.Contains($"({type})", error.Message);
        Assert.Contains(json, error.Message);
    }

    [Fact]
    public void A_misfit_excerpt_is_bounded()
    {
        var layout = new SearchLayout([new SearchLayoutColumn("n", "long", SearchCellKind.Integer)]);
        var huge = "\"" + new string('x', 1_000) + "\"";

        var error = Assert.Throws<SearchValueMisfitException>(() => SearchValues.LaterPage(layout, [[Json(huge)]]));

        Assert.Equal(SearchValues.ExcerptLength + 1, error.Excerpt.Length);
        Assert.StartsWith("\"xxx", error.Excerpt);
    }

    [Fact]
    public void A_later_page_takes_null_in_any_column()
    {
        var layout = new SearchLayout(
            [new SearchLayoutColumn("n", "long", SearchCellKind.Integer), new SearchLayoutColumn("b", "binary", SearchCellKind.Binary)]);

        Assert.Equal([null, null], SearchValues.LaterPage(layout, [[Json("null"), Json("null")]])[0]);
    }

    [Fact]
    public void A_row_of_the_wrong_width_is_refused()
        => Assert.Throws<ArgumentException>(() => SearchValues.LaterPage(
            new SearchLayout([new SearchLayoutColumn("n", "long", SearchCellKind.Integer)]), [[Json("1"), Json("2")]]));

    // ---- Helpers ----

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
