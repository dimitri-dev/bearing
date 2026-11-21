using System.Net.Http.Headers;
using System.Text;

namespace Bearing.Data.Search;

/// <summary>
/// Which of the two servers a connection speaks to. <c>Data</c>'s own copy: the <c>Sql</c> project has one of
/// the same name for its dialects, and the two projects may not reference each other (§2.2).
/// </summary>
internal enum SearchEngine
{
    Elasticsearch,
    OpenSearch,
}

/// <summary>
/// What one request came back with: the HTTP status, the body as the UTF-8 bytes the server sent, and the raw
/// value of every <c>Warning</c> response header, in the order they arrived (empty when there were none).
/// <para>
/// A non-2xx status is an answer, not an exception: the caller knows which statement and which language it
/// sent, and words the failure itself (<see cref="SearchErrors.ToQueryError(int, byte[], string?, string?)"/>).
/// The warnings are kept exactly as the header carried them (an Elasticsearch warning reads
/// <c>299 Elasticsearch-9.x.y-hash "No limit defined, …"</c>); pulling the quoted text out is the protocol's job.
/// </para>
/// </summary>
internal sealed record SearchResponse(int StatusCode, byte[] Body, IReadOnlyList<string> Warnings)
{
    /// <summary>A 2xx status.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The body decoded as UTF-8 — what both servers send for JSON.</summary>
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// The one HTTP call Bearing makes to Elasticsearch or OpenSearch — a seam so the executor and the metadata
/// reader are tested against a hand-rolled fake (§4.1: no mocking library), the arrangement
/// <see cref="DynamoDb.IDynamoDbApi"/> has for DynamoDB.
/// <para>
/// <b>One door for everything.</b> A read, a cursor page, a catalog request and anything else go through
/// <see cref="SendAsync"/>; what may be sent is decided before it is called (the write guard,
/// <c>WriteRefusal</c>, the external policy), never by this seam.
/// </para>
/// </summary>
internal interface ISearchApi : IDisposable
{
    /// <summary>
    /// Send <paramref name="method"/> to <paramref name="pathAndQuery"/> (already escaped, starting with
    /// <c>/</c>) with <paramref name="jsonBody"/> as an <c>application/json</c> body when it is not null, and
    /// return what came back. A non-2xx status is returned; a transport failure (no connection, a failed TLS
    /// handshake) throws. A body longer than <paramref name="maxResponseBytes"/> is not buffered: the call
    /// throws <see cref="SearchResponseTooLargeException"/> as soon as that is known.
    /// </summary>
    Task<SearchResponse> SendAsync(
        HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct);
}

/// <summary>
/// How a request proves who sent it, applied to the finished <see cref="HttpRequestMessage"/> just before it
/// is sent, with the exact body bytes it carries — the shape a request signer needs (a SigV4 signature covers
/// the body's hash), so header authentication and signing are one seam.
/// <para>
/// An implementation may add headers and must not change the method, the URI or the body. It must never put a
/// secret in an exception message (§1.1).
/// </para>
/// </summary>
internal interface ISearchRequestAuth
{
    ValueTask ApplyAsync(HttpRequestMessage request, ReadOnlyMemory<byte> body, CancellationToken ct);
}

/// <summary>No <c>Authorization</c> header — a cluster with security off, chosen by a blank user and a blank
/// password.</summary>
internal sealed class NoSearchAuth : ISearchRequestAuth
{
    public static readonly NoSearchAuth Instance = new();

    private NoSearchAuth()
    {
    }

    public ValueTask ApplyAsync(HttpRequestMessage request, ReadOnlyMemory<byte> body, CancellationToken ct)
        => ValueTask.CompletedTask;
}

/// <summary>
/// <c>Authorization: Basic base64(user:password)</c>, UTF-8 — the "User and password" choice. Base64 cannot
/// carry a line break, so no value of either part can split the header.
/// </summary>
internal sealed class BasicSearchAuth : ISearchRequestAuth
{
    private readonly AuthenticationHeaderValue _header;

    public BasicSearchAuth(string user, string password)
        => _header = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    public ValueTask ApplyAsync(HttpRequestMessage request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        request.Headers.Authorization = _header;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>Authorization: ApiKey &lt;key&gt;</c> — Elasticsearch's API key, in the encoded form Kibana and the
/// create-key response show. No user name is sent. The key is checked for control characters before this is
/// built (<see cref="SearchConnectionFactory"/>), so it cannot split the header.
/// </summary>
internal sealed class ApiKeySearchAuth : ISearchRequestAuth
{
    private readonly AuthenticationHeaderValue _header;

    public ApiKeySearchAuth(string apiKey) => _header = new AuthenticationHeaderValue("ApiKey", apiKey);

    public ValueTask ApplyAsync(HttpRequestMessage request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        request.Headers.Authorization = _header;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="ISearchApi"/> over an <see cref="HttpClient"/> the factory built (scheme, certificate rule, no
/// redirects, the tunnel's dialler). Holds no decision of its own: it builds the request, lets the
/// <see cref="ISearchRequestAuth"/> sign it, sends it, and reads the answer up to the ceiling. Falling back from
/// TLS, refusing a redirect and wording a 401 are the factory's (<see cref="SearchConnectionFactory.SendAsync"/>).
/// </summary>
internal sealed class HttpSearchApi : ISearchApi
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly ISearchRequestAuth _auth;

    public HttpSearchApi(HttpClient client, Uri baseUri, ISearchRequestAuth auth)
    {
        _client = client;
        _baseUri = baseUri;
        _auth = auth;
    }

    /// <summary>The scheme, host and port requests are addressed to — the connection's real host, also when the
    /// TCP connection is dialled through a tunnel.</summary>
    public Uri BaseUri => _baseUri;

    public async Task<SearchResponse> SendAsync(
        HttpMethod method, string pathAndQuery, byte[]? jsonBody, long maxResponseBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, pathAndQuery));
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = Json;
        }
        await _auth.ApplyAsync(request, jsonBody ?? ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var warnings = new List<string>();
        if (response.Headers.NonValidated.TryGetValues("Warning", out var values))
        {
            foreach (var value in values)
            {
                warnings.Add(value);
            }
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maxResponseBytes)
        {
            throw new SearchResponseTooLargeException(maxResponseBytes);
        }

        var body = await ReadBoundedAsync(response.Content, maxResponseBytes, ct).ConfigureAwait(false);

        return new SearchResponse((int)response.StatusCode, body, warnings);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The body, read in chunks and refused the moment it passes the ceiling — a length the server
    /// did not declare (chunked, compressed) is held to the same limit.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new SearchResponseTooLargeException(maxBytes);
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
