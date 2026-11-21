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
        string? authentication = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "search",
            ProviderId = "elasticsearch",
            Host = host,
            Port = port,
            User = user,
            Tls = tls,
            CredentialKind = kind,
            Options = authentication is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [SearchConnectionFactory.AuthenticationOption] = authentication },
        };

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

    [Fact]
    public async Task An_aws_profile_with_a_supplied_password_sends_neither_basic_nor_api_key()
    {
        await using var server = LoopbackServer.Plain(_ => new Canned(200, Ok));
        await using var factory = new SearchConnectionFactory(
            SearchEngine.OpenSearch,
            Info(server.Port, TlsMode.Disable, user: "dev", kind: CredentialKind.AwsProfile,
                authentication: SearchConnectionFactory.AuthenticationPassword),
            "typed-in-the-password-box");

        var refused = await Assert.ThrowsAsync<SearchConfigurationException>(() => factory.TestConnectionAsync(CancellationToken.None));

        // Past the plain-HTTP check (loopback); the signer is not part of this build, so nothing is sent.
        Assert.Equal(SearchErrors.SigningUnavailableSentence, refused.Message);
        Assert.Empty(server.Requests);
        Assert.Equal(0, server.Connections);
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
