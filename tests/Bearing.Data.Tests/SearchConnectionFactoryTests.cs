using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bearing.Core.Data;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The search engines' HTTP transport, proven by <b>real round trips</b> to a small HTTP server on this
/// machine's loopback (plain, or TLS with a certificate made for the test) — never by inspecting a request that
/// was not sent (lesson 2026-11-19). The server records each request's line, headers and body, and answers with
/// what the test asks for.
/// </summary>
public class SearchConnectionFactoryTests
{
    private const string Ok = """{"name":"node-1","cluster_name":"test","version":{"number":"9.0.0"},"tagline":"You Know, for Search"}""";

    private static ConnectionInfo Info(
        int port,
        TlsMode tls,
        string host = "127.0.0.1",
        string user = "",
        CredentialKind kind = CredentialKind.StoredPassword,
        string? authentication = null,
        string? region = null)
    {
        var options = new Dictionary<string, string>();
        if (authentication is not null)
        {
            options[SearchConnectionFactory.AuthenticationOption] = authentication;
        }
        if (region is not null)
        {
            options[SearchConnectionFactory.RegionOption] = region;
        }

        return new()
        {
            Id = Guid.NewGuid(),
            Name = "search",
            ProviderId = "elasticsearch",
            Host = host,
            Port = port,
            User = user,
            Tls = tls,
            CredentialKind = kind,
            Options = options,
        };
    }

    // ---- TLS modes ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_sends_plain_http_with_the_json_and_agent_headers()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable), null);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        var request = Assert.Single(server.Requests);
        Assert.False(request.OverTls);
        Assert.Equal("GET /", request.Line[..5]);
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("Bearing", request.Header("User-Agent"));
        Assert.Equal($"127.0.0.1:{server.Port}", request.Header("Host"));
        Assert.Equal("http", factory.BaseUri!.Scheme);
    }

    [Fact]
    public async Task Require_accepts_a_self_signed_certificate()
    {
        await using var server = LoopbackServer.Tls(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Require), null);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.True(Assert.Single(server.Requests).OverTls);
        Assert.Equal("https", factory.BaseUri!.Scheme);
    }

    [Theory]
    [InlineData(TlsMode.VerifyFull)]
    [InlineData(TlsMode.VerifyCa)]
    public async Task A_verify_mode_rejects_an_untrusted_certificate_and_sends_no_request(TlsMode mode)
    {
        await using var server = LoopbackServer.Tls(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, mode), null);

        var failure = await Assert.ThrowsAsync<SearchTransportException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(HttpRequestError.SecureConnectionError, failure.Error);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Prefer_against_a_tls_server_uses_tls()
    {
        await using var server = LoopbackServer.Tls(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Prefer), null);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.True(Assert.Single(server.Requests).OverTls);
        Assert.Equal("https", factory.BaseUri!.Scheme);
    }

    [Fact]
    public async Task Prefer_against_a_plain_server_falls_back_after_the_handshake_fails_and_remembers_it()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Prefer), null);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(1, server.TlsHellos);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.False(r.OverTls));
        Assert.Equal("http", factory.BaseUri!.Scheme);
    }

    [Fact]
    public async Task Prefer_against_a_closed_port_fails_without_falling_back()
    {
        var port = ClosedPort();
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(port, TlsMode.Prefer), null);

        var failure = await Assert.ThrowsAsync<SearchTransportException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(HttpRequestError.ConnectionError, failure.Error);
        Assert.Null(factory.BaseUri);
    }

#pragma warning disable format
    [Theory]
    [InlineData(TlsMode.Require,    SslPolicyErrors.RemoteCertificateChainErrors,  true)]
    [InlineData(TlsMode.Prefer,     SslPolicyErrors.RemoteCertificateNotAvailable, true)]
    [InlineData(TlsMode.VerifyCa,   SslPolicyErrors.None,                          true)]
    [InlineData(TlsMode.VerifyCa,   SslPolicyErrors.RemoteCertificateNameMismatch, true)]
    [InlineData(TlsMode.VerifyCa,   SslPolicyErrors.RemoteCertificateChainErrors,  false)]
    [InlineData(TlsMode.VerifyCa,   SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    [InlineData(TlsMode.VerifyCa,   SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(TlsMode.VerifyFull, SslPolicyErrors.None,                          true)]
    [InlineData(TlsMode.VerifyFull, SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(TlsMode.VerifyFull, SslPolicyErrors.RemoteCertificateChainErrors,  false)]
#pragma warning restore format
    public void Certificate_validation_is_relaxed_only_where_the_mode_says_so(TlsMode mode, SslPolicyErrors errors, bool accepted)
        => Assert.Equal(accepted, SearchConnectionFactory.CertificateAccepted(mode, errors));

    // ---- Sign-in -----------------------------------------------------------------------------------------

    [Fact]
    public async Task User_and_password_send_exactly_basic_utf8()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable, user: "elastic"), "pa:ss wörd");

        await factory.TestConnectionAsync(CancellationToken.None);

        var header = Assert.Single(server.Requests).Header("Authorization")!;
        Assert.StartsWith("Basic ", header);
        Assert.Equal("elastic:pa:ss wörd", Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
        Assert.Equal(SearchAuthMethod.Basic, factory.AuthMethod);
    }

    [Fact]
    public async Task An_api_key_is_sent_exactly_and_the_user_is_not()
    {
        const string key = "VnVhQ2ZHY0JDZGJrUW0tZTVhT3g6dWkybHAyYXhUTm1zeWFrdzl0dk5udw==";
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch,
            Info(server.Port, TlsMode.Disable, user: "someuser", authentication: SearchConnectionFactory.AuthenticationApiKey),
            key + "\n");

        await factory.TestConnectionAsync(CancellationToken.None);

        var request = Assert.Single(server.Requests);
        Assert.Equal($"ApiKey {key}", request.Header("Authorization"));
        Assert.DoesNotContain("someuser", request.Raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_blank_user_and_password_send_no_authorization_header(string? secret)
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable, user: "  "), secret);

        await factory.TestConnectionAsync(CancellationToken.None);

        Assert.Null(Assert.Single(server.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task The_api_key_option_is_ignored_on_opensearch()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.OpenSearch,
            Info(server.Port, TlsMode.Disable, user: "admin", authentication: SearchConnectionFactory.AuthenticationApiKey),
            "secret-pass");

        await factory.TestConnectionAsync(CancellationToken.None);

        var header = Assert.Single(server.Requests).Header("Authorization")!;
        Assert.Equal("admin:secret-pass", Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
    }

    [Theory]
    [InlineData(SearchConnectionFactory.AuthenticationPassword)]
    [InlineData(SearchConnectionFactory.AuthenticationApiKey)]
    public async Task An_aws_profile_with_a_supplied_password_sends_neither_basic_nor_api_key(string authentication)
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.OpenSearch,
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile,
                authentication: authentication, region: "eu-west-1"),
            "typed-in-the-password-box",
            null,
            source,
            new FixedClock(SigningTime));

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        var request = Assert.Single(server.Requests);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", request.Header("Authorization"));
        Assert.DoesNotContain("typed-in-the-password-box", request.Raw);
        Assert.Equal(SearchAuthMethod.AwsSigV4, factory.AuthMethod);
    }

    // ---- AWS profile: SigV4 ------------------------------------------------------------------------------

    private const string FakeAccessKey = "AKIAFAKEKEYID1234567";
    private const string FakeSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET";
    private const string FakeToken = "IQoJb3JpZ2luX2VjFAKESESSIONTOKEN+/=";
    private static readonly DateTimeOffset SigningTime = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static SearchConnectionFactory Signed(
        ConnectionInfo info, IAwsCredentialSource source, TunnelEndpoint? tunnel = null, string? secret = null)
        => new(SearchEngine.OpenSearch, info, secret, tunnel, source, new FixedClock(SigningTime));

    [Fact]
    public async Task A_signed_request_carries_the_four_headers_and_a_signature_the_server_can_verify()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);
        var body = Encoding.UTF8.GetBytes("""{"query":"select * from logs"}""");

        var response = await factory.SendAsync(HttpMethod.Post, "/_plugins/_sql?format=jdbc", body, 1024, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        var request = Assert.Single(server.Requests);
        Assert.Equal($"127.0.0.1:{server.Port}", request.Header("Host"));
        Assert.Equal("20250102T030405Z", request.Header("x-amz-date"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), request.Header("x-amz-content-sha256"));
        Assert.Null(request.Header("x-amz-security-token"));
        var authorization = request.Header("Authorization")!;
        Assert.StartsWith(
            $"AWS4-HMAC-SHA256 Credential={FakeAccessKey}/20250102/eu-west-1/es/aws4_request, "
            + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=",
            authorization);
        Assert.Equal(authorization, ServerSideAuthorization(request, "eu-west-1", "es"));
        Assert.DoesNotContain(FakeSecretKey, request.Raw);
    }

    [Fact]
    public async Task A_session_token_is_sent_and_signed()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, FakeToken));
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, kind: CredentialKind.AwsProfile, region: "us-east-1"), source);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        var request = Assert.Single(server.Requests);
        Assert.Equal(FakeToken, request.Header("x-amz-security-token"));
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token,", request.Header("Authorization"));
        Assert.Equal(AwsSigV4.EmptyPayloadHash, request.Header("x-amz-content-sha256"));
        Assert.Equal(request.Header("Authorization"), ServerSideAuthorization(request, "us-east-1", "es"));
    }

    [Fact]
    public async Task Through_a_tunnel_the_signature_names_the_real_host_and_its_region_and_service()
    {
        const string host = "abc123.aoss.eu-central-1.on.aws";
        await using var server = LoopbackServer.Tls(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(443, TlsMode.Require, host: host, kind: CredentialKind.AwsProfile),
            source,
            new TunnelEndpoint("127.0.0.1", server.Port));

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        var request = Assert.Single(server.Requests);
        Assert.Equal(host, request.Header("Host"));
        Assert.Contains("/20250102/eu-central-1/aoss/aws4_request,", request.Header("Authorization"));
        Assert.Equal(request.Header("Authorization"), ServerSideAuthorization(request, "eu-central-1", "aoss"));
    }

    [Fact]
    public async Task Prefer_with_an_aws_profile_off_loopback_refuses_the_plain_http_fallback_and_sends_nothing()
    {
        // The handshake fails against a plain server; Prefer would fall back to http://, which would send the
        // signature and every row read in the clear. Off loopback that is refused (AwsNoTlsSentence).
        const string host = "search-x.eu-west-1.es.amazonaws.com";
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(443, TlsMode.Prefer, host: host, kind: CredentialKind.AwsProfile),
            source,
            new TunnelEndpoint("127.0.0.1", server.Port));

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.AwsNoTlsSentence, refused.Message);
        Assert.Equal(1, server.TlsHellos);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Credentials_are_asked_for_on_every_request()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);

        await factory.SendAsync(HttpMethod.Get, "/", null, 1024, CancellationToken.None);
        source.Next = new Amazon.Runtime.ImmutableCredentials("AKIAREFRESHEDKEY0001", FakeSecretKey, FakeToken);
        await factory.SendAsync(HttpMethod.Get, "/", null, 1024, CancellationToken.None);

        Assert.Equal(2, source.Calls);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains($"Credential={FakeAccessKey}/", server.Requests[0].Header("Authorization"));
        Assert.Contains("Credential=AKIAREFRESHEDKEY0001/", server.Requests[1].Header("Authorization"));
        Assert.Equal(FakeToken, server.Requests[1].Header("x-amz-security-token"));
    }

    [Fact]
    public async Task No_region_in_the_field_or_the_host_is_refused_before_credentials_are_asked_for()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(443, TlsMode.Require, host: "search.example.com", kind: CredentialKind.AwsProfile),
            source,
            new TunnelEndpoint("127.0.0.1", server.Port));

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.AwsRegionMissingSentence, refused.Message);
        Assert.Contains("AWS region field", refused.Message);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task A_403_from_the_server_stays_a_privilege_not_a_sign_in_failure()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(403,
            """{"error":{"type":"security_exception","reason":"no permissions for [cluster:monitor/main]"},"status":403}"""));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);

        var failure = await Assert.ThrowsAsync<SearchServerException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(403, failure.StatusCode);
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.Classify(failure));
    }

    [Fact]
    public async Task A_401_to_a_signed_request_names_the_profile()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(401, "{}"));
        var source = new FakeCredentials(new Amazon.Runtime.ImmutableCredentials(FakeAccessKey, FakeSecretKey, null));
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);

        var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchAuthMethod.AwsSigV4, refused.Method);
        Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(refused));
        Assert.Contains("aws sso login --profile dev", refused.Message);
    }

    [Fact]
    public async Task Anything_the_sdk_throws_while_producing_credentials_is_the_sign_in_sentence_without_its_text()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        var source = new FakeCredentials(null)
        {
            Failure = new Amazon.Runtime.AmazonClientException(
                $"Failed to resolve AWS credentials. Tried key {FakeAccessKey} with secret={FakeSecretKey}"),
        };
        await using var factory = Signed(
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);

        var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(refused));
        Assert.Equal(SearchErrors.AwsSignInSentence(SearchEngine.OpenSearch, "dev", Bearing.Data.Aws.AwsSignInProblem.CouldNotSignIn), refused.Message);
        Assert.Contains("aws sso login --profile dev", refused.Message);
        Assert.DoesNotContain(FakeAccessKey, refused.Message);
        Assert.DoesNotContain("Failed to resolve", refused.Message);
        Assert.Null(refused.InnerException);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task A_cancel_while_waiting_for_credentials_passes_as_it_came()
    {
        using var cancel = new CancellationTokenSource();
        var source = new FakeCredentials(null)
        {
            BeforeAnswer = cancel.Cancel,
            Failure = new OperationCanceledException(cancel.Token),
        };
        await using var factory = Signed(Info(9200, TlsMode.Disable, kind: CredentialKind.AwsProfile, region: "eu-west-1"), source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.SendAsync(HttpMethod.Get, "/", null, 1024, cancel.Token));
        Assert.Equal(1, source.Calls);
    }

    // Against the real SDK, over a temporary profiles file — never the user's own.

    [Fact]
    public async Task A_profile_the_sdk_does_not_know_is_a_sign_in_failure_naming_it_and_no_key_material()
    {
        var path = WriteProfiles($"""
            [somebody-else]
            aws_access_key_id = {FakeAccessKey}
            aws_secret_access_key = {FakeSecretKey}
            """);
        try
        {
            await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
            await using var factory = Signed(
                Info(server.Port, TlsMode.Disable, user: " dev ", kind: CredentialKind.AwsProfile, region: "eu-west-1"),
                new AwsProfileCredentialSource(SearchEngine.OpenSearch, "dev", path));

            var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(CancellationToken.None));

            Assert.Equal(Bearing.Data.Aws.AwsSignInProblem.ProfileNotFound, refused.Problem);
            Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(refused));
            Assert.Contains("'dev' was not found", refused.Message);
            Assert.Contains("aws sso login --profile dev", refused.Message);
            Assert.DoesNotContain(FakeAccessKey, refused.Message);
            Assert.DoesNotContain("wJalrXUtnFEMI", refused.Message);
            Assert.Equal(0, server.Connections);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_profile_without_credentials_is_not_called_missing()
    {
        var path = WriteProfiles("""
            [region-only]
            region = eu-west-1
            """);
        try
        {
            await using var factory = Signed(
                Info(9200, TlsMode.Disable, user: "region-only", kind: CredentialKind.AwsProfile, region: "eu-west-1"),
                new AwsProfileCredentialSource(SearchEngine.OpenSearch, "region-only", path));

            var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(CancellationToken.None));

            Assert.Equal(Bearing.Data.Aws.AwsSignInProblem.ProfileHasNoCredentials, refused.Problem);
            Assert.Contains("'region-only' was found", refused.Message);
            Assert.Contains("aws sso login --profile region-only", refused.Message);
            Assert.DoesNotContain("was not found", refused.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_sso_profile_with_no_signed_in_session_could_not_sign_in()
    {
        // A start URL nothing has signed in to: the SDK finds no cached token and fails before the network.
        var path = WriteProfiles($"""
            [bearing-test-sso]
            sso_start_url = https://bearing-test-{Guid.NewGuid():N}.awsapps.com/start
            sso_region = eu-west-1
            sso_account_id = 123456789012
            sso_role_name = Dev
            """);
        try
        {
            await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
            await using var factory = Signed(
                Info(server.Port, TlsMode.Disable, user: "bearing-test-sso", kind: CredentialKind.AwsProfile, region: "eu-west-1"),
                new AwsProfileCredentialSource(SearchEngine.OpenSearch, "bearing-test-sso", path));

            var refused = await Assert.ThrowsAsync<SearchSignInException>(
                () => factory.TestConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token));

            Assert.Equal(Bearing.Data.Aws.AwsSignInProblem.CouldNotSignIn, refused.Problem);
            Assert.Contains("aws sso login --profile bearing-test-sso", refused.Message);
            Assert.Null(refused.InnerException);
            Assert.Equal(0, server.Connections);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_profile_with_static_keys_signs_with_them()
    {
        var path = WriteProfiles($"""
            [static]
            aws_access_key_id = {FakeAccessKey}
            aws_secret_access_key = {FakeSecretKey}
            """);
        try
        {
            await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
            await using var factory = Signed(
                Info(server.Port, TlsMode.Disable, user: "static", kind: CredentialKind.AwsProfile, region: "eu-west-1"),
                new AwsProfileCredentialSource(SearchEngine.OpenSearch, "static", path));

            Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

            var request = Assert.Single(server.Requests);
            Assert.Contains($"Credential={FakeAccessKey}/20250102/eu-west-1/es/aws4_request", request.Header("Authorization"));
            Assert.Equal(request.Header("Authorization"), ServerSideAuthorization(request, "eu-west-1", "es"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Service and region (pure) ------------------------------------------------------------------------

#pragma warning disable format
    [Theory]
    [InlineData("search-logs-abc123.eu-west-1.es.amazonaws.com",   "es",   "eu-west-1")]
    [InlineData("vpc-logs-abc123.us-gov-west-1.es.amazonaws.com",  "es",   "us-gov-west-1")]
    [InlineData("abc123.us-east-1.aoss.amazonaws.com",             "aoss", "us-east-1")]
    [InlineData("abc123.aoss.eu-central-1.on.aws",                 "aoss", "eu-central-1")]
    [InlineData("abc123.aoss-fips.us-east-1.on.aws",               "aoss", "us-east-1")]
    [InlineData("SEARCH-X.EU-WEST-1.ES.AMAZONAWS.COM.",            "es",   "eu-west-1")]
    [InlineData("search.example.com",                              "es",   null)]
    [InlineData("logs.aoss.example.com",                           "aoss", null)]
    [InlineData("127.0.0.1",                                       "es",   null)]
    [InlineData("es.amazonaws.com",                                "es",   null)]
#pragma warning restore format
    public void Service_and_region_are_read_from_the_host(string host, string service, string? region)
    {
        Assert.Equal(service, SearchConnectionFactory.ServiceFor(host));
        Assert.Equal(region, SearchConnectionFactory.RegionFromHost(host));
    }

    [Theory]
    [InlineData("search-x.eu-west-1.es.amazonaws.com", null, "eu-west-1")]
    [InlineData("search-x.eu-west-1.es.amazonaws.com", "us-east-2", "us-east-2")] // the field wins over the host
    [InlineData("search.example.com", " AP-SOUTHEAST-2 ", "ap-southeast-2")]      // a custom endpoint with the field
    public void The_region_field_overrides_the_host(string host, string? field, string expected)
        => Assert.Equal(
            expected,
            SearchConnectionFactory.ResolveRegion(Info(443, TlsMode.Require, host: host, kind: CredentialKind.AwsProfile, region: field)));

    [Theory]
    [InlineData("search.example.com", null, SearchErrors.AwsRegionMissingSentence)]
    [InlineData("search.example.com", "  ", SearchErrors.AwsRegionMissingSentence)]
    [InlineData("search-x.eu-west-1.es.amazonaws.com", "eu-west-1\r\nX-Injected: yes", SearchErrors.AwsRegionInvalidSentence)]
    [InlineData("search-x.eu-west-1.es.amazonaws.com", "eu west 1", SearchErrors.AwsRegionInvalidSentence)]
    public void A_missing_or_malformed_region_is_refused_with_its_sentence(string host, string? field, string sentence)
    {
        var refused = Assert.Throws<SearchConfigurationException>(
            () => SearchConnectionFactory.ResolveRegion(Info(443, TlsMode.Require, host: host, kind: CredentialKind.AwsProfile, region: field)));

        Assert.Equal(sentence, refused.Message);
        Assert.DoesNotContain("Injected", refused.Message);
    }

    [Fact]
    public async Task An_aws_profile_on_elasticsearch_is_refused()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable, kind: CredentialKind.AwsProfile), "secret");

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.UnsupportedCredentialSentence(SearchEngine.Elasticsearch, CredentialKind.AwsProfile), refused.Message);
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_api_key_is_refused_at_connect(string? secret)
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch,
            Info(server.Port, TlsMode.Disable, user: "elastic", authentication: SearchConnectionFactory.AuthenticationApiKey),
            secret);

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.ApiKeyMissingSentence, refused.Message);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task An_api_key_holding_a_control_character_is_refused_without_quoting_it()
    {
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch,
            Info(9200, TlsMode.Disable, authentication: SearchConnectionFactory.AuthenticationApiKey),
            "abc\r\nX-Injected: yes");

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.ControlCharacterSentence, refused.Message);
        Assert.DoesNotContain("Injected", refused.Message);
    }

    [Fact]
    public async Task An_aws_profile_over_plain_http_off_loopback_is_refused_before_anything_is_sent()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        // The tunnel would reach the server if anything were sent.
        await using var factory = new SearchConnectionFactory(
            SearchEngine.OpenSearch,
            Info(443, TlsMode.Disable, host: "search-domain.eu-west-1.es.amazonaws.com", kind: CredentialKind.AwsProfile),
            null,
            new TunnelEndpoint("127.0.0.1", server.Port));

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.AwsPlainHttpSentence, refused.Message);
        Assert.Equal(0, server.Connections);
    }

    // ---- Redirects, tunnel, host -------------------------------------------------------------------------

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        await using var server = LoopbackServer.Plain(
            r => r.Target == "/" ? new Canned(302, "", ("Location", "/elsewhere")) : new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable), null);

        var failure = await Assert.ThrowsAsync<SearchServerException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(302, failure.StatusCode);
        Assert.Equal(SearchErrors.RedirectCode, failure.Error.SqlState);
        Assert.Equal(SearchErrors.RedirectSentence(302), failure.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Through_a_tunnel_the_request_names_the_real_host()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch,
            Info(9200, TlsMode.Disable, host: "search.invalid"),
            null,
            new TunnelEndpoint("127.0.0.1", server.Port));

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal("search.invalid:9200", Assert.Single(server.Requests).Header("Host"));
    }

    [Fact]
    public async Task Through_a_tunnel_tls_names_the_real_host()
    {
        await using var server = LoopbackServer.Tls(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.OpenSearch,
            Info(9200, TlsMode.Require, host: "search.invalid"),
            null,
            new TunnelEndpoint("127.0.0.1", server.Port));

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal("search.invalid", server.ServerNameIndication);
        Assert.Equal("search.invalid:9200", Assert.Single(server.Requests).Header("Host"));
    }

    [Theory]
    [InlineData("http://search.example.com")]
    [InlineData("https://elastic:hunter22@search.example.com")]
    [InlineData("search.example.com/prefix")]
    [InlineData("search.example.com:9200")]
    [InlineData("search example.com")]
    public async Task A_host_carrying_more_than_a_host_is_refused_at_connect_with_the_sentence(string host)
    {
        var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(9200, TlsMode.Require, host: host), null);
        await using (factory)
        {
            var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

            Assert.Equal(SearchErrors.HostOnlySentence, refused.Message);
            Assert.DoesNotContain("hunter22", refused.Message);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_host_is_refused_at_connect(string host)
    {
        await using var factory = new SearchConnectionFactory(SearchEngine.OpenSearch, Info(9200, TlsMode.Require, host: host), null);

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(SearchErrors.HostMissingSentence, refused.Message);
    }

    [Fact]
    public async Task Constructing_with_any_bad_setting_does_not_throw_and_every_use_says_why()
    {
        var bad = new[]
        {
            Info(9200, TlsMode.Require, host: "https://x"),
            Info(9200, TlsMode.Require, authentication: "Kerberos"),
            Info(9200, TlsMode.Require, kind: CredentialKind.EntraToken),
            Info(9200, TlsMode.Require, kind: CredentialKind.Integrated),
            Info(9200, TlsMode.Disable, host: "search.example.com", kind: CredentialKind.AwsProfile),
        };

        foreach (var info in bad)
        {
            var engine = info.CredentialKind == CredentialKind.AwsProfile ? SearchEngine.OpenSearch : SearchEngine.Elasticsearch;
            await using var factory = new SearchConnectionFactory(engine, info, "secret");

            await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));
            await Assert.ThrowsAsync<SearchConfigurationException>(
                () => factory.SendAsync(HttpMethod.Get, "/", null, 1024, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task A_port_outside_the_range_is_the_default(int port)
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch,
            Info(port, TlsMode.Disable, host: "search.invalid"),
            null,
            new TunnelEndpoint("127.0.0.1", server.Port));

        await factory.TestConnectionAsync(CancellationToken.None);

        Assert.Equal($"search.invalid:{SearchConnectionFactory.DefaultPort}", Assert.Single(server.Requests).Header("Host"));
    }

    // ---- The connection test -----------------------------------------------------------------------------

    [Fact]
    public async Task A_401_is_the_sign_in_failure()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(401,
            """{"error":{"type":"security_exception","reason":"unable to authenticate user [elastic]"},"status":401}""",
            ("WWW-Authenticate", "Basic realm=\"security\"")));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable, user: "elastic"), "wrong-password");

        var refused = await Assert.ThrowsAsync<SearchSignInException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(DbErrorKind.Authentication, SearchErrors.Classify(refused));
        Assert.Equal(SearchAuthMethod.Basic, refused.Method);
        Assert.DoesNotContain("wrong-password", refused.Message);
    }

    [Fact]
    public async Task A_root_that_is_not_served_is_judged_by_the_index_list()
    {
        await using var server = LoopbackServer.Plain(r => r.Target == "/" ? new Canned(404, "") : new Canned(200, "[]"));
        await using var factory = new SearchConnectionFactory(SearchEngine.OpenSearch, Info(server.Port, TlsMode.Disable), null);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(["/", "/_cat/indices?format=json&h=index"], server.Requests.Select(r => r.Target));
    }

    [Fact]
    public async Task Any_other_status_on_the_root_throws_the_servers_reason()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(403,
            """{"error":{"type":"security_exception","reason":"action [cluster:monitor/main] is unauthorized"},"status":403}"""));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable), null);

        var failure = await Assert.ThrowsAsync<SearchServerException>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal("action [cluster:monitor/main] is unauthorized", failure.Message);
        Assert.Equal(DbErrorKind.Unknown, SearchErrors.Classify(failure));
    }

    // ---- The send door -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_is_sent_as_json_and_warnings_and_errors_come_back_as_answers()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(400, """{"error":"bad"}""",
            ("Warning", "299 Elasticsearch-9.0.0-abc \"No limit defined, adding default limit of [1000]\""),
            ("Warning", "299 Elasticsearch-9.0.0-abc \"second\"")));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable), null);
        var body = Encoding.UTF8.GetBytes("""{"query":"FROM x"}""");

        var response = await factory.SendAsync(HttpMethod.Post, "/_query", body, 1024, CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("""{"error":"bad"}""", response.BodyText);
        Assert.Equal(
            ["299 Elasticsearch-9.0.0-abc \"No limit defined, adding default limit of [1000]\"", "299 Elasticsearch-9.0.0-abc \"second\""],
            response.Warnings);
        var request = Assert.Single(server.Requests);
        Assert.StartsWith("POST /_query ", request.Line);
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal(body, request.Body);
    }

    [Fact]
    public async Task An_answer_over_the_ceiling_is_refused()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, "[" + new string(' ', 5000) + "]"));
        await using var factory = new SearchConnectionFactory(SearchEngine.Elasticsearch, Info(server.Port, TlsMode.Disable), null);

        var refused = await Assert.ThrowsAsync<SearchResponseTooLargeException>(
            () => factory.SendAsync(HttpMethod.Get, "/_mapping", null, 1000, CancellationToken.None));

        Assert.Equal(1000, refused.LimitBytes);
    }

    [Fact]
    public async Task A_handed_in_api_still_gets_the_redirect_and_sign_in_rules()
    {
        var api = new FakeSearchApi(new SearchResponse(401, [], []));
        await using var factory = new SearchConnectionFactory(api, SearchEngine.OpenSearch, Info(9200, TlsMode.Require, user: "admin"));

        await Assert.ThrowsAsync<SearchSignInException>(
            () => factory.SendAsync(HttpMethod.Post, "/_plugins/_sql", [], 1024, CancellationToken.None));

        api.Next = new SearchResponse(500, Encoding.UTF8.GetBytes("{}"), []);
        var answer = await factory.SendAsync(HttpMethod.Post, "/_plugins/_sql", [], 1024, CancellationToken.None);

        Assert.Equal(500, answer.StatusCode);
        Assert.Equal(2, api.Calls);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private static string WriteProfiles(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bearing-aws-{Guid.NewGuid():N}");
        File.WriteAllText(path, text);

        return path;
    }

    /// <summary>
    /// What a server holding the secret would compute for <paramref name="request"/> as it arrived: the signed
    /// header names from its <c>Authorization</c>, their values and the body as received, the request target as
    /// sent. Equal to the header only when the signature covers exactly what went over the wire.
    /// </summary>
    private static string ServerSideAuthorization(Recorded request, string region, string service)
    {
        var authorization = request.Header("Authorization")!;
        var signedNames = authorization.Split("SignedHeaders=")[1].Split(',')[0].Split(';');
        var accessKey = authorization.Split("Credential=")[1].Split('/')[0];
        var date = DateTime.ParseExact(
            request.Header("x-amz-date")!, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var target = request.Target.Split('?', 2);
        var method = request.Line.Split(' ')[0];

        return AwsSigV4.Compute(
                method,
                target[0],
                target.Length > 1 ? target[1] : "",
                signedNames.Select(name => new KeyValuePair<string, string>(name, request.Header(name)!)),
                Convert.ToHexStringLower(SHA256.HashData(request.Body)),
                region,
                service,
                accessKey,
                FakeSecretKey,
                date)
            .Authorization;
    }

    /// <summary>Hands out the credentials it was given (or throws its failure) and counts the asks.</summary>
    private sealed class FakeCredentials(Amazon.Runtime.ImmutableCredentials? next) : IAwsCredentialSource
    {
        private int _calls;

        public Amazon.Runtime.ImmutableCredentials? Next { get; set; } = next;

        public Exception? Failure { get; init; }

        public Action? BeforeAnswer { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<Amazon.Runtime.ImmutableCredentials> GetAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            BeforeAnswer?.Invoke();
            if (Failure is not null)
            {
                return Task.FromException<Amazon.Runtime.ImmutableCredentials>(Failure);
            }

            return Task.FromResult(Next ?? throw new InvalidOperationException("No credentials were set for the fake."));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSearchApi(SearchResponse next) : ISearchApi
    {
        public SearchResponse Next { get; set; } = next;

        public int Calls { get; private set; }

        public Task<SearchResponse> SendAsync(
            HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }

        public void Dispose()
        {
        }
    }

    private sealed record Canned(int Status, string Body, params (string Name, string Value)[] Headers);

    private sealed record Recorded(string Line, string Target, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body, string Raw, bool OverTls)
    {
        public string? Header(string name)
            => Headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value)
                .FirstOrDefault();
    }

    /// <summary>
    /// A one-request-per-connection HTTP/1.1 server on 127.0.0.1. Plain, it answers a TLS ClientHello the way a
    /// plain HTTP server does (a 400 in clear text, then close) and counts it; with a certificate it speaks TLS
    /// only and records the SNI it was asked for.
    /// </summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private static readonly Lazy<X509Certificate2> Certificate = new(MakeCertificate);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<Recorded, Canned> _respond;
        private readonly X509Certificate2? _certificate;
        private readonly ConcurrentQueue<Recorded> _requests = new();
        private readonly Task _loop;
        private int _tlsHellos;
        private int _connections;

        private LoopbackServer(Func<Recorded, Canned> respond, X509Certificate2? certificate)
        {
            _respond = respond;
            _certificate = certificate;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        public static LoopbackServer Plain(Func<Recorded, Canned> respond) => new(respond, null);

        public static LoopbackServer Tls(Func<Recorded, Canned> respond) => new(respond, Certificate.Value);

        public int Port { get; }

        public IReadOnlyList<Recorded> Requests => _requests.ToArray();

        public int TlsHellos => Volatile.Read(ref _tlsHellos);

        public int Connections => Volatile.Read(ref _connections);

        public string? ServerNameIndication { get; private set; }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // The loop ends by the listener being stopped.
            }
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                Stream stream = client.GetStream();
                var overTls = false;
                byte[] prefix = [];
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) =>
                        {
                            ServerNameIndication = name;
                            return _certificate;
                        },
                    }, timeout.Token);
                    stream = ssl;
                    overTls = true;
                }
                else
                {
                    var first = new byte[1];
                    if (await stream.ReadAsync(first, timeout.Token) == 0)
                    {
                        return;
                    }
                    if (first[0] == 0x16)
                    {
                        // A TLS handshake record: answer as a plain HTTP server would, and hang up.
                        Interlocked.Increment(ref _tlsHellos);
                        await stream.WriteAsync(
                            Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                            timeout.Token);
                        return;
                    }
                    prefix = first;
                }

                var request = await ReadRequestAsync(stream, prefix, overTls, timeout.Token);
                _requests.Enqueue(request);
                var canned = _respond(request);
                var body = Encoding.UTF8.GetBytes(canned.Body);
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {canned.Status} Canned\r\n")
                    .Append("Content-Type: application/json\r\n")
                    .Append($"Content-Length: {body.Length}\r\n")
                    .Append("Connection: close\r\n");
                foreach (var (name, value) in canned.Headers)
                {
                    head.Append($"{name}: {value}\r\n");
                }
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (Exception)
            {
                // A client that gave up (a refused certificate, a cancelled request) is not the test's failure.
            }
        }

        private static async Task<Recorded> ReadRequestAsync(Stream stream, byte[] prefix, bool overTls, CancellationToken ct)
        {
            var buffer = new List<byte>(prefix);
            var one = new byte[1];
            while (!EndsWithBlankLine(buffer))
            {
                if (await stream.ReadAsync(one, ct) == 0)
                {
                    break;
                }
                buffer.Add(one[0]);
            }

            var head = Encoding.UTF8.GetString(buffer.ToArray());
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .Select(parts => (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : ""))
                .ToList();
            var length = headers
                .Where(h => string.Equals(h.Item1, "Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(h => int.Parse(h.Item2))
                .FirstOrDefault();
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read), ct);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            var requestLine = lines.Length > 0 ? lines[0] : "";
            var target = requestLine.Split(' ') is { Length: >= 2 } words ? words[1] : "";

            return new Recorded(requestLine, target, headers, body, head + Encoding.UTF8.GetString(body), overTls);
        }

        private static bool EndsWithBlankLine(List<byte> buffer)
            => buffer.Count >= 4
               && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n';

        /// <summary>A self-signed server certificate for localhost, 127.0.0.1 and search.invalid — made here,
        /// trusted by nothing. Round-tripped through PKCS#12 so the platform's TLS stack can use its key.</summary>
        private static X509Certificate2 MakeCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddDnsName("search.invalid");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
        }
    }
}
