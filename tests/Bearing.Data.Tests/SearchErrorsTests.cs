using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using Bearing.Core.Data;
using Bearing.Data.Aws;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What a failed Elasticsearch or OpenSearch request becomes: its verdict on the neutral scale, the sentence
/// the user reads, the position a parse error points at, and what is scrubbed out of it. Pure — the bodies are
/// the servers' documented envelopes, so no server is involved.
/// </summary>
public class SearchErrorsTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- The envelope ----------------------------------------------------------------------------------

    [Fact]
    public void An_elasticsearch_envelope_gives_its_reason_type_and_the_position_of_line_and_column()
    {
        const string statement = "SELECT foo FROM bar";
        var body = Utf8("""
            {"error":{"root_cause":[{"type":"verification_exception","reason":"Found 1 problem\nline 1:8: Unknown column [foo]"}],
             "type":"verification_exception","reason":"Found 1 problem\nline 1:8: Unknown column [foo]"},"status":400}
            """);

        var error = SearchErrors.ToQueryError(400, body, "SQL", statement);

        Assert.Equal("SQL: Found 1 problem\nline 1:8: Unknown column [foo]", error.Message);
        Assert.Equal("verification_exception", error.SqlState);
        Assert.Equal(8, error.Position);
        Assert.Equal('f', statement[error.Position!.Value - 1]);
        Assert.Equal(DbErrorKind.SyntaxOrShape, SearchErrors.ClassifyState(error.SqlState));
    }

    [Fact]
    public void An_opensearch_envelope_joins_reason_and_details()
    {
        var body = Utf8("""
            {"error":{"reason":"Invalid Query","details":"[x] is not a valid term at this part of the query: 'source=x | whe' <-- HERE.","type":"SyntaxCheckException"},"status":400}
            """);

        var error = SearchErrors.ToQueryError(400, body, "PPL", "source=x | whe");

        Assert.Equal(
            "PPL: Invalid Query: [x] is not a valid term at this part of the query: 'source=x | whe' <-- HERE.",
            error.Message);
        Assert.Equal("SyntaxCheckException", error.SqlState);
        Assert.Null(error.Position);
        Assert.Equal(DbErrorKind.SyntaxOrShape, SearchErrors.ClassifyState(error.SqlState));
    }

    /// <summary>Decision D4, on the body Elasticsearch 9.5.6 sent for a closed cursor: the reason is generic, the
    /// root cause says what failed — so it follows the reason in parentheses.</summary>
    [Fact]
    public void A_root_cause_that_says_more_follows_the_reason_in_parentheses()
    {
        var body = Utf8("""
            {"error":{"root_cause":[{"type":"search_context_missing_exception","reason":"No search context found for id [42]"}],
             "type":"search_phase_execution_exception","reason":"all shards failed","phase":"query"},"status":404}
            """);

        var error = SearchErrors.ToQueryError(404, body, "SQL", "SELECT 1");

        Assert.Equal("SQL: all shards failed (No search context found for id [42])", error.Message);
        Assert.Equal("search_phase_execution_exception", error.SqlState);
    }

    [Theory]
    [InlineData("""{"error":{"type":"x","reason":"all shards failed"},"status":500}""")]
    [InlineData("""{"error":{"root_cause":[],"type":"x","reason":"all shards failed"},"status":500}""")]
    [InlineData("""{"error":{"root_cause":[{"type":"y","reason":"   "}],"type":"x","reason":"all shards failed"},"status":500}""")]
    [InlineData("""{"error":{"root_cause":[{"type":"y"}],"type":"x","reason":"all shards failed"},"status":500}""")]
    [InlineData("""{"error":{"root_cause":[{"type":"y","reason":"all shards failed"}],"type":"x","reason":"all shards failed"},"status":500}""")]
    [InlineData("""{"error":{"root_cause":["all shards failed"],"type":"x","reason":"all shards failed"},"status":500}""")]
    public void An_absent_blank_or_identical_root_cause_adds_nothing(string body)
        => Assert.Equal("all shards failed", SearchErrors.ToQueryError(500, Utf8(body), null, null).Message);

    [Fact]
    public void Only_the_first_root_cause_is_shown_and_it_is_bounded_and_scrubbed()
    {
        var longCause = "Cause with api_key=abc123XYZabc123 " + new string('z', 1_000);
        var body = Utf8($$"""
            {"error":{"root_cause":[{"reason":"{{longCause}}"},{"reason":"the second one"}],
             "type":"x","reason":"all shards failed"},"status":500}
            """);

        var message = SearchErrors.ToQueryError(500, body, null, null).Message;

        Assert.StartsWith("all shards failed (Cause with ", message);
        Assert.EndsWith("…)", message);
        Assert.True(message.Length < "all shards failed (".Length + SearchErrors.ExcerptLength + 5);
        Assert.DoesNotContain("the second one", message);
        Assert.DoesNotContain("abc123XYZabc123", message);
    }

    [Fact]
    public void A_root_cause_does_not_move_the_position_the_reason_gives()
    {
        var body = Utf8("""
            {"error":{"root_cause":[{"type":"parsing_exception","reason":"line 1:3: mismatched input"}],
             "type":"parsing_exception","reason":"line 1:8: extraneous input"},"status":400}
            """);

        var error = SearchErrors.ToQueryError(400, body, "SQL", "SELECT foo FROM bar");

        Assert.Equal("SQL: line 1:8: extraneous input (line 1:3: mismatched input)", error.Message);
        Assert.Equal(8, error.Position);
    }

    [Fact]
    public void A_string_error_is_the_reason()
    {
        var body = Utf8("""{"error":"Incorrect HTTP method for uri [/_sql] and method [GET], allowed: [POST]","status":405}""");

        var error = SearchErrors.ToQueryError(405, body, null, null);

        Assert.Equal("Incorrect HTTP method for uri [/_sql] and method [GET], allowed: [POST]", error.Message);
        Assert.Equal("HTTP 405", error.SqlState);
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.ClassifyState(error.SqlState));
    }

    [Fact]
    public void A_body_that_is_not_json_is_quoted_as_a_bounded_excerpt()
    {
        var html = "<html>\n  <body>502   Bad Gateway</body>\n</html>" + new string('x', 2000);

        var error = SearchErrors.ToQueryError(502, Utf8(html), "SQL", "select 1");

        Assert.StartsWith("SQL: <html> <body>502 Bad Gateway</body> </html>xxx", error.Message);
        Assert.EndsWith("…", error.Message);
        Assert.True(error.Message.Length <= "SQL: ".Length + SearchErrors.ExcerptLength + 1, error.Message);
        Assert.Equal("HTTP 502", error.SqlState);
    }

    [Fact]
    public void An_empty_body_still_says_something()
    {
        var error = SearchErrors.ToQueryError(503, [], null, null);

        Assert.Equal("The server answered HTTP 503 with no explanation.", error.Message);
    }

    [Fact]
    public void A_400_pointing_into_the_statement_is_its_shape_even_with_a_type_not_listed()
    {
        var body = Utf8("""{"error":{"type":"illegal_argument_exception","reason":"line 1:5: no viable alternative"},"status":400}""");

        var error = SearchErrors.ToQueryError(400, body, "ES|QL", "FROM x | y");

        Assert.Equal(SearchErrors.SyntaxCode, error.SqlState);
        Assert.Equal(DbErrorKind.SyntaxOrShape, SearchErrors.ClassifyState(error.SqlState));
        Assert.Equal(5, error.Position);
    }

    [Fact]
    public void A_400_without_a_position_and_an_unlisted_type_stays_unknown()
    {
        var body = Utf8("""{"error":{"type":"illegal_argument_exception","reason":"index [x] is closed"},"status":400}""");

        var error = SearchErrors.ToQueryError(400, body, "SQL", "select * from x");

        Assert.Equal("illegal_argument_exception", error.SqlState);
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.ClassifyState(error.SqlState));
    }

    [Fact]
    public void A_403_is_a_privilege_and_a_401_is_the_sign_in()
    {
        var forbidden = SearchErrors.ToQueryError(
            403,
            Utf8("""{"error":{"type":"security_exception","reason":"action [indices:data/read/sql] is unauthorized for user [reader]"},"status":403}"""),
            "SQL", "select 1");
        var unauthorized = SearchErrors.ToQueryError(401, Utf8("""{"error":{"type":"security_exception"}}"""), "SQL", "select 1");

        Assert.Equal(DbErrorKind.Unknown, SearchErrors.ClassifyState(forbidden.SqlState));
        Assert.Equal(DbErrorKind.Authentication, SearchErrors.ClassifyState(unauthorized.SqlState));
    }

    // ---- Classification --------------------------------------------------------------------------------

    [Theory]
    [InlineData(SearchErrors.SignInCode, DbErrorKind.Authentication)]
    [InlineData(SearchErrors.TimeoutCode, DbErrorKind.Canceled)]
    [InlineData(SearchErrors.CanceledCode, DbErrorKind.Canceled)]
    [InlineData("parsing_exception", DbErrorKind.SyntaxOrShape)]
    [InlineData("verification_exception", DbErrorKind.SyntaxOrShape)]
    [InlineData("SyntaxCheckException", DbErrorKind.SyntaxOrShape)]
    [InlineData("SemanticCheckException", DbErrorKind.SyntaxOrShape)]
    [InlineData(SearchErrors.SyntaxCode, DbErrorKind.SyntaxOrShape)]
    [InlineData(SearchErrors.RedirectCode, DbErrorKind.Unknown)]
    [InlineData("security_exception", DbErrorKind.Unknown)]
    [InlineData("HTTP 403", DbErrorKind.Unknown)]
    [InlineData(null, DbErrorKind.Unknown)]
    public void Codes_are_placed_on_the_neutral_scale(string? code, DbErrorKind expected)
        => Assert.Equal(expected, SearchErrors.ClassifyState(code));

    public static TheoryData<Exception, DbErrorKind> Thrown() => new()
    {
        { new SearchSignInException(SearchEngine.Elasticsearch, SearchAuthMethod.Basic, "elastic"), DbErrorKind.Authentication },
        { new InvalidOperationException("wrapped", new SearchSignInException(SearchEngine.OpenSearch, SearchAuthMethod.ApiKey, null)), DbErrorKind.Authentication },
        { new OperationCanceledException(), DbErrorKind.Canceled },
        { new AggregateException(new TaskCanceledException()), DbErrorKind.Canceled },
        { new SearchServerException(403, new QueryError("no", "security_exception", null)), DbErrorKind.Unknown },
        { new SearchServerException(400, new QueryError("bad", "parsing_exception", 3)), DbErrorKind.SyntaxOrShape },
        { new SearchTransportException("refused", HttpRequestError.ConnectionError, null), DbErrorKind.Unknown },
        { new SearchConfigurationException(SearchErrors.HostOnlySentence), DbErrorKind.Unknown },
        { new SearchResponseTooLargeException(1024), DbErrorKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(Thrown))]
    public void Thrown_failures_are_classified_through_the_chain(Exception exception, DbErrorKind expected)
        => Assert.Equal(expected, SearchErrors.Classify(exception));

    // ---- Position ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT a FROM b", 1, 1, 1)]
    [InlineData("SELECT a FROM b", 1, 15, 15)]
    [InlineData("SELECT a FROM b", 1, 16, 16)] // one past the end: "expected more"
    [InlineData("SELECT a FROM b", 1, 17, null)]
    [InlineData("SELECT a\nFROM b", 2, 6, 15)]
    [InlineData("SELECT a\r\nFROM b", 2, 6, 16)]
    [InlineData("SELECT a\rFROM b", 2, 1, 10)]
    [InlineData("SELECT a\nFROM b", 3, 1, null)]
    [InlineData("SELECT a", 0, 1, null)]
    [InlineData("SELECT a", 1, 0, null)]
    public void Line_and_column_become_a_one_based_offset(string text, int line, int column, int? expected)
        => Assert.Equal(expected, SearchErrors.PositionOf(text, line, column));

    [Fact]
    public void A_position_is_read_from_the_details_when_the_reason_has_none()
    {
        var body = Utf8("""{"error":{"type":"x","reason":"Invalid query","details":"line 2:3: mismatched input"},"status":400}""");

        var error = SearchErrors.ToQueryError(400, body, "SQL", "SELECT\n  *x FROM y");

        Assert.Equal(10, error.Position);
    }

    [Fact]
    public void No_statement_means_no_position()
    {
        var body = Utf8("""{"error":{"type":"parsing_exception","reason":"line 1:8: x"},"status":400}""");

        Assert.Null(SearchErrors.ToQueryError(400, body, "SQL", null).Position);
    }

    // ---- Sentences ---------------------------------------------------------------------------------------

    [Fact]
    public void The_timeout_sentence_names_the_connections_own_timeout_and_classifies_as_canceled()
    {
        var error = SearchErrors.ToQueryError(new TaskCanceledException(), SearchEngine.OpenSearch, 30, timedOut: true);

        Assert.Equal("Stopped: this connection's statement timeout (30 s) elapsed before OpenSearch answered.", error.Message);
        Assert.Equal(SearchErrors.TimeoutSentence(SearchEngine.OpenSearch, 30), error.Message);
        Assert.Equal(DbErrorKind.Canceled, SearchErrors.ClassifyState(error.SqlState));
    }

    [Fact]
    public void A_cancel_that_is_not_the_timeout_is_the_users_own()
    {
        var error = SearchErrors.ToQueryError(new OperationCanceledException(), SearchEngine.Elasticsearch, 30);

        Assert.Equal("The statement was canceled.", error.Message);
        Assert.Equal(SearchErrors.CanceledCode, error.SqlState);
        Assert.Equal(DbErrorKind.Canceled, SearchErrors.Classify(new OperationCanceledException()));
    }

    /// <summary>R2-3: the handler's connect timeout reaches the factory as a cancel nobody asked for, kept as the
    /// transport exception's inner exception. The transport exception is terminal: its own sentence, not
    /// "canceled", and not classified as a cancel.</summary>
    [Fact]
    public void A_transport_failure_holding_a_cancel_is_the_transport_sentence_not_a_cancel()
    {
        var timeout = new TaskCanceledException(
            "The operation was canceled.", new TimeoutException("A connection could not be established within the configured ConnectTimeout."));
        var failure = new SearchTransportException(
            "No connection could be made within 15 s. The operation was canceled: A connection could not be established within the configured ConnectTimeout.",
            null, timeout);

        var error = SearchErrors.ToQueryError(failure, SearchEngine.Elasticsearch, 30);

        Assert.Equal(DbErrorKind.Unknown, SearchErrors.Classify(failure));
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.Classify(new InvalidOperationException("wrapped", failure)));
        Assert.Equal(failure.Message, error.Message);
        Assert.StartsWith("No connection could be made within 15 s.", error.Message);
        Assert.NotEqual(SearchErrors.CanceledCode, error.SqlState);
        Assert.Null(error.SqlState);
    }

    [Theory]
    [InlineData(nameof(SearchAuthMethod.Basic), "elastic", "Elasticsearch refused the user name and password for user 'elastic' (HTTP 401).")]
    [InlineData(nameof(SearchAuthMethod.ApiKey), "elastic", "Elasticsearch refused the API key (HTTP 401).")]
    [InlineData(nameof(SearchAuthMethod.None), "", "Elasticsearch asks for sign-in (HTTP 401)")]
    public void The_sign_in_sentence_names_the_method_that_was_refused(string methodName, string user, string start)
    {
        // By name: the enum is internal, and a public test method cannot take it.
        var method = Enum.Parse<SearchAuthMethod>(methodName);
        var sentence = SearchErrors.SignInSentence(SearchEngine.Elasticsearch, method, user);

        Assert.StartsWith(start, sentence);
        if (method == SearchAuthMethod.ApiKey)
        {
            Assert.DoesNotContain("elastic'", sentence);
        }
    }

    [Fact]
    public void The_aws_sign_in_sentence_names_the_profile_and_the_command()
    {
        var sentence = SearchErrors.SignInSentence(SearchEngine.OpenSearch, SearchAuthMethod.AwsSigV4, "dev");

        Assert.Contains("aws sso login --profile dev", sentence);
    }

    [Theory]
    [InlineData(nameof(AwsSignInProblem.ProfileNotFound), "AWS profile 'dev' was not found.")]
    [InlineData(nameof(AwsSignInProblem.ProfileHasNoCredentials), "AWS profile 'dev' was found, but")]
    [InlineData(nameof(AwsSignInProblem.CouldNotSignIn), "Could not get AWS credentials from profile 'dev' to sign the requests to OpenSearch")]
    public void The_aws_credential_sentences_name_the_profile_and_the_command(string problemName, string start)
    {
        var problem = Enum.Parse<AwsSignInProblem>(problemName);

        var sentence = SearchErrors.AwsSignInSentence(SearchEngine.OpenSearch, "  dev ", problem);

        Assert.StartsWith(start, sentence);
        Assert.Contains("aws sso login --profile dev", sentence);
        Assert.Equal(sentence, new SearchSignInException(SearchEngine.OpenSearch, "dev", problem).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_blank_profile_says_the_default_chain_produced_no_credentials(string? profile)
    {
        var sentence = SearchErrors.AwsSignInSentence(SearchEngine.OpenSearch, profile, AwsSignInProblem.CouldNotSignIn);

        Assert.StartsWith("The AWS SDK's default credential chain produced no credentials", sentence);
        Assert.Contains("aws sso login", sentence);
        Assert.DoesNotContain("--profile", sentence);
    }

    [Fact]
    public void An_aws_credential_failure_is_an_authentication_failure_with_its_own_sentence_in_the_grid()
    {
        var failure = new SearchSignInException(SearchEngine.OpenSearch, "dev", AwsSignInProblem.ProfileNotFound);

        Assert.Equal(SearchAuthMethod.AwsSigV4, failure.Method);
        Assert.Equal(AwsSignInProblem.ProfileNotFound, failure.Problem);
        Assert.Null(failure.InnerException);
        Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(new AggregateException(failure)));
        var error = SearchErrors.ToQueryError(failure, SearchEngine.OpenSearch, 30);
        Assert.Equal(failure.Message, error.Message);
        Assert.Equal(SearchErrors.SignInCode, error.SqlState);
        Assert.Null(new SearchSignInException(SearchEngine.OpenSearch, SearchAuthMethod.AwsSigV4, "dev").Problem);
    }

    [Fact]
    public void The_region_sentences_name_the_field()
    {
        Assert.Contains("AWS region field", SearchErrors.AwsRegionMissingSentence);
        Assert.Contains("AWS region field", SearchErrors.AwsRegionInvalidSentence);
    }

    [Fact]
    public void A_thrown_server_error_keeps_its_query_error_and_other_text_is_scrubbed()
    {
        var server = new QueryError("SQL: bad", "parsing_exception", 4);

        Assert.Same(server, SearchErrors.ToQueryError(new SearchServerException(400, server), SearchEngine.Elasticsearch, 0));

        var leaky = SearchErrors.ToQueryError(
            new InvalidOperationException("failed with Authorization: Basic ZWxhc3RpYzpzM2NyZXQ="), SearchEngine.Elasticsearch, 0);
        Assert.DoesNotContain("ZWxhc3RpYzpzM2NyZXQ=", leaky.Message);
    }

    [Fact]
    public void A_transport_failure_reads_as_its_meaningful_messages()
    {
        var failure = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        Assert.Equal(
            "The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot.",
            SearchErrors.DescribeTransport(failure));
    }

    // ---- Scrub -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("request failed: Authorization: Basic ZWxhc3RpYzpzM2NyZXQ=", "ZWxhc3RpYzpzM2NyZXQ=")]
    [InlineData("header was [authorization=ApiKey VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky]", "VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky")]
    [InlineData("sent ApiKey VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky to the server", "VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky")]
    [InlineData("token Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl rejected", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl")]
    [InlineData("basic dXNlcjpodW50ZXIy was refused", "dXNlcjpodW50ZXIy")]
    [InlineData("The security token AKIAFAKEKEYID1234567 is invalid", "AKIAFAKEKEYID1234567")]
    [InlineData("aws_secret_access_key=wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET in profile", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET")]
    [InlineData("x-amz-security-token: IQoJb3JpZ2luX2VjFAKESESSIONTOKEN", "IQoJb3JpZ2luX2VjFAKESESSIONTOKEN")]
    [InlineData("could not reach https://elastic:s3cretpass@search.example.com:9200/", "s3cretpass")]
    [InlineData("api_key=abcdef0123456789 was rejected", "abcdef0123456789")]
    [InlineData("connection string Password=hunter22;Host=x", "hunter22")]
    [InlineData("Credential=AKIDEXAMPLE/20150830/us-east-1/es/aws4_request, SignedHeaders=host;x-amz-date", "AKIDEXAMPLE")]
    [InlineData("SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
        "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31")]
    [InlineData("GET /?X-Amz-Credential=ASIAFAKEKEYID12345678%2F20250102&X-Amz-Signature=abc123def456 failed", "ASIAFAKEKEYID12345678")]
    [InlineData("GET /?X-Amz-Credential=ASIAFAKEKEYID12345678%2F20250102&X-Amz-Signature=abc123def456 failed", "abc123def456")]
    public void Scrub_removes_every_credential_form(string message, string secret)
    {
        var scrubbed = SearchErrors.Scrub(message);

        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("***", scrubbed);
    }

    [Fact]
    public void Scrub_leaves_an_ordinary_message_alone()
    {
        const string message = "line 1:8: Unknown column [foo]; Basic auth is enabled on host search.example.com:9200";

        Assert.Equal(message, SearchErrors.Scrub(message));
    }

    [Fact]
    public void A_server_message_echoing_a_key_is_scrubbed_before_it_is_shown()
    {
        var body = Utf8("""{"error":{"type":"security_exception","reason":"unable to authenticate with provided credentials ApiKey VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky"},"status":400}""");

        var error = SearchErrors.ToQueryError(400, body, "SQL", "select 1");

        Assert.DoesNotContain("VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWky", error.Message);
    }
}
