using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using Bearing.Core.Data;

namespace Bearing.Data.Search;

/// <summary>
/// The HTTP transport under both search engines: one connection's scheme, certificate rule, sign-in and dialler,
/// and the one door every request goes through (<see cref="SendAsync"/>). No client library — an
/// <see cref="HttpClient"/> over a <see cref="SocketsHttpHandler"/>.
/// <para>
/// <b>Scheme and certificates follow <see cref="TlsPolicy.Resolve"/></b>, and certificate validation is relaxed
/// only in the modes that say so: <see cref="TlsMode.Disable"/> is <c>http://</c>; <see cref="TlsMode.Require"/>
/// is <c>https://</c> accepting any certificate ("encrypted, unverified"); <see cref="TlsMode.VerifyCa"/> needs a
/// trusted chain and accepts a host-name mismatch alone; <see cref="TlsMode.VerifyFull"/> is the platform's own
/// validation. <see cref="TlsMode.Prefer"/> is <c>https://</c> with Require's rule, falling back to
/// <c>http://</c> only when the TLS handshake itself fails — not on a refused connection, a timeout, an HTTP
/// error or a certificate — decided by the first request that gets an answer and kept for this factory. The OS
/// trust store is the only trust source; no client certificate is offered.
/// </para>
/// <para>
/// <b>Sign-in is decided by the settings, never by whether a secret is present</b> (the dialog's Test hands over
/// the password box whatever the kind): <see cref="CredentialKind.StoredPassword"/> / <see cref="CredentialKind.Prompt"/>
/// with <c>Options["Authentication"]</c> absent or <see cref="AuthenticationPassword"/> is HTTP Basic — and no
/// <c>Authorization</c> header at all when both the user and the secret are blank; with
/// <see cref="AuthenticationApiKey"/> (Elasticsearch only; ignored on OpenSearch) it is
/// <c>Authorization: ApiKey &lt;secret&gt;</c> with no user sent. <see cref="CredentialKind.AwsProfile"/> is
/// never Basic or ApiKey, whatever secret was passed; plain HTTP to a host that is not this machine is refused
/// for it before anything is sent, because a signed request over plain HTTP exposes the signature and the data.
/// </para>
/// <para>
/// <b>AWS profile (OpenSearch only).</b> Every request is signed with SigV4 (<see cref="AwsSigV4SearchAuth"/>):
/// service <c>aoss</c> for a host containing <c>.aoss.</c> or <c>.aoss-fips.</c>, else <c>es</c>
/// (<see cref="ServiceFor"/>); region from <c>Options["Region"]</c>, else read from an Amazon endpoint name, else
/// the connect is refused naming the AWS region field (<see cref="ResolveRegion"/>). The profile is
/// <see cref="ConnectionInfo.User"/>, trimmed; blank is the SDK's default chain. The SDK's credentials object is
/// resolved once, at the first request, and asked for its keys on every request so a refreshed SSO or
/// assumed-role session is used; the keys live for that one request and are never stored, logged or shown. A
/// credential failure is a <see cref="SearchSignInException"/> built from the profile name alone. The signature
/// covers the request as sent — the real host (also through a tunnel) and the exact body bytes.
/// </para>
/// <para>
/// <b>Redirects are never followed</b>: a 3xx is a <see cref="SearchServerException"/> saying so. <b>A 401</b> is
/// a <see cref="SearchSignInException"/>, classified <see cref="DbErrorKind.Authentication"/>.
/// </para>
/// <para>
/// <b>Tunnel.</b> With a <see cref="TunnelEndpoint"/>, the request URI still names the connection's real host and
/// port — so the <c>Host</c> header, SNI and certificate validation are the real server's — while the TCP
/// connection is dialled to the tunnel's loopback end (<see cref="SocketsHttpHandler.ConnectCallback"/>). No
/// proxy on that path.
/// </para>
/// <para>
/// <b>A setting that cannot be honoured fails the connect, not the constructor.</b> The constructor never throws
/// for one; the sentence is thrown by <see cref="TestConnectionAsync"/> and every <see cref="SendAsync"/>, and
/// nothing is sent from a refused setting.
/// </para>
/// </summary>
public sealed class SearchConnectionFactory : IDbConnectionFactory
{
    /// <summary>The key in <see cref="ConnectionInfo.Options"/> the editor's Authentication choice fills
    /// (Elasticsearch).</summary>
    public const string AuthenticationOption = "Authentication";

    /// <summary>The Authentication value for HTTP Basic — also what an absent option means.</summary>
    public const string AuthenticationPassword = "User and password";

    /// <summary>The Authentication value for <c>Authorization: ApiKey …</c>.</summary>
    public const string AuthenticationApiKey = "API key";

    /// <summary>The key in <see cref="ConnectionInfo.Options"/> the editor's AWS region field fills (OpenSearch).</summary>
    public const string RegionOption = "Region";

    /// <summary>The port both servers listen on out of the box — used when the connection's port is not 1–65535.</summary>
    public const int DefaultPort = 9200;

    /// <summary>The ceiling for one answer when the caller has no reason to pick another (the schema reads).</summary>
    internal const long DefaultResponseLimitBytes = 64L * 1024 * 1024;

    /// <summary>What the connection test reads of <c>GET /</c> — a small JSON document on both servers.</summary>
    internal const long RootResponseLimitBytes = 1024L * 1024;

    /// <summary>How long establishing a connection (TCP and TLS) may take. The statement timeout is the
    /// executor's: <see cref="HttpClient.Timeout"/> is infinite.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly SearchSettings? _settings;
    private readonly Exception? _failure;
    private readonly TunnelEndpoint? _tunnel;
    private readonly ISearchApi? _injected;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ISearchApi? _api;
    private bool _disposed;

    /// <summary>A factory over the real transport. Never throws for a setting: see the class remarks.</summary>
    /// <param name="secret">The password or API key; null or blank for none. Ignored for an AWS profile.</param>
    /// <param name="tunnel">The loopback end of an SSH forward to dial instead of the host, or null.</param>
    internal SearchConnectionFactory(SearchEngine engine, ConnectionInfo info, string? secret, TunnelEndpoint? tunnel = null)
        : this(engine, info, secret, tunnel, awsCredentials: null, clock: null)
    {
    }

    /// <summary>The real transport with an AWS credential source and a clock someone else made — the fakes, in the
    /// signer's round-trip tests, or a source over a temporary profiles file. Nothing in the app passes either;
    /// null is the real one (<see cref="AwsProfileCredentialSource"/> over the SDK's own files, the system clock).</summary>
    internal SearchConnectionFactory(
        SearchEngine engine,
        ConnectionInfo info,
        string? secret,
        TunnelEndpoint? tunnel,
        IAwsCredentialSource? awsCredentials,
        TimeProvider? clock)
    {
        Engine = engine;
        Info = info;
        StatementTimeoutSeconds = SessionPolicy.TimeoutSeconds(info);
        _tunnel = tunnel;
        try
        {
            _settings = Read(engine, info, secret, awsCredentials, clock ?? TimeProvider.System);
        }
        catch (SearchConfigurationException e)
        {
            // Held, not thrown: see the class remarks.
            _failure = e;
        }
    }

    /// <summary>A factory over an API someone else made — the fake, in the executor's and reader's tests. No
    /// scheme, sign-in or dialling is applied (the fake is the whole transport); the redirect and 401 rules of
    /// <see cref="SendAsync"/> still are. The API comes first so a literal <c>null</c> secret in the other
    /// constructor can never resolve to this one.</summary>
    internal SearchConnectionFactory(ISearchApi api, SearchEngine engine, ConnectionInfo info)
    {
        Engine = engine;
        Info = info;
        StatementTimeoutSeconds = SessionPolicy.TimeoutSeconds(info);
        _injected = api;
    }

    /// <summary>Which server this connection speaks to.</summary>
    internal SearchEngine Engine { get; }

    /// <summary>The connection's record as it was handed over — the real host, also when tunnelled.</summary>
    internal ConnectionInfo Info { get; }

    /// <summary>The connection's statement timeout in seconds (<see cref="SessionPolicy.TimeoutSeconds"/>);
    /// <see cref="SessionPolicy.NoTimeout"/> when none. A client-side cancel of the request (§1.9a).</summary>
    internal int StatementTimeoutSeconds { get; }

    /// <summary>How requests sign in; null when a setting was refused (or the API was handed in).</summary>
    internal SearchAuthMethod? AuthMethod => _settings?.Method;

    /// <summary>The scheme, host and port requests are addressed to, once the first request has settled it;
    /// null before that, for a refused setting and for a handed-in API. For tests and diagnostics.</summary>
    internal Uri? BaseUri => (_api as HttpSearchApi)?.BaseUri;

    /// <summary>
    /// <c>GET /</c>: 2xx is true. A server that does not serve the root (404 / 405, as OpenSearch Serverless
    /// documents) is judged by <c>GET /_cat/indices?format=json&amp;h=index</c> instead. Every failure throws
    /// its sentence — a refused setting as <see cref="SearchConfigurationException"/>, a 401 as
    /// <see cref="SearchSignInException"/>, any other status as <see cref="SearchServerException"/>, no answer
    /// as <see cref="SearchTransportException"/> — so the dialog's Test shows it.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        var root = await SendAsync(HttpMethod.Get, "/", null, RootResponseLimitBytes, ct).ConfigureAwait(false);
        if (root.IsSuccess)
        {
            return true;
        }

        if (root.StatusCode is 404 or 405)
        {
            var indices = await SendAsync(
                HttpMethod.Get, "/_cat/indices?format=json&h=index", null, DefaultResponseLimitBytes, ct)
                .ConfigureAwait(false);
            if (indices.IsSuccess)
            {
                return true;
            }

            throw new SearchServerException(
                indices.StatusCode, SearchErrors.ToQueryError(indices.StatusCode, indices.Body, null, null));
        }

        throw new SearchServerException(root.StatusCode, SearchErrors.ToQueryError(root.StatusCode, root.Body, null, null));
    }

    /// <summary>
    /// Send one request and return the answer — the call the executor and the metadata reader make. A 2xx and
    /// any other status the caller words (<see cref="SearchErrors.ToQueryError(int, byte[], string?, string?)"/>)
    /// come back as a <see cref="SearchResponse"/>. Thrown instead: a refused setting
    /// (<see cref="SearchConfigurationException"/>, nothing sent), a 401 (<see cref="SearchSignInException"/>),
    /// a 3xx (<see cref="SearchServerException"/>, not followed), no answer (<see cref="SearchTransportException"/>,
    /// scrubbed), an answer over <paramref name="maxResponseBytes"/>
    /// (<see cref="SearchResponseTooLargeException"/>). A cancel through <paramref name="ct"/> is an
    /// <see cref="OperationCanceledException"/>, as it came.
    /// </summary>
    internal async Task<SearchResponse> SendAsync(
        HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null)
        {
            throw _failure;
        }

        if (_injected is not null)
        {
            return Judge(await _injected.SendAsync(method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false));
        }

        if (_api is { } settled)
        {
            return Judge(await TransportAsync(settled, method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false));
        }

        // The first request builds the client — and, under Prefer, decides the scheme. One at a time, so two
        // first requests do not each probe.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_api is { } built)
            {
                return Judge(await TransportAsync(built, method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false));
            }

            return Judge(await FirstSendAsync(method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _api?.Dispose();
        _injected?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whether a server certificate is accepted under <paramref name="mode"/>. Pure, so the one place validation
    /// is relaxed is testable without a certificate authority: <see cref="TlsMode.Require"/> and
    /// <see cref="TlsMode.Prefer"/> accept anything; <see cref="TlsMode.VerifyCa"/> accepts a trusted chain whose
    /// only fault is the host name; every other mode accepts only a certificate with no fault at all.
    /// </summary>
    internal static bool CertificateAccepted(TlsMode mode, SslPolicyErrors errors) => mode switch
    {
        TlsMode.Require or TlsMode.Prefer => true,
        TlsMode.VerifyCa => (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None,
        _ => errors == SslPolicyErrors.None,
    };

    // ---- Reading the settings --------------------------------------------------------------------------

    /// <summary>Everything decided from the record, before any request: where to send, how to sign in.</summary>
    private sealed record SearchSettings(
        string UriHost, int Port, TlsMode Mode, bool Loopback, SearchAuthMethod Method, ISearchRequestAuth Auth);

    /// <summary>Read and check the record. Pure — no network. Throws the sentence for a refused setting.</summary>
    private static SearchSettings Read(
        SearchEngine engine, ConnectionInfo info, string? secret, IAwsCredentialSource? awsCredentials, TimeProvider clock)
    {
        var host = HostOf(info.Host);
        var port = info.Port is >= 1 and <= 65535 ? info.Port : DefaultPort;
        var mode = TlsPolicy.Resolve(info);
        var loopback = TlsPolicy.IsLoopback(info.Host);
        var method = MethodOf(engine, info);

        if (method == SearchAuthMethod.AwsSigV4 && mode == TlsMode.Disable && !loopback)
        {
            throw new SearchConfigurationException(SearchErrors.AwsPlainHttpSentence);
        }

        var auth = method == SearchAuthMethod.AwsSigV4
            ? SignerOf(engine, info, awsCredentials, clock)
            : AuthOf(method, info, secret);

        return new SearchSettings(host, port, mode, loopback, method, auth);
    }

    /// <summary>The SigV4 signer for an AWS-profile connection: region and service settled now (a missing region
    /// fails the connect here), credentials asked for per request. The password box is never read.</summary>
    private static AwsSigV4SearchAuth SignerOf(
        SearchEngine engine, ConnectionInfo info, IAwsCredentialSource? awsCredentials, TimeProvider clock)
    {
        var region = ResolveRegion(info);
        var service = ServiceFor(info.Host);
        var profile = string.IsNullOrWhiteSpace(info.User) ? null : info.User.Trim();

        return new AwsSigV4SearchAuth(
            engine, profile, awsCredentials ?? new AwsProfileCredentialSource(engine, profile), region, service, clock);
    }

    /// <summary>
    /// The SigV4 signing name for <paramref name="host"/>: <c>aoss</c> (OpenSearch Serverless) when the host holds
    /// <c>.aoss.</c> or <c>.aoss-fips.</c>, otherwise <c>es</c> (Amazon OpenSearch Service, and anything else —
    /// a custom endpoint names no service). Case-insensitive. Pure.
    /// </summary>
    internal static string ServiceFor(string? host)
    {
        var name = $".{host?.Trim().TrimEnd('.') ?? ""}.";

        return name.Contains(".aoss.", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".aoss-fips.", StringComparison.OrdinalIgnoreCase)
            ? "aoss"
            : "es";
    }

    /// <summary>
    /// The signing region: <c>Options["Region"]</c> when set (trimmed, lower-cased; refused unless it is letters,
    /// digits and hyphens), else read from the host (<see cref="RegionFromHost"/>), else the connect is refused
    /// with <see cref="SearchErrors.AwsRegionMissingSentence"/>. Pure.
    /// </summary>
    internal static string ResolveRegion(ConnectionInfo info)
    {
        var key = info.Options.Keys.FirstOrDefault(k => string.Equals(k, RegionOption, StringComparison.OrdinalIgnoreCase));
        var option = key is null ? null : info.Options[key]?.Trim();
        if (!string.IsNullOrEmpty(option))
        {
            return IsRegionName(option)
                ? option.ToLowerInvariant()
                : throw new SearchConfigurationException(SearchErrors.AwsRegionInvalidSentence);
        }

        return RegionFromHost(info.Host)
            ?? throw new SearchConfigurationException(SearchErrors.AwsRegionMissingSentence);
    }

    /// <summary>
    /// The region an Amazon endpoint name carries, or null: <c>….&lt;region&gt;.es.amazonaws.com</c> (a domain),
    /// <c>….&lt;region&gt;.aoss.amazonaws.com</c> (Serverless), <c>….aoss.&lt;region&gt;.on.aws</c> and
    /// <c>….aoss-fips.&lt;region&gt;.on.aws</c> (Serverless, newer names). Case-insensitive; a trailing dot is
    /// ignored. A custom endpoint, an IP address or any other name carries none. Pure.
    /// </summary>
    internal static string? RegionFromHost(string? host)
    {
        var labels = (host?.Trim().TrimEnd('.') ?? "").ToLowerInvariant().Split('.');
        if (labels.Length < 4)
        {
            return null;
        }

        string? region = null;
        if (labels[^1] == "com" && labels[^2] == "amazonaws" && labels[^3] is "es" or "aoss")
        {
            region = labels[^4];
        }
        else if (labels[^1] == "aws" && labels[^2] == "on" && labels[^4] is "aoss" or "aoss-fips")
        {
            region = labels[^3];
        }

        return region is not null && IsRegionName(region) ? region : null;
    }

    /// <summary>A region name as SigV4's credential scope can carry it: letters, digits and hyphens, starting
    /// with a letter, at least one hyphen (<c>eu-west-1</c>, <c>us-gov-west-1</c>).</summary>
    private static bool IsRegionName(string value)
        => value.Length is >= 4 and <= 32
           && char.IsAsciiLetter(value[0])
           && value.Contains('-')
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>The host as a URI names it: trimmed, an IPv6 literal bracketed. A value carrying a scheme, a
    /// path, a port, user-info or whitespace is refused (<see cref="SearchErrors.HostOnlySentence"/>).</summary>
    private static string HostOf(string? value)
    {
        var host = value?.Trim() ?? "";
        if (host.Length == 0)
        {
            throw new SearchConfigurationException(SearchErrors.HostMissingSentence);
        }

        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{bare}]";
        }

        if (host.IndexOfAny(['/', '\\', '?', '#', '@', ':', '[', ']']) >= 0
            || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate($"http://{host}:{DefaultPort}/", UriKind.Absolute, out var probe)
            || !string.Equals(probe.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            throw new SearchConfigurationException(SearchErrors.HostOnlySentence);
        }

        return host;
    }

    private static SearchAuthMethod MethodOf(SearchEngine engine, ConnectionInfo info)
    {
        switch (info.CredentialKind)
        {
            case CredentialKind.AwsProfile when engine == SearchEngine.OpenSearch:
                return SearchAuthMethod.AwsSigV4;
            case CredentialKind.StoredPassword or CredentialKind.Prompt:
                break;
            default:
                throw new SearchConfigurationException(SearchErrors.UnsupportedCredentialSentence(engine, info.CredentialKind));
        }

        if (engine == SearchEngine.OpenSearch)
        {
            return SearchAuthMethod.Basic;
        }

        var key = info.Options.Keys.FirstOrDefault(
            k => string.Equals(k, AuthenticationOption, StringComparison.OrdinalIgnoreCase));
        var choice = key is null ? null : info.Options[key]?.Trim();
        if (string.IsNullOrEmpty(choice) || string.Equals(choice, AuthenticationPassword, StringComparison.OrdinalIgnoreCase))
        {
            return SearchAuthMethod.Basic;
        }
        if (string.Equals(choice, AuthenticationApiKey, StringComparison.OrdinalIgnoreCase))
        {
            return SearchAuthMethod.ApiKey;
        }

        throw new SearchConfigurationException(SearchErrors.UnknownAuthenticationSentence(choice));
    }

    /// <summary>The request authentication for <paramref name="method"/>. Basic with a blank user and a blank
    /// secret is no header at all (a cluster with security off); the API key is trimmed (no encoded key holds
    /// whitespace, a pasted one often ends in a line break) and refused blank or holding a control character.</summary>
    private static ISearchRequestAuth AuthOf(SearchAuthMethod method, ConnectionInfo info, string? secret)
    {
        switch (method)
        {
            case SearchAuthMethod.Basic:
                var user = info.User ?? "";
                return string.IsNullOrEmpty(user.Trim()) && string.IsNullOrEmpty(secret)
                    ? NoSearchAuth.Instance
                    : new BasicSearchAuth(user.Trim(), secret ?? "");
            case SearchAuthMethod.ApiKey:
                var key = secret?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    throw new SearchConfigurationException(SearchErrors.ApiKeyMissingSentence);
                }
                if (key.Any(char.IsControl))
                {
                    throw new SearchConfigurationException(SearchErrors.ControlCharacterSentence);
                }
                return new ApiKeySearchAuth(key);
            case SearchAuthMethod.AwsSigV4:
                // Signed by SignerOf, never here: failing closed beats sending an AWS-profile request unsigned.
                throw new InvalidOperationException("An AWS-profile connection is signed by the SigV4 signer.");
            default:
                return NoSearchAuth.Instance;
        }
    }

    // ---- Sending -----------------------------------------------------------------------------------------

    /// <summary>The first request: build the client for the settled scheme and send — or, under Prefer, try
    /// <c>https://</c> and fall back to <c>http://</c> only after a failed TLS handshake.</summary>
    private async Task<SearchResponse> FirstSendAsync(
        HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
    {
        var settings = _settings!;
        if (settings.Mode != TlsMode.Prefer)
        {
            _api = Build(settings, settings.Mode == TlsMode.Disable ? Uri.UriSchemeHttp : Uri.UriSchemeHttps);
            return await TransportAsync(_api, method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false);
        }

        var secure = Build(settings, Uri.UriSchemeHttps);
        try
        {
            var answer = await TransportAsync(secure, method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false);
            _api = secure;
            return answer;
        }
        catch (SearchTransportException e) when (e.Error == HttpRequestError.SecureConnectionError)
        {
            // The handshake failed before any HTTP was exchanged, so nothing was sent and resending is safe.
            secure.Dispose();
        }
        catch
        {
            // A refused connection, a timeout, a cancel: no reason to fall back, and nothing decided.
            secure.Dispose();
            throw;
        }

        if (settings.Method == SearchAuthMethod.AwsSigV4 && !settings.Loopback)
        {
            throw new SearchConfigurationException(SearchErrors.AwsNoTlsSentence);
        }

        var plain = Build(settings, Uri.UriSchemeHttp);
        try
        {
            var answer = await TransportAsync(plain, method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false);
            _api = plain;
            return answer;
        }
        catch
        {
            plain.Dispose();
            throw;
        }
    }

    /// <summary>One call through <paramref name="api"/>, with a transport failure turned into a scrubbed
    /// <see cref="SearchTransportException"/>. A cancel through <paramref name="ct"/> passes as it came; a
    /// cancel nobody asked for is the handler's connect timeout.</summary>
    private static async Task<SearchResponse> TransportAsync(
        ISearchApi api, HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
    {
        try
        {
            return await api.SendAsync(method, pathAndQuery, jsonBody, maxResponseBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            throw Transport(e, null, $"No connection could be made within {ConnectTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            throw Transport(e, e.HttpRequestError, null);
        }
        catch (IOException e)
        {
            throw Transport(e, null, null);
        }
    }

    private static SearchTransportException Transport(Exception e, HttpRequestError? error, string? lead)
    {
        var described = SearchErrors.DescribeTransport(e);
        var message = lead is null ? described : $"{lead} {described}";
        var clean = Chain(e).All(x => string.Equals(SearchErrors.Scrub(x.Message), x.Message, StringComparison.Ordinal));

        return new SearchTransportException(message, error, clean ? e : null);
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>The rules every answer passes: a 3xx is not followed, a 401 is the sign-in sentence.</summary>
    private SearchResponse Judge(SearchResponse response)
    {
        if (response.StatusCode is >= 300 and < 400)
        {
            throw new SearchServerException(
                response.StatusCode,
                new QueryError(SearchErrors.RedirectSentence(response.StatusCode), SearchErrors.RedirectCode, null));
        }
        if (response.StatusCode == 401)
        {
            throw new SearchSignInException(Engine, _settings?.Method ?? SearchAuthMethod.Basic, Info.User);
        }

        return response;
    }

    /// <summary>The client for one scheme: no redirects, the connect timeout, an infinite request timeout, the
    /// certificate rule of the mode, and — with a tunnel — the dialler and no proxy.</summary>
    private HttpSearchApi Build(SearchSettings settings, string scheme)
    {
        var mode = settings.Mode;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) => CertificateAccepted(mode, errors),
            },
        };

        if (_tunnel is { } tunnel)
        {
            handler.UseProxy = false;
            handler.ConnectCallback = async (_, cancel) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(tunnel.Host, tunnel.Port, cancel).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Bearing");

        var baseUri = new Uri($"{scheme}://{settings.UriHost}:{settings.Port}/");

        return new HttpSearchApi(client, baseUri, settings.Auth);
    }
}
