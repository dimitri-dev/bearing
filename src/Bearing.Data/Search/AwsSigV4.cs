using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Bearing.Data.Aws;
using Bearing.Data.DynamoDb;

namespace Bearing.Data.Search;

/// <summary>
/// AWS Signature Version 4, by hand — the signature an Amazon OpenSearch Service domain (<c>es</c>) or OpenSearch
/// Serverless collection (<c>aoss</c>) expects on every request. Pure and deterministic: request parts,
/// credentials and the clock's reading in, headers out. The SDK's own <c>AWS4Signer</c> is deliberately not used
/// (it is public only in an <c>Internal</c> namespace and needs the SDK's request type).
/// <para>
/// <b>The steps</b> follow AWS's specification and are exposed so tests pin each one against AWS's published
/// vectors: the canonical request (<see cref="CanonicalRequest"/>: method, URI-encoded path, sorted and encoded
/// query, lower-cased sorted headers with trimmed values, the signed-header list, the payload hash), the string to
/// sign (<see cref="StringToSign"/>), and the HMAC-SHA256 chain from the secret key through date, region, service
/// and <c>aws4_request</c> (<see cref="Signature"/>).
/// </para>
/// <para>
/// <b>The path is encoded as it goes on the wire, once more</b> (<see cref="CanonicalUri"/>): every byte but the
/// unreserved characters and <c>/</c> is percent-encoded, so an already-escaped path (<c>%20</c>) is encoded
/// again (<c>%2520</c>) — the "double encoding" AWS specifies for every service but S3.
/// </para>
/// <para>
/// <b>Secrets.</b> The secret key and the session token are parameters only — never held in a field of this
/// class, never put in an exception message or a log line. The types that carry a token or a signature out
/// (<see cref="AwsSigV4Headers"/>, <see cref="AwsSigV4Steps"/>) print nothing of them from <c>ToString()</c>.
/// </para>
/// </summary>
internal static class AwsSigV4
{
    internal const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The hex SHA-256 of an empty payload — what a request without a body signs.</summary>
    internal const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    internal const string DateHeader = "x-amz-date";
    internal const string ContentHashHeader = "x-amz-content-sha256";
    internal const string SecurityTokenHeader = "x-amz-security-token";

    /// <summary>
    /// Bearing's own signature for one request: <c>host</c>, <c>x-amz-content-sha256</c> (always sent and signed —
    /// OpenSearch Serverless requires it), <c>x-amz-date</c> and, when there is a session token,
    /// <c>x-amz-security-token</c> (sent <b>and</b> signed) are the signed headers. Nothing else is signed: the
    /// HTTP stack adds headers of its own after this runs (<c>Accept-Encoding</c>, <c>Content-Length</c>, the
    /// client's defaults), and a signed header it changed would break the signature.
    /// </summary>
    /// <param name="method">The HTTP method, as sent.</param>
    /// <param name="uri">The request URI as sent — its escaped path and query are signed.</param>
    /// <param name="host">The <c>Host</c> header value the request carries (the real host, also through a tunnel).</param>
    /// <param name="payload">The exact body bytes; empty for none.</param>
    internal static AwsSigV4Headers Sign(
        string method,
        Uri uri,
        string host,
        ReadOnlySpan<byte> payload,
        string region,
        string service,
        string accessKey,
        string secretKey,
        string? sessionToken,
        DateTime utcNow)
    {
        var amzDate = AmzDate(utcNow);
        var payloadHash = PayloadHash(payload);
        var token = string.IsNullOrEmpty(sessionToken) ? null : sessionToken;

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", host),
            new(ContentHashHeader, payloadHash),
            new(DateHeader, amzDate),
        };
        if (token is not null)
        {
            headers.Add(new(SecurityTokenHeader, token));
        }

        var steps = Compute(method, uri.AbsolutePath, uri.Query.TrimStart('?'), headers, payloadHash,
            region, service, accessKey, secretKey, utcNow);

        return new AwsSigV4Headers(amzDate, payloadHash, token, steps.Authorization, steps.SignedHeaders);
    }

    /// <summary>
    /// The general signer: the canonical request over exactly <paramref name="headersToSign"/> (which must hold
    /// <c>host</c> and <c>x-amz-date</c> as the request carries them), the string to sign, the signature and the
    /// <c>Authorization</c> value. <paramref name="path"/> is the path as it goes on the wire (unencoded
    /// characters are encoded once; an escape is encoded again); <paramref name="query"/> is without its <c>?</c>.
    /// </summary>
    internal static AwsSigV4Steps Compute(
        string method,
        string path,
        string query,
        IEnumerable<KeyValuePair<string, string>> headersToSign,
        string payloadHash,
        string region,
        string service,
        string accessKey,
        string secretKey,
        DateTime utcNow)
    {
        var amzDate = AmzDate(utcNow);
        var date = amzDate[..8];
        var scope = $"{date}/{region}/{service}/aws4_request";

        var (canonicalHeaders, signedHeaders) = CanonicalHeaders(headersToSign);
        var canonicalRequest = CanonicalRequest(method, path, query, canonicalHeaders, signedHeaders, payloadHash);
        var stringToSign = StringToSign(amzDate, scope, canonicalRequest);
        var signature = Signature(secretKey, date, region, service, stringToSign);
        var authorization = $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        return new AwsSigV4Steps(canonicalRequest, stringToSign, signedHeaders, signature, authorization);
    }

    /// <summary>
    /// The canonical request: method, canonical URI, canonical query, the canonical header lines (each ending in a
    /// line break), the signed-header list and the payload hash, joined by line breaks.
    /// </summary>
    internal static string CanonicalRequest(
        string method, string path, string query, string canonicalHeaders, string signedHeaders, string payloadHash)
        => string.Join('\n',
            method.ToUpperInvariant(),
            CanonicalUri(path),
            CanonicalQuery(query),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

    /// <summary>The string to sign: algorithm, request date, credential scope and the hex SHA-256 of the
    /// canonical request.</summary>
    internal static string StringToSign(string amzDate, string scope, string canonicalRequest)
        => string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

    /// <summary>The hex signature: HMAC-SHA256 of <paramref name="stringToSign"/> under the key derived from the
    /// secret through the date (<c>yyyyMMdd</c>), the region, the service and <c>aws4_request</c>. Every derived
    /// key is cleared once used.</summary>
    internal static string Signature(string secretKey, string date, string region, string service, string stringToSign)
    {
        var secret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        var dateKey = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        var signingKey = HMACSHA256.HashData(serviceKey, "aws4_request"u8);
        try
        {
            return Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(dateKey);
            CryptographicOperations.ZeroMemory(regionKey);
            CryptographicOperations.ZeroMemory(serviceKey);
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <summary>The canonical URI: <paramref name="path"/> with every byte of its UTF-8 form percent-encoded except
    /// the unreserved characters and <c>/</c>; <c>/</c> for an empty path.</summary>
    internal static string CanonicalUri(string path)
        => string.IsNullOrEmpty(path) ? "/" : Encode(path, keepSlash: true);

    /// <summary>
    /// The canonical query: each <c>name=value</c> pair (a pair without <c>=</c> has an empty value) decoded from
    /// its escapes, encoded strictly, and sorted by name then value in ordinal order, joined by <c>&amp;</c>.
    /// </summary>
    internal static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return "";
        }

        var pairs = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var equals = part.IndexOf('=');
                var name = equals < 0 ? part : part[..equals];
                var value = equals < 0 ? "" : part[(equals + 1)..];

                return (Name: Encode(Uri.UnescapeDataString(name), keepSlash: false),
                    Value: Encode(Uri.UnescapeDataString(value), keepSlash: false));
            })
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}");

        return string.Join('&', pairs);
    }

    /// <summary>
    /// The canonical header lines and the signed-header list: names lower-cased and sorted in ordinal order,
    /// values trimmed with inner runs of spaces collapsed to one, each line <c>name:value</c> and a line break;
    /// a name given twice has its values joined by commas in the order given.
    /// </summary>
    internal static (string CanonicalHeaders, string SignedHeaders) CanonicalHeaders(
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        var grouped = headers
            .GroupBy(header => header.Key.Trim().ToLowerInvariant())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Value: string.Join(',', group.Select(header => TrimValue(header.Value)))))
            .ToList();

        var lines = new StringBuilder();
        foreach (var (name, value) in grouped)
        {
            lines.Append(name).Append(':').Append(value).Append('\n');
        }

        return (lines.ToString(), string.Join(';', grouped.Select(header => header.Name)));
    }

    /// <summary>The hex SHA-256 of the payload — the <c>x-amz-content-sha256</c> value and the canonical
    /// request's last line.</summary>
    internal static string PayloadHash(ReadOnlySpan<byte> payload) => Hex(SHA256.HashData(payload));

    /// <summary>The request date in SigV4's basic ISO 8601 form, <c>yyyyMMddTHHmmssZ</c>, in UTC.</summary>
    internal static string AmzDate(DateTime utcNow)
        => (utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow)
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string TrimValue(string value)
    {
        var trimmed = value.Trim();
        var collapsed = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var c in trimmed)
        {
            if (c == ' ')
            {
                if (!lastWasSpace)
                {
                    collapsed.Append(c);
                }
                lastWasSpace = true;
                continue;
            }
            collapsed.Append(c);
            lastWasSpace = false;
        }

        return collapsed.ToString();
    }

    /// <summary>SigV4's URI encoding: unreserved characters (<c>A–Z a–z 0–9 - _ . ~</c>) as they are, everything
    /// else as <c>%XX</c> of its UTF-8 bytes in upper-case hex; <c>/</c> kept only in a path.</summary>
    private static string Encode(string text, bool keepSlash)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}

/// <summary>
/// The headers <see cref="AwsSigV4.Sign"/> adds to a request. Carries the session token and the signature, so
/// <see cref="ToString"/> names the signed headers only.
/// </summary>
internal sealed class AwsSigV4Headers
{
    internal AwsSigV4Headers(string amzDate, string contentSha256, string? securityToken, string authorization, string signedHeaders)
    {
        AmzDate = amzDate;
        ContentSha256 = contentSha256;
        SecurityToken = securityToken;
        Authorization = authorization;
        SignedHeaders = signedHeaders;
    }

    /// <summary>The <c>x-amz-date</c> value.</summary>
    public string AmzDate { get; }

    /// <summary>The <c>x-amz-content-sha256</c> value.</summary>
    public string ContentSha256 { get; }

    /// <summary>The <c>x-amz-security-token</c> value; null when the credentials had no session token.</summary>
    public string? SecurityToken { get; }

    /// <summary>The <c>Authorization</c> value: algorithm, credential scope, signed headers, signature.</summary>
    public string Authorization { get; }

    /// <summary>The <c>;</c>-separated list of the headers the signature covers.</summary>
    public string SignedHeaders { get; }

    /// <summary>Every header to add, in the order they are added.</summary>
    public IEnumerable<(string Name, string Value)> All
    {
        get
        {
            yield return (AwsSigV4.DateHeader, AmzDate);
            yield return (AwsSigV4.ContentHashHeader, ContentSha256);
            if (SecurityToken is not null)
            {
                yield return (AwsSigV4.SecurityTokenHeader, SecurityToken);
            }
            yield return ("Authorization", Authorization);
        }
    }

    public override string ToString() => $"SigV4 headers (signed: {SignedHeaders})";
}

/// <summary>
/// The intermediate results of one signature, for tests and diagnostics. The canonical request holds the value of
/// every signed header — a session token among them — so <see cref="ToString"/> prints none of it.
/// </summary>
internal sealed class AwsSigV4Steps
{
    internal AwsSigV4Steps(string canonicalRequest, string stringToSign, string signedHeaders, string signature, string authorization)
    {
        CanonicalRequest = canonicalRequest;
        StringToSign = stringToSign;
        SignedHeaders = signedHeaders;
        Signature = signature;
        Authorization = authorization;
    }

    public string CanonicalRequest { get; }

    public string StringToSign { get; }

    public string SignedHeaders { get; }

    public string Signature { get; }

    public string Authorization { get; }

    public override string ToString() => $"SigV4 steps (signed: {SignedHeaders})";
}

/// <summary>
/// Where an OpenSearch connection's AWS credentials come from — a seam so the signer's round trip is tested with
/// a fake (§4.1), and the real SDK path (<see cref="AwsProfileCredentialSource"/>) only against a temporary
/// profiles file, never the user's own.
/// </summary>
internal interface IAwsCredentialSource
{
    /// <summary>The credentials to sign one request with — asked for every request, so a refreshed session (SSO,
    /// an assumed role) is used as soon as the SDK has it. The caller keeps the answer for that request only.</summary>
    Task<ImmutableCredentials> GetAsync(CancellationToken ct);
}

/// <summary>
/// The AWS SDK's credentials for one profile (blank = the SDK's default chain). The SDK's
/// <see cref="AWSCredentials"/> object is resolved once, at the first request — so a refused profile fails the
/// connect, not the factory's constructor — and asked for its keys on every request
/// (<see cref="AWSCredentials.GetCredentialsAsync"/>), so the SDK's own refresh is honoured. The keys it returns
/// are never kept here.
/// <para>
/// A failed lookup is a <see cref="SearchSignInException"/> built from the profile name and the problem alone;
/// a failure is not remembered, so fixing the profile and trying again works without reconnecting.
/// </para>
/// </summary>
internal sealed class AwsProfileCredentialSource : IAwsCredentialSource
{
    private readonly SearchEngine _engine;
    private readonly string? _profile;
    private readonly string? _profilesLocation;
    private AWSCredentials? _credentials;

    /// <param name="profile">The profile name, trimmed; null or blank for the SDK's default chain.</param>
    /// <param name="profilesLocation">A credentials file to read profiles from instead of the SDK's own
    /// locations. For tests only: nothing in the app passes it.</param>
    internal AwsProfileCredentialSource(SearchEngine engine, string? profile, string? profilesLocation = null)
    {
        _engine = engine;
        _profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        _profilesLocation = profilesLocation;
    }

    /// <summary>The profile, trimmed; null for the default chain.</summary>
    internal string? Profile => _profile;

    public async Task<ImmutableCredentials> GetAsync(CancellationToken ct)
    {
        var credentials = Volatile.Read(ref _credentials);
        if (credentials is null)
        {
            var resolved = await ResolveAsync(ct).ConfigureAwait(false);
            credentials = Interlocked.CompareExchange(ref _credentials, resolved, null) ?? resolved;
        }

        // The SDK's call takes no token; a cancel abandons the wait, the SDK finishes on its own.
        return await credentials.GetCredentialsAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<AWSCredentials> ResolveAsync(CancellationToken ct)
    {
        if (_profile is null)
        {
            return await AwsProfileCredentials.DefaultChainAsync(ct).ConfigureAwait(false);
        }

        if (!AwsProfileCredentials.TryNamed(_profile, _profilesLocation, out var credentials, out var problem))
        {
            throw new SearchSignInException(_engine, _profile, problem);
        }

        return credentials;
    }
}

/// <summary>
/// The SigV4 <see cref="ISearchRequestAuth"/>: asks the credential source for this request's keys, signs the
/// request as it will be sent (the real host — also through an SSH tunnel — and the exact body bytes), and adds
/// the headers. The keys live in this call only.
/// <para>
/// <b>A credential failure is a sign-in failure, said from the profile name alone</b>: a
/// <see cref="SearchSignInException"/> from the source passes as it is; anything else the SDK throws while
/// producing credentials (an expired SSO session, a role it could not assume — <see cref="DynamoDbErrors.IsSignInFailure"/>
/// recognises them — or anything it did not explain) becomes one too, with
/// <see cref="AwsSignInProblem.ProfileNotFound"/> only when the SDK said so. The SDK's own text is never kept: not
/// in the message, not as an inner exception. A cancel through the token passes as it came.
/// </para>
/// </summary>
internal sealed class AwsSigV4SearchAuth : ISearchRequestAuth
{
    private readonly SearchEngine _engine;
    private readonly string? _profile;
    private readonly IAwsCredentialSource _source;
    private readonly TimeProvider _clock;

    internal AwsSigV4SearchAuth(
        SearchEngine engine, string? profile, IAwsCredentialSource source, string region, string service, TimeProvider clock)
    {
        _engine = engine;
        _profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        _source = source;
        Region = region;
        Service = service;
        _clock = clock;
    }

    /// <summary>The signing region.</summary>
    internal string Region { get; }

    /// <summary>The signing name: <c>es</c> or <c>aoss</c>.</summary>
    internal string Service { get; }

    public async ValueTask ApplyAsync(HttpRequestMessage request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no URI to sign.");

        ImmutableCredentials credentials;
        try
        {
            credentials = await _source.GetAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SearchSignInException)
        {
            throw;
        }
        catch (Exception e)
        {
            var problem = DynamoDbErrors.IsSignInFailure(e) && DynamoDbErrors.IsProfileMissing(e)
                ? AwsSignInProblem.ProfileNotFound
                : AwsSignInProblem.CouldNotSignIn;
            throw new SearchSignInException(_engine, _profile, problem);
        }

        if (credentials is null || string.IsNullOrEmpty(credentials.AccessKey) || string.IsNullOrEmpty(credentials.SecretKey))
        {
            throw new SearchSignInException(_engine, _profile, AwsSignInProblem.CouldNotSignIn);
        }

        // The Host header is set to exactly what is signed, so the HTTP stack cannot send a different spelling.
        var host = HostHeader(uri);
        request.Headers.Host = host;

        var signed = AwsSigV4.Sign(
            request.Method.Method,
            uri,
            host,
            body.Span,
            Region,
            Service,
            credentials.AccessKey,
            credentials.SecretKey,
            credentials.UseToken ? credentials.Token : null,
            _clock.GetUtcNow().UtcDateTime);

        foreach (var (name, value) in signed.All)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>The <c>Host</c> header for <paramref name="uri"/>: the host (IPv6 bracketed, a name in its ASCII
    /// form) and the port unless it is the scheme's default.</summary>
    internal static string HostHeader(Uri uri)
    {
        var name = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;

        return uri.IsDefaultPort ? name : $"{name}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
