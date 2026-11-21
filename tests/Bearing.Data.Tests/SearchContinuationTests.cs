using System.Text;
using Bearing.Data.DynamoDb;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <see cref="SearchContinuation"/>: the opaque token a SQL result with a cursor carries. It round-trips exactly,
/// and <see cref="SearchContinuation.Decode"/> refuses anything this class did not write — a DynamoDB token,
/// garbage, an empty string, a token cut short or edited (SC9).
/// </summary>
public class SearchContinuationTests
{
    private static readonly SearchLayout Layout = new(
    [
        new SearchLayoutColumn("author", "text", SearchCellKind.Text),
        new SearchLayoutColumn("page_count", "short", SearchCellKind.Integer),
        new SearchLayoutColumn("release_date", "datetime", SearchCellKind.DateTime),
        new SearchLayoutColumn("tags", "keyword", SearchCellKind.Json),
    ]);

    [Theory]
    [InlineData(nameof(SearchEngine.Elasticsearch), "sDXF1ZXJ5QW5kRmV0Y2gBAAAAAAAAAAEWWWdrRlVfSS1TbDYtcW9lc1FJNmlYdw==", 0)]
    [InlineData(nameof(SearchEngine.OpenSearch), "d:eyJhIjp7fSwicyI6IkRYRjFaWEo1UVc1a1JtVjBZMmdC+/=", 3)]
    public void A_token_round_trips_exactly(string engineName, string cursor, int index)
    {
        var original = new SearchContinuation(Enum.Parse<SearchEngine>(engineName), SearchLanguage.Sql, cursor, Layout, index);

        var token = original.Encode();
        var decoded = SearchContinuation.Decode(token);

        Assert.StartsWith(SearchContinuation.Prefix, token);
        Assert.Equal(Enum.Parse<SearchEngine>(engineName), decoded.Engine);
        Assert.Equal(SearchLanguage.Sql, decoded.Language);
        Assert.Equal(cursor, decoded.Cursor);
        Assert.Equal(index, decoded.StatementIndex);
        Assert.Equal(Layout.Columns, decoded.Layout.Columns);
        Assert.Same(SearchProtocol.EndpointFor(Enum.Parse<SearchEngine>(engineName), SearchLanguage.Sql), decoded.Endpoint);
    }

    [Fact]
    public void The_token_holds_the_column_types_it_was_given()
    {
        var decoded = SearchContinuation.Decode(new SearchContinuation(
            SearchEngine.Elasticsearch, SearchLanguage.Sql, "c", Layout, 0).Encode());

        Assert.Equal(
            [typeof(string), typeof(long), typeof(DateTime), typeof(string)],
            decoded.Layout.Descriptors().Select(d => d.ClrType));
    }

    [Fact]
    public void The_token_is_url_safe_and_opaque()
    {
        var token = new SearchContinuation(SearchEngine.OpenSearch, SearchLanguage.Sql, "d:the-cursor", Layout, 0).Encode();

        Assert.DoesNotContain("the-cursor", token);
        Assert.Matches("^[A-Za-z0-9._-]+$", token);
    }

    [Fact]
    public void A_dynamodb_token_is_refused()
    {
        var dynamo = new DynamoDbContinuation(
            "select * from \"T\"",
            "next-token",
            new DynamoDbColumnLayout([new DynamoDbColumn("pk", DynamoDbColumnType.Text)], HasOverflow: true),
            ["pk"],
            0).Encode();

        Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(dynamo));
        Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(SearchContinuation.Prefix + dynamo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("search1.")]
    [InlineData("search1.!!!")]
    [InlineData("search1.e30")]
    [InlineData("SEARCH1.e30")]
    [InlineData("search2.e30")]
    public void Garbage_and_an_empty_string_are_refused(string text)
    {
        var error = Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(text));

        Assert.Equal("The continuation token is not valid.", error.Message);
    }

    [Fact]
    public void A_null_token_is_refused()
        => Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(null!));

    [Fact]
    public void A_truncated_token_is_refused()
    {
        var token = new SearchContinuation(SearchEngine.Elasticsearch, SearchLanguage.Sql, "cursor", Layout, 1).Encode();

        for (var length = SearchContinuation.Prefix.Length; length < token.Length; length += 3)
        {
            var cut = token[..length];
            Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(cut));
        }
    }

    [Theory]
    [InlineData("""{"v":2,"e":"Elasticsearch","l":"Sql","u":"c","c":[],"i":0}""")]
    [InlineData("""{"e":"Elasticsearch","l":"Sql","u":"c","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Solr","l":"Sql","u":"c","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"0","l":"Sql","u":"c","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Piped","u":"c","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"sql","u":"c","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","c":[],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[],"i":-1}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[]}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[null],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[{"n":"a","t":"long"}],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[{"n":"a","t":"long","k":"Decimal"}],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":"c","c":[{"t":"long","k":"Integer"}],"i":0}""")]
    [InlineData("""{"v":1,"e":"Elasticsearch","l":"Sql","u":5,"c":[],"i":0}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""null""")]
    public void A_token_with_a_missing_wrong_or_foreign_field_is_refused(string json)
        => Assert.Throws<SearchContinuationException>(() => SearchContinuation.Decode(Token(json)));

    [Fact]
    public void A_well_formed_hand_made_token_is_read_the_same_way()
    {
        // The refusal theory above is only meaningful if this shape is otherwise accepted.
        var decoded = SearchContinuation.Decode(Token(
            """{"v":1,"e":"OpenSearch","l":"Sql","u":"c","c":[{"n":"a","t":"long","k":"Integer"}],"i":2}"""));

        Assert.Equal(SearchEngine.OpenSearch, decoded.Engine);
        Assert.Equal(new SearchLayoutColumn("a", "long", SearchCellKind.Integer), Assert.Single(decoded.Layout.Columns));
        Assert.Equal(2, decoded.StatementIndex);
    }

    [Theory]
    [InlineData(nameof(SearchLanguage.Piped), "c", 0)]
    [InlineData(nameof(SearchLanguage.Sql), "", 0)]
    [InlineData(nameof(SearchLanguage.Sql), "c", -1)]
    public void A_token_decode_would_refuse_is_never_written(string languageName, string cursor, int index)
        => Assert.Throws<ArgumentException>(
            () => new SearchContinuation(SearchEngine.Elasticsearch, Enum.Parse<SearchLanguage>(languageName), cursor, Layout, index).Encode());

    private static string Token(string json)
        => SearchContinuation.Prefix
           + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
