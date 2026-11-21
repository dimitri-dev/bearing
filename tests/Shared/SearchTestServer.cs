using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bearing.Core.Data;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Testing;

/// <summary>
/// The Elasticsearch and OpenSearch servers SearchLiveTests use — <see cref="DynamoDbTestServer"/>'s sibling.
/// <c>build/test-db-search.sh</c> starts three:
/// <list type="bullet">
///   <item><c>http://localhost:19200</c> — Elasticsearch, security off (<c>BEARING_TEST_ELASTICSEARCH_ENDPOINT</c>);</item>
///   <item><c>http://localhost:19201</c> — OpenSearch, security plugin off (<c>BEARING_TEST_OPENSEARCH_ENDPOINT</c>);</item>
///   <item><c>https://localhost:19202</c> — Elasticsearch with security on, user <c>elastic</c>
///     (<c>BEARING_TEST_ELASTICSEARCH_SECURE_ENDPOINT</c>, password <c>BEARING_TEST_ELASTICSEARCH_SECURE_PASSWORD</c>).
///     Measured on the 9.5.6 image: with security left at its default the node auto-configures TLS for HTTP on
///     first start and serves HTTPS only, with a certificate its own generated CA signed (names <c>localhost</c>
///     and <c>127.0.0.1</c>); a plain-HTTP request gets the connection closed with no answer. No machine trusts
///     that CA, so a connection needs TLS mode Require, and Verify Full refuses it.</item>
/// </list>
/// Unreachable means skipped (§4.2) — and a skip is not a pass.
/// <para>
/// Fixtures are made over <b>raw HTTP</b> with this class's own <see cref="HttpClient"/>
/// (<see cref="SearchFixtures"/>), never through the provider under test, so the code being tested is never also
/// the thing that sets the test up. Every fixture name is unique per call and removed when the fixture set is
/// disposed (§4.7).
/// </para>
/// </summary>
public static class SearchTestServer
{
    public const string DefaultElasticsearchEndpoint = "http://localhost:19200";
    public const string DefaultOpenSearchEndpoint = "http://localhost:19201";
    public const string DefaultSecureEndpoint = "https://localhost:19202";
    public const string DefaultSecurePassword = "Bearing-Test-1";

    /// <summary>The built-in superuser the secured node's password is set for.</summary>
    public const string SecureUser = "elastic";

    /// <summary>The plain (security off) server for <paramref name="providerId"/>.</summary>
    public static Uri Endpoint(string providerId) => new(providerId switch
    {
        SearchProvider.ElasticsearchId => Setting("BEARING_TEST_ELASTICSEARCH_ENDPOINT", DefaultElasticsearchEndpoint),
        SearchProvider.OpenSearchId => Setting("BEARING_TEST_OPENSEARCH_ENDPOINT", DefaultOpenSearchEndpoint),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Not a search engine."),
    });

    /// <summary>The secured Elasticsearch node.</summary>
    public static Uri SecureEndpoint => new(Setting("BEARING_TEST_ELASTICSEARCH_SECURE_ENDPOINT", DefaultSecureEndpoint));

    public static string SecurePassword => Setting("BEARING_TEST_ELASTICSEARCH_SECURE_PASSWORD", DefaultSecurePassword);

    public static SearchProvider Provider(string providerId) => providerId switch
    {
        SearchProvider.ElasticsearchId => SearchProvider.Elasticsearch(),
        SearchProvider.OpenSearchId => SearchProvider.OpenSearch(),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Not a search engine."),
    };

    /// <summary>
    /// A <see cref="ConnectionInfo"/> for <paramref name="providerId"/> aimed at its plain server, with a fresh id
    /// per call: host and port from the endpoint, no user (security is off, so no <c>Authorization</c> is sent),
    /// TLS mode <see cref="TlsMode.Disable"/> for an <c>http</c> endpoint unless <paramref name="tls"/> says
    /// otherwise.
    /// </summary>
    public static ConnectionInfo Info(string providerId, TlsMode? tls = null) => For(providerId, Endpoint(providerId), tls, user: "");

    /// <summary>
    /// A <see cref="ConnectionInfo"/> for the secured Elasticsearch node: user <see cref="SecureUser"/> (or
    /// <paramref name="user"/>), TLS mode <see cref="TlsMode.Require"/> for its self-signed <c>https</c>
    /// endpoint unless <paramref name="tls"/> says otherwise, and <paramref name="authentication"/> as the
    /// connection's Authentication choice when given.
    /// </summary>
    public static ConnectionInfo SecureInfo(TlsMode? tls = null, string? authentication = null, string user = SecureUser)
    {
        var info = For(SearchProvider.ElasticsearchId, SecureEndpoint, tls, user);

        return authentication is null
            ? info
            : info with
            {
                Options = new Dictionary<string, string> { [SearchConnectionFactory.AuthenticationOption] = authentication },
            };
    }

    public static IDbConnectionFactory Factory(ConnectionInfo info, string? secret = null)
        => Provider(info.ProviderId).CreateConnectionFactory(info, secret);

    /// <summary>A name no other test, and no earlier run against a long-lived server, has used: lower case, no
    /// dot, a plain SQL word on both engines (<c>bearing_test_&lt;stem&gt;_&lt;guid&gt;</c>).</summary>
    public static string UniqueName(string stem) => $"bearing_test_{stem}_{Guid.NewGuid():N}";

    /// <summary>Skip unless the plain server for <paramref name="providerId"/> answers.</summary>
    public static Task RequireAsync(string providerId)
        => RequireAsync(Endpoint(providerId), $"No {Provider(providerId).DisplayName} reachable for integration test",
            secured: false);

    /// <summary>Skip unless the secured Elasticsearch node answers.</summary>
    public static Task RequireSecureAsync()
        => RequireAsync(SecureEndpoint, "No secured Elasticsearch reachable for integration test", secured: true);

    /// <summary>Fixtures on the plain server for <paramref name="providerId"/>; the caller disposes them.</summary>
    public static SearchFixtures Fixtures(string providerId) => new(Endpoint(providerId), user: null, password: null);

    /// <summary>Fixtures on the secured node, signed in as <see cref="SecureUser"/>; the caller disposes them.</summary>
    public static SearchFixtures SecureFixtures() => new(SecureEndpoint, SecureUser, SecurePassword);

    // ---- skip plumbing -----------------------------------------------------------------------------

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<string?>> Probes = new(StringComparer.Ordinal);

    /// <summary>
    /// Skip only when nothing answers at <paramref name="endpoint"/>: no connection, a TLS handshake that never
    /// completes, or no answer within 10 seconds. A server that answered — with any status, a 401 included — is
    /// not a reason to skip: the tests run and say what is wrong. Probed once per endpoint per run.
    /// </summary>
    private static async Task RequireAsync(Uri endpoint, string what, bool secured)
    {
        Task<string?> probe;
        lock (Gate)
        {
            if (!Probes.TryGetValue(endpoint.AbsoluteUri, out var existing))
            {
                existing = ProbeAsync(endpoint);
                Probes[endpoint.AbsoluteUri] = existing;
            }
            probe = existing;
        }

        var reason = await probe.ConfigureAwait(false);
        Skip.If(reason is not null, $"{what} at {endpoint} — {reason?.TrimEnd('.', ' ')}. Start "
            + (secured ? "it" : "one") + " with ./build/test-db-search.sh.");
    }

    private static async Task<string?> ProbeAsync(Uri endpoint)
    {
        using var client = SearchFixtures.CreateClient(endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/"), timeout.Token).ConfigureAwait(false);

            return null;
        }
        catch (HttpRequestException e)
        {
            return SafeErrorText.Of(e);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return "it did not answer within 10 seconds.";
        }
    }

    private static ConnectionInfo For(string providerId, Uri endpoint, TlsMode? tls, string user) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{providerId}-test",
        ProviderId = providerId,
        Host = endpoint.Host,
        Port = endpoint.Port,
        User = user,
        CredentialKind = CredentialKind.StoredPassword,
        Tls = tls ?? (endpoint.Scheme == Uri.UriSchemeHttps ? TlsMode.Require : TlsMode.Disable),
    };

    private static string Setting(string variable, string fallback)
        => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : fallback;
}

/// <summary>A raw HTTP answer from a fixture call: status, body text, and every <c>Warning</c> header value as the
/// handler exposed it (<see cref="HttpResponseHeaders.NonValidated"/>, the same read the provider makes).</summary>
public sealed record RawSearchResponse(int StatusCode, string Body, IReadOnlyList<string> Warnings)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The body parsed; the caller disposes the document.</summary>
    public JsonDocument Json() => JsonDocument.Parse(Body);
}

/// <summary>
/// One test's fixtures on one server, made over raw HTTP and removed on dispose (§4.7): indices (an alias goes
/// with its index), index templates, data streams and API keys. A removal the server answers with 404 means it
/// is already gone; any other failure is collected and thrown once everything else has been tried, so a fixture
/// that could not be removed is said rather than left silently.
/// </summary>
public sealed class SearchFixtures : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly List<string> _indices = [];
    private readonly List<string> _templates = [];
    private readonly List<string> _streams = [];
    private readonly List<string> _apiKeys = [];

    internal SearchFixtures(Uri endpoint, string? user, string? password)
    {
        Endpoint = endpoint;
        _client = CreateClient(endpoint);
        if (user is not null)
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }
    }

    public Uri Endpoint { get; }

    /// <summary>
    /// The fixtures' own client. Redirects are not followed. A certificate is accepted whatever its chain only when
    /// the endpoint is loopback — the secured test node's self-made CA, which nothing trusts — and is validated
    /// normally anywhere else. Test plumbing only: the provider under test does its own TLS (§1.4).
    /// </summary>
    internal static HttpClient CreateClient(Uri endpoint)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (endpoint.IsLoopback)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Any request, answered as it came (no status check) — for the measurements.</summary>
    public async Task<RawSearchResponse> SendAsync(HttpMethod method, string pathAndQuery, string? json = null)
    {
        using var request = new HttpRequestMessage(method, pathAndQuery);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>
    /// A uniquely named index with an explicit mapping. <paramref name="properties"/> is the JSON object that goes
    /// under <c>mappings.properties</c>. <paramref name="name"/> overrides the generated name (a test about names
    /// passes its own unique one).
    /// </summary>
    public async Task<string> CreateIndexAsync(string stem, string properties, string? name = null)
    {
        name ??= SearchTestServer.UniqueName(stem);
        _indices.Add(name);
        await Expect(HttpMethod.Put, $"/{Uri.EscapeDataString(name)}",
            $$$"""{"settings":{"number_of_shards":1,"number_of_replicas":0},"mappings":{"properties":{{{properties}}}}}""")
            .ConfigureAwait(false);

        return name;
    }

    /// <summary>
    /// An index whose one shard can never be allocated (it requires a node that does not exist), so the cluster
    /// cannot read it — the way to make a search answer partial on a one-node cluster.
    /// </summary>
    public async Task<string> CreateUnallocatableIndexAsync(string stem, string properties)
    {
        var name = SearchTestServer.UniqueName(stem);
        _indices.Add(name);
        // wait_for_active_shards=0: the shard never becomes active, so the default wait would run to its timeout.
        await Expect(HttpMethod.Put, $"/{Uri.EscapeDataString(name)}?wait_for_active_shards=0",
            $$$"""
              {"settings":{"number_of_shards":1,"number_of_replicas":0,
                "index.routing.allocation.require._name":"bearing-no-such-node"},
               "mappings":{"properties":{{{properties}}}}}
              """).ConfigureAwait(false);

        return name;
    }

    /// <summary>Index <paramref name="documents"/> (one JSON object each) with <c>refresh=true</c>, so a read that
    /// follows sees them. Fails on any per-item error. <paramref name="create"/> writes with <c>create</c> — what
    /// a data stream requires.</summary>
    public async Task BulkAsync(string target, IEnumerable<string> documents, bool create = false)
    {
        var body = new StringBuilder();
        foreach (var document in documents)
        {
            body.Append(create ? """{"create":{}}""" : """{"index":{}}""").Append('\n').Append(document).Append('\n');
        }
        if (body.Length == 0) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Uri.EscapeDataString(target)}/_bulk?refresh=true")
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson"),
        };
        var response = await SendAsync(request).ConfigureAwait(false);
        Check(response, $"bulk into {target}");

        using var json = response.Json();
        if (json.RootElement.GetProperty("errors").GetBoolean())
        {
            throw new InvalidOperationException($"The bulk into {target} reported item errors: {Cut(response.Body)}");
        }
    }

    /// <summary>A uniquely named alias on <paramref name="target"/> (removed with its index or stream).</summary>
    public async Task<string> CreateAliasAsync(string target, string stem)
    {
        var alias = SearchTestServer.UniqueName(stem);
        await Expect(HttpMethod.Post, "/_aliases",
            $$$"""{"actions":[{"add":{"index":"{{{target}}}","alias":"{{{alias}}}"}}]}""").ConfigureAwait(false);

        return alias;
    }

    /// <summary>The raw answer to adding an alias — for the engine that refuses one on a data stream.</summary>
    public Task<RawSearchResponse> TryCreateAliasAsync(string target, string alias)
        => SendAsync(HttpMethod.Post, "/_aliases", $$$"""{"actions":[{"add":{"index":"{{{target}}}","alias":"{{{alias}}}"}}]}""");

    /// <summary>
    /// A uniquely named data stream: an index template matching exactly its name with <c>data_stream: {}</c> and
    /// <paramref name="properties"/> (which must hold an <c>@timestamp</c> date), then the stream itself. A high
    /// template priority wins over the servers' built-in <c>logs-*</c> style templates.
    /// </summary>
    public async Task<string> CreateDataStreamAsync(string stem, string properties)
    {
        var name = SearchTestServer.UniqueName(stem);
        var template = $"{name}_template";
        _templates.Add(template);
        await Expect(HttpMethod.Put, $"/_index_template/{template}",
            $$$"""
              {"index_patterns":["{{{name}}}"],"data_stream":{},"priority":500,
               "template":{"settings":{"number_of_shards":1,"number_of_replicas":0},
                           "mappings":{"properties":{{{properties}}}}} }
              """).ConfigureAwait(false);
        _streams.Add(name);
        await Expect(HttpMethod.Put, $"/_data_stream/{name}").ConfigureAwait(false);

        return name;
    }

    /// <summary>Close a SQL cursor on the server (<c>_sql/close</c> or <c>_plugins/_sql/close</c>); the answer as
    /// it came.</summary>
    public Task<RawSearchResponse> CloseSqlCursorAsync(string providerId, string cursor)
        => SendAsync(HttpMethod.Post,
            providerId == SearchProvider.OpenSearchId ? "/_plugins/_sql/close" : "/_sql/close",
            JsonSerializer.Serialize(new { cursor }));

    /// <summary>
    /// A uniquely named Elasticsearch API key valid for an hour, invalidated on dispose. Returns its
    /// <c>encoded</c> form — what Kibana shows and what <c>Authorization: ApiKey</c> takes. The key is a secret:
    /// the caller passes it to the provider and nowhere else.
    /// </summary>
    public async Task<string> CreateApiKeyAsync(string stem)
    {
        var response = await Expect(HttpMethod.Post, "/_security/api_key",
            $$"""{"name":"{{SearchTestServer.UniqueName(stem)}}","expiration":"1h"}""").ConfigureAwait(false);

        using var json = response.Json();
        _apiKeys.Add(json.RootElement.GetProperty("id").GetString()!);

        return json.RootElement.GetProperty("encoded").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<string>();

        foreach (var id in _apiKeys)
        {
            await Remove(failures, HttpMethod.Delete, "/_security/api_key", JsonSerializer.Serialize(new { ids = new[] { id } }))
                .ConfigureAwait(false);
        }
        foreach (var stream in _streams)
        {
            await Remove(failures, HttpMethod.Delete, $"/_data_stream/{stream}").ConfigureAwait(false);
        }
        foreach (var template in _templates)
        {
            await Remove(failures, HttpMethod.Delete, $"/_index_template/{template}").ConfigureAwait(false);
        }
        foreach (var index in _indices)
        {
            await Remove(failures, HttpMethod.Delete, $"/{Uri.EscapeDataString(index)}").ConfigureAwait(false);
        }

        _client.Dispose();

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Could not remove every fixture on {Endpoint}: {string.Join("; ", failures)}");
        }
    }

    private async Task Remove(List<string> failures, HttpMethod method, string path, string? json = null)
    {
        try
        {
            var response = await SendAsync(method, path, json).ConfigureAwait(false);
            if (!response.IsSuccess && response.StatusCode != 404)
            {
                failures.Add($"{method} {path} answered HTTP {response.StatusCode}: {Cut(response.Body)}");
            }
        }
        catch (HttpRequestException e)
        {
            failures.Add($"{method} {path} failed: {SafeErrorText.Of(e)}");
        }
    }

    private async Task<RawSearchResponse> Expect(HttpMethod method, string path, string? json = null)
    {
        var response = await SendAsync(method, path, json).ConfigureAwait(false);
        Check(response, $"{method} {path}");

        return response;
    }

    private static void Check(RawSearchResponse response, string what)
    {
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException($"Fixture call {what} answered HTTP {response.StatusCode}: {Cut(response.Body)}");
        }
    }

    private async Task<RawSearchResponse> SendAsync(HttpRequestMessage request)
    {
        using var response = await _client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var warnings = response.Headers.NonValidated.TryGetValues("Warning", out var values)
            ? values.ToList()
            : [];

        return new RawSearchResponse((int)response.StatusCode, body, warnings);
    }

    private static string Cut(string body) => body.Length <= 400 ? body : body[..400] + "…";
}
