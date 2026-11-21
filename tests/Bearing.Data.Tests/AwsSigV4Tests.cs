using System.Security.Cryptography;
using System.Text;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The SigV4 signer against AWS's own published test vectors (the <c>aws-c-auth</c> signing test suite, v4 —
/// copied in as literals, with AWS's example credentials, which are not secrets), and then Bearing's own header
/// set against a signature computed independently here: a plain HMAC chain written in this file, never a call
/// back into the signer.
/// </summary>
public class AwsSigV4Tests
{
    // AWS's example credentials and clock, the same in every vector of the suite.
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string VectorRegion = "us-east-1";
    private const string VectorService = "service";
    private static readonly DateTime VectorTime = new(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);

    private const string VectorDate = "20150830T123600Z";
    private const string VectorHost = "example.amazonaws.com";

    private sealed record Vector(
        string Name,
        string Method,
        string Path,
        string Query,
        (string Name, string Value)[] Headers,
        string? Body,
        string CanonicalRequest,
        string StringToSign,
        string Signature);

    /// <summary>The published cases. Headers are each vector's own set as <c>header-canonical-request.txt</c>
    /// signs it (with <c>sign_body: false</c> there is no <c>x-amz-content-sha256</c> header, only the payload
    /// hash line).</summary>
    private static readonly Vector[] Vectors =
    [
        new("get-vanilla", "GET", "/", "",
            [("Host", VectorHost), ("X-Amz-Date", VectorDate)],
            null,
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63",
            "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31"),
        new("post-vanilla", "POST", "/", "",
            [("Host", VectorHost), ("X-Amz-Date", VectorDate)],
            null,
            "POST\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "553f88c9e4d10fc9e109e2aeb65f030801b70c2f6468faca261d401ae622fc87",
            "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b"),
        new("post-x-www-form-urlencoded", "POST", "/", "",
            [
                ("Content-Type", "application/x-www-form-urlencoded"),
                ("Host", VectorHost),
                ("Content-Length", "13"),
                ("X-Amz-Date", VectorDate),
                ("x-amz-content-sha256", "9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e"),
            ],
            "Param1=value1",
            "POST\n/\n\ncontent-length:13\ncontent-type:application/x-www-form-urlencoded\nhost:example.amazonaws.com\n"
            + "x-amz-content-sha256:9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e\n"
            + "x-amz-date:20150830T123600Z\n\ncontent-length;content-type;host;x-amz-content-sha256;x-amz-date\n"
            + "9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "b1edd1d03544c25390e32085d55b57acc9a3961bb59415ff86c45c3d89d16cfb",
            "d3875051da38690788ef43de4db0d8f280229d82040bfac253562e56c3f20e0b"),
        new("get-vanilla-query-order-key-case", "GET", "/", "Param2=value2&Param1=value1",
            [("Host", VectorHost), ("X-Amz-Date", VectorDate)],
            null,
            "GET\n/\nParam1=value1&Param2=value2\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\n"
            + "host;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "816cd5b414d056048ba4f7c5386d6e0533120fb1fcfa93762cf0fc39e2cf19e0",
            "b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500"),
        new("get-utf8", "GET", "/ሴ", "",
            [("Host", VectorHost), ("X-Amz-Date", VectorDate)],
            null,
            "GET\n/%E1%88%B4\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "2a0a97d02205e45ce2e994789806b19270cfbbb0921b278ccf58f5249ac42102",
            "8318018e0b0f223aa2bbf98705b62bb787dc9c0e678f255a891fd03141be5d85"),
        new("post-header-key-sort", "POST", "/", "",
            [("Host", VectorHost), ("My-Header1", "value1"), ("X-Amz-Date", VectorDate)],
            null,
            "POST\n/\n\nhost:example.amazonaws.com\nmy-header1:value1\nx-amz-date:20150830T123600Z\n\n"
            + "host;my-header1;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "9368318c2967cf6de74404b30c65a91e8f6253e0a8659d6d5319f1a812f87d65",
            "c5410059b04c1ee005303aed430f6e6645f61f4dc9e1461ec8f8916fdf18852c"),
        // The session token is added after signing (omit_session_token): the signature is post-vanilla's.
        new("post-sts-header-after", "POST", "/", "",
            [("Host", VectorHost), ("X-Amz-Date", VectorDate)],
            null,
            "POST\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
            + "553f88c9e4d10fc9e109e2aeb65f030801b70c2f6468faca261d401ae622fc87",
            "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b"),
    ];

    public static TheoryData<string> VectorNames()
    {
        var names = new TheoryData<string>();
        foreach (var vector in Vectors)
        {
            names.Add(vector.Name);
        }

        return names;
    }

    // ---- AWS's published vectors -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void The_published_vectors_give_the_published_canonical_request_string_to_sign_and_signature(string name)
    {
        var vector = Vectors.Single(v => v.Name == name);
        var payloadHash = AwsSigV4.PayloadHash(vector.Body is null ? [] : Encoding.UTF8.GetBytes(vector.Body));

        var steps = AwsSigV4.Compute(
            vector.Method,
            vector.Path,
            vector.Query,
            vector.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)),
            payloadHash,
            VectorRegion,
            VectorService,
            AccessKey,
            SecretKey,
            VectorTime);

        Assert.Equal(vector.CanonicalRequest, steps.CanonicalRequest);
        Assert.Equal(vector.StringToSign, steps.StringToSign);
        Assert.Equal(vector.Signature, steps.Signature);
        Assert.Equal(
            $"AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, "
            + $"SignedHeaders={steps.SignedHeaders}, Signature={vector.Signature}",
            steps.Authorization);
    }

    [Fact]
    public void The_payload_hash_of_the_form_vector_is_the_published_one()
        => Assert.Equal(
            "9095672bbd1f56dfc5b65f3e153adc8731a4a654192329106275f4c7b24d0b6e",
            AwsSigV4.PayloadHash("Param1=value1"u8));

    // ---- Canonical pieces --------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/_plugins/_sql", "/_plugins/_sql")]
    [InlineData("/*/_mapping", "/%2A/_mapping")]
    [InlineData("/a,b/_search", "/a%2Cb/_search")]
    [InlineData("/my%20index/_search", "/my%2520index/_search")] // an escape on the wire is encoded again
    [InlineData("/é", "/%C3%A9")]
    public void The_canonical_uri_encodes_every_byte_but_the_unreserved_and_the_slash(string path, string expected)
        => Assert.Equal(expected, AwsSigV4.CanonicalUri(path));

    [Theory]
    [InlineData("", "")]
    [InlineData("format=json&h=index", "format=json&h=index")]
    [InlineData("b=2&a=1&a=0", "a=0&a=1&b=2")]
    [InlineData("flag", "flag=")]
    [InlineData("q=a%20b&x=*", "q=a%20b&x=%2A")]
    [InlineData("h=index,docs.count", "h=index%2Cdocs.count")]
    public void The_canonical_query_is_decoded_encoded_strictly_and_sorted(string query, string expected)
        => Assert.Equal(expected, AwsSigV4.CanonicalQuery(query));

    [Fact]
    public void Canonical_headers_are_lower_cased_sorted_and_trimmed_with_runs_of_spaces_collapsed()
    {
        var (lines, signed) = AwsSigV4.CanonicalHeaders(
        [
            new("X-Amz-Date", VectorDate),
            new("Host", "  example.amazonaws.com "),
            new("My-Header", "a   b  c"),
        ]);

        Assert.Equal("host:example.amazonaws.com\nmy-header:a b c\nx-amz-date:20150830T123600Z\n", lines);
        Assert.Equal("host;my-header;x-amz-date", signed);
    }

    // ---- Bearing's own header set ------------------------------------------------------------------------

    [Fact]
    public void The_empty_payload_hash_is_the_well_known_constant()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AwsSigV4.EmptyPayloadHash);
        Assert.Equal(AwsSigV4.EmptyPayloadHash, AwsSigV4.PayloadHash([]));
    }

    [Fact]
    public void Bearings_signature_sends_and_signs_the_content_hash_and_the_session_token_and_matches_an_independent_hmac_chain()
    {
        const string host = "search-logs-abc123.eu-west-1.es.amazonaws.com";
        const string token = "IQoJb3JpZ2luX2VjEXAMPLESESSIONTOKEN+/=";
        var body = Encoding.UTF8.GetBytes("""{"query":"select * from logs","fetch_size":200}""");
        var now = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var signed = AwsSigV4.Sign(
            "POST", new Uri($"https://{host}/_plugins/_sql?format=jdbc"), host, body,
            "eu-west-1", "es", AccessKey, SecretKey, token, now);

        // What the request carries.
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        Assert.Equal("20250102T030405Z", signed.AmzDate);
        Assert.Equal(bodyHash, signed.ContentSha256);
        Assert.Equal(token, signed.SecurityToken);
        Assert.Equal("host;x-amz-content-sha256;x-amz-date;x-amz-security-token", signed.SignedHeaders);
        Assert.Equal(
            ["x-amz-date", "x-amz-content-sha256", "x-amz-security-token", "Authorization"],
            signed.All.Select(h => h.Name));

        // The same signature, worked out here from the specification alone.
        var canonical = string.Join('\n',
            "POST",
            "/_plugins/_sql",
            "format=jdbc",
            $"host:{host}\nx-amz-content-sha256:{bodyHash}\nx-amz-date:20250102T030405Z\nx-amz-security-token:{token}\n",
            "host;x-amz-content-sha256;x-amz-date;x-amz-security-token",
            bodyHash);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            "20250102T030405Z",
            "20250102/eu-west-1/es/aws4_request",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + SecretKey), "20250102");
        key = Hmac(key, "eu-west-1");
        key = Hmac(key, "es");
        key = Hmac(key, "aws4_request");
        var expected = Convert.ToHexStringLower(Hmac(key, stringToSign));

        Assert.Equal(
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/20250102/eu-west-1/es/aws4_request, "
            + $"SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token, Signature={expected}",
            signed.Authorization);
    }

    [Fact]
    public void Without_a_session_token_no_token_header_is_sent_or_signed_and_an_empty_body_signs_the_empty_hash()
    {
        var signed = AwsSigV4.Sign(
            "GET", new Uri("https://abc.us-east-1.aoss.amazonaws.com/"), "abc.us-east-1.aoss.amazonaws.com", [],
            "us-east-1", "aoss", AccessKey, SecretKey, sessionToken: "", VectorTime);

        Assert.Null(signed.SecurityToken);
        Assert.Equal("host;x-amz-content-sha256;x-amz-date", signed.SignedHeaders);
        Assert.Equal(AwsSigV4.EmptyPayloadHash, signed.ContentSha256);
        Assert.DoesNotContain(signed.All, h => h.Name == AwsSigV4.SecurityTokenHeader);
        Assert.Contains("/us-east-1/aoss/aws4_request", signed.Authorization);
    }

    [Fact]
    public void A_different_body_or_host_gives_a_different_signature()
    {
        Uri uri = new("https://h.eu-west-1.es.amazonaws.com/_plugins/_ppl");
        string Sig(string host, string body) => AwsSigV4.Sign(
            "POST", uri, host, Encoding.UTF8.GetBytes(body), "eu-west-1", "es", AccessKey, SecretKey, null, VectorTime)
            .Authorization;

        var reference = Sig("h.eu-west-1.es.amazonaws.com", "{}");
        Assert.NotEqual(reference, Sig("h.eu-west-1.es.amazonaws.com", "{ }"));
        Assert.NotEqual(reference, Sig("127.0.0.1:9200", "{}"));
    }

    [Fact]
    public void Nothing_carrying_the_token_or_the_signature_prints_them()
    {
        const string token = "IQoJb3JpZ2luX2VjEXAMPLESESSIONTOKEN";
        var signed = AwsSigV4.Sign(
            "GET", new Uri("https://h.eu-west-1.es.amazonaws.com/"), "h.eu-west-1.es.amazonaws.com", [],
            "eu-west-1", "es", AccessKey, SecretKey, token, VectorTime);
        var steps = AwsSigV4.Compute(
            "GET", "/", "", [new("host", "h"), new("x-amz-date", VectorDate), new("x-amz-security-token", token)],
            AwsSigV4.EmptyPayloadHash, "eu-west-1", "es", AccessKey, SecretKey, VectorTime);

        foreach (var text in new[] { signed.ToString(), steps.ToString() })
        {
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain(AccessKey, text);
            Assert.DoesNotContain(steps.Signature, text);
            Assert.DoesNotContain(SecretKey, text);
        }
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);

        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
