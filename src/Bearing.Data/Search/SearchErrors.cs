using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bearing.Core.Data;
using Bearing.Data.Aws;
using Bearing.Data.DynamoDb;

namespace Bearing.Data.Search;

/// <summary>
/// How a connection proves who it is, once its settings are read (<see cref="SearchConnectionFactory"/>) —
/// decided by the credential kind and the Authentication option, never by whether a secret was passed.
/// </summary>
internal enum SearchAuthMethod
{
    /// <summary>No <c>Authorization</c> header: a blank user and a blank password.</summary>
    None,

    /// <summary>HTTP Basic with the user and the password.</summary>
    Basic,

    /// <summary><c>Authorization: ApiKey …</c> (Elasticsearch).</summary>
    ApiKey,

    /// <summary>A SigV4 signature from an AWS profile's credentials (OpenSearch).</summary>
    AwsSigV4,
}

/// <summary>
/// What went wrong with an Elasticsearch or OpenSearch request, placed on the neutral <see cref="DbErrorKind"/>
/// scale and said in words the user can act on. Pure, so every verdict and sentence is testable without a
/// server (§2.5) — the <c>DynamoDbErrors</c> arrangement.
/// <para>
/// <b>Everything a server or the transport says is scrubbed before it is shown</b> (<see cref="Scrub"/>): an
/// <c>Authorization</c> value, a Basic / ApiKey / Bearer token, a <c>user:password@</c> in a URL, and the AWS
/// forms <see cref="DynamoDbErrors.Scrub"/> already knows. No sentence here is built from a secret: the sign-in
/// sentence names the method and the user name, never the password or key (§1.1).
/// </para>
/// </summary>
internal static class SearchErrors
{
    // QueryError.SqlState for what Bearing itself raised. A server error carries the server's own error type
    // there instead ("parsing_exception", "SyntaxCheckException"); those never start with "Bearing".
    internal const string SignInCode = "BearingSignIn";
    internal const string TimeoutCode = "BearingTimeout";
    internal const string CanceledCode = "BearingCanceled";
    internal const string RedirectCode = "BearingRedirect";

    /// <summary>A 400 whose reason reads <c>line L:C: …</c> but whose type is not one of
    /// <see cref="SyntaxTypes"/> — the server pointed into the statement, so it is the statement's shape.</summary>
    internal const string SyntaxCode = "BearingSyntax";

    /// <summary>The longest excerpt of a body that is not JSON quoted in a message.</summary>
    internal const int ExcerptLength = 300;

    /// <summary>
    /// The servers' error types for a statement they could not parse or check: Elasticsearch SQL and ES|QL
    /// (<c>parsing_exception</c>, <c>verification_exception</c>) and the OpenSearch SQL / PPL plugin's
    /// (<c>SyntaxCheckException</c>, <c>SemanticCheckException</c>).
    /// </summary>
    private static readonly HashSet<string> SyntaxTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "parsing_exception", "verification_exception", "SyntaxCheckException", "SemanticCheckException",
        SyntaxCode,
    };

    /// <summary><c>line L:C:</c> as both servers' parsers write it (Elasticsearch SQL and ES|QL; the column is
    /// 1-based).</summary>
    private static readonly Regex LinePosition = new(
        @"\bline (?<line>\d{1,6}):(?<column>\d{1,6}):", RegexOptions.CultureInvariant);

    /// <summary>The display name of an engine, for sentences.</summary>
    internal static string EngineName(SearchEngine engine)
        => engine == SearchEngine.OpenSearch ? "OpenSearch" : "Elasticsearch";

    // ---- Classification --------------------------------------------------------------------------------

    /// <summary>Place a failed request on the neutral scale by the code <see cref="ToQueryError(int, byte[], string?, string?)"/>
    /// or <see cref="ToQueryError(Exception, SearchEngine, int, bool)"/> recorded.</summary>
    internal static DbErrorKind ClassifyState(string? code) => code switch
    {
        SignInCode => DbErrorKind.Authentication,
        TimeoutCode or CanceledCode => DbErrorKind.Canceled,
        { } type when SyntaxTypes.Contains(type) => DbErrorKind.SyntaxOrShape,
        _ => DbErrorKind.Unknown,
    };

    /// <summary>
    /// The same judgement for a thrown exception — the connect path has no <see cref="QueryError"/>. Walks the
    /// whole chain. A 401 is <see cref="DbErrorKind.Authentication"/>; a 403 is a privilege and stays
    /// <see cref="DbErrorKind.Unknown"/> (signing in again changes nothing). A
    /// <see cref="SearchTransportException"/> is terminal: the handler's connect timeout arrives as a cancel
    /// inside it, and it is no cancel of the caller's (a caller's cancel is never wrapped).
    /// </summary>
    internal static DbErrorKind Classify(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            switch (e)
            {
                case SearchSignInException:
                    return DbErrorKind.Authentication;
                case SearchTransportException:
                    return DbErrorKind.Unknown;
                case OperationCanceledException:
                    return DbErrorKind.Canceled;
                case SearchServerException server:
                    return ClassifyState(server.Error.SqlState);
            }
        }

        return DbErrorKind.Unknown;
    }

    // ---- Sentences -------------------------------------------------------------------------------------

    /// <summary>
    /// The sentence for an HTTP 401 — which method was refused, and what to check. Built from the method and
    /// the user name only; the password, key or signature has no path into it.
    /// </summary>
    internal static string SignInSentence(SearchEngine engine, SearchAuthMethod method, string? user)
    {
        var name = EngineName(engine);
        var who = string.IsNullOrWhiteSpace(user) ? "" : $" for user '{user.Trim()}'";

        return method switch
        {
            SearchAuthMethod.Basic =>
                $"{name} refused the user name and password{who} (HTTP 401). Check both and try again.",
            SearchAuthMethod.ApiKey =>
                $"{name} refused the API key (HTTP 401). Check that it is the encoded key {name} showed when it was "
                + "created, and that it has not expired or been invalidated.",
            SearchAuthMethod.AwsSigV4 => string.IsNullOrWhiteSpace(user)
                ? $"{name} refused the request signed with the AWS SDK's default credentials (HTTP 401). "
                  + "Set an AWS profile on the connection, or run \"aws sso login\" and try again."
                : $"{name} refused the request signed with AWS profile '{user.Trim()}' (HTTP 401). If it is an SSO "
                  + $"profile, run \"aws sso login --profile {user.Trim()}\" and try again; otherwise check the profile.",
            _ =>
                $"{name} asks for sign-in (HTTP 401), and this connection has no user or password. "
                + "Enter a user and password"
                + (engine == SearchEngine.Elasticsearch ? ", or choose API key authentication." : "."),
        };
    }

    /// <summary>
    /// The sentence for a statement stopped by the connection's own clock (<see cref="SessionPolicy"/>,
    /// §1.9a): it names the setting, since the user chose it and may want to raise it. Says nothing about
    /// whether the server stopped working on it — the request was abandoned, which is all Bearing knows.
    /// </summary>
    internal static string TimeoutSentence(SearchEngine engine, int timeoutSeconds)
        => $"Stopped: this connection's statement timeout ({timeoutSeconds} s) elapsed before {EngineName(engine)} answered.";

    /// <summary>A 3xx answer. The target is not quoted: a <c>Location</c> may carry anything, credentials included.</summary>
    internal static string RedirectSentence(int status)
        => $"The server answered with a redirect (HTTP {status}), and Bearing does not follow redirects. "
           + "Check the host, the port and the TLS mode.";

    /// <summary>The host field held more than a host. The value is not quoted: a pasted URL can carry a
    /// <c>user:password@</c>.</summary>
    internal const string HostOnlySentence =
        "Enter the server's host name or address only — no http:// or https://, no path and no port (the port has "
        + "its own field). Choose encryption with the TLS mode.";

    internal const string HostMissingSentence = "Enter the host name or address of the server.";

    /// <summary>The port is neither 0 (the default) nor 1–65535. Refused rather than replaced: a connection must
    /// not quietly reach a port nobody entered.</summary>
    internal const string PortOutOfRangeSentence =
        "The port must be a number from 1 to 65535, or 0 for the default port (9200).";

    internal const string ApiKeyMissingSentence =
        "Authentication is set to API key, but no key was given. Enter the encoded API key (the form Elasticsearch "
        + "shows when it creates one) in the password box.";

    internal const string ControlCharacterSentence =
        "The API key holds a line break or another control character, which an HTTP header cannot carry. "
        + "Paste the key again.";

    /// <summary>
    /// An AWS-profile connection over plain HTTP to a host that is not this machine. Said before any request
    /// is sent: a signed request over plain HTTP exposes the signature and everything it reads.
    /// </summary>
    internal const string AwsPlainHttpSentence =
        "This connection signs its requests with an AWS profile, and Bearing never sends a signed request over "
        + "plain HTTP to a host that is not this machine: the signature and everything read would cross the "
        + "network unencrypted. Choose a TLS mode other than Disable.";

    /// <summary>The TLS-preferring connection found a server that does not speak TLS, and its requests are
    /// signed with an AWS profile — the fallback to plain HTTP is refused off loopback.</summary>
    internal const string AwsNoTlsSentence =
        "The server did not answer TLS, and a connection that signs its requests with an AWS profile is never "
        + "sent over plain HTTP to a host that is not this machine. Check the host and the port.";

    /// <summary>
    /// An AWS-profile connection with no region: the AWS region field is blank and the host is not an AWS
    /// endpoint name a region can be read from (a custom endpoint, an IP address). Said at connect, before any
    /// credential is asked for or any request is sent.
    /// </summary>
    internal const string AwsRegionMissingSentence =
        "This connection signs its requests with an AWS profile, and the signature needs the AWS region. Enter it "
        + "in the AWS region field (for example eu-west-1): it can only be read from the host when the host is an "
        + "Amazon OpenSearch Service or OpenSearch Serverless endpoint name.";

    /// <summary>The AWS region field holds something that is not a region name. Not quoted: a pasted value could
    /// be anything.</summary>
    internal const string AwsRegionInvalidSentence =
        "The AWS region field does not hold a region name. Enter one such as eu-west-1 (letters, digits and hyphens "
        + "only), or leave it blank to read the region from an Amazon endpoint name.";

    /// <summary>
    /// The sentence for an AWS-profile connection whose credentials could not be produced — before anything was
    /// sent, so it says nothing about the server. Built from the profile name and the problem alone: the SDK's own
    /// text, and every key, secret or token, have no path into it (§1.1). A blank profile is the SDK's default
    /// chain, and the sentence says that it produced none.
    /// </summary>
    internal static string AwsSignInSentence(SearchEngine engine, string? profile, AwsSignInProblem problem)
    {
        var name = EngineName(engine);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return $"The AWS SDK's default credential chain produced no credentials to sign the requests to {name} "
                + "with. Set an AWS profile on the connection, or run \"aws sso login\" and try again.";
        }

        var who = profile.Trim();
        return problem switch
        {
            AwsSignInProblem.ProfileNotFound =>
                $"AWS profile '{who}' was not found. Check its name in your AWS config, "
                + $"or run \"aws sso login --profile {who}\" if it is an SSO profile.",
            AwsSignInProblem.ProfileHasNoCredentials =>
                $"AWS profile '{who}' was found, but the AWS SDK could not read a complete set of credential settings "
                + "from it (access keys, SSO, a role or a credential process). A profile that only sets a region "
                + $"cannot sign in; check the profile in your AWS config (for an SSO profile, \"aws sso login --profile {who}\").",
            _ =>
                $"Could not get AWS credentials from profile '{who}' to sign the requests to {name} with. If it is an "
                + $"SSO profile, run \"aws sso login --profile {who}\" and try again; otherwise check the profile in your AWS config.",
        };
    }

    /// <summary>The Authentication option holds a value Bearing does not know. Quoted: it is a setting, not a secret.</summary>
    internal static string UnknownAuthenticationSentence(string value)
        => $"The Authentication setting \"{value}\" is not one Bearing knows. Choose "
           + $"\"{SearchConnectionFactory.AuthenticationPassword}\" or \"{SearchConnectionFactory.AuthenticationApiKey}\".";

    /// <summary>A credential kind this engine does not sign in with (a hand-edited project, a pasted connection).</summary>
    internal static string UnsupportedCredentialSentence(SearchEngine engine, CredentialKind kind)
        => kind == CredentialKind.AwsProfile
            ? $"AWS profile sign-in is available on OpenSearch connections only. Use a stored password or a prompt "
              + $"for this {EngineName(engine)} connection."
            : $"This sign-in method is not available on {EngineName(engine)} connections. Use a stored password "
              + "or a prompt.";

    internal static string TooLargeSentence(long limitBytes)
        => $"The server's answer is larger than {SizeText(limitBytes)}, the most Bearing reads for one request.";

    // ---- Server answers -> QueryError ------------------------------------------------------------------

    /// <summary>
    /// A non-2xx answer as the <see cref="QueryError"/> the grid shows. Reads the servers' JSON envelope —
    /// <c>{"error":{"type","reason","details"},"status"}</c>, where <c>error</c> may also be a plain string —
    /// and falls back to a bounded excerpt of a body that is not JSON. When <c>error.root_cause[0].reason</c> is
    /// non-blank and not already in the text (it differs from the reason), it follows the reason in parentheses,
    /// cut at <see cref="ExcerptLength"/> characters: a generic reason ("all shards failed") then still says what
    /// failed. <paramref name="language"/> ("SQL",
    /// "ES|QL", "PPL") leads the message when given, so a parse error says which language it was read as.
    /// <para>
    /// <b>Position.</b> When the reason (or, failing that, the details) carries <c>line L:C:</c>, the position
    /// is the 1-based character offset of that line and column in <paramref name="statement"/> — the convention
    /// PostgreSQL's <c>Position</c> uses, counted in the text that was sent. Null when there is no statement,
    /// no such marker, or the marker points outside the text.
    /// </para>
    /// </summary>
    internal static QueryError ToQueryError(int status, byte[] body, string? language, string? statement)
    {
        if (status == 401)
        {
            return new QueryError(Lead(language, "The server refused the sign-in (HTTP 401)."), SignInCode, null);
        }
        if (status is >= 300 and < 400)
        {
            return new QueryError(RedirectSentence(status), RedirectCode, null);
        }

        var (type, reason, details, rootCause) = ReadEnvelope(body);
        var text = reason ?? details ?? Excerpt(body);
        if (reason is not null && details is not null && !reason.Contains(details, StringComparison.Ordinal))
        {
            text = $"{reason}: {details}";
        }
        // The root cause, when it says more: Elasticsearch answers a closed cursor with reason "all shards failed"
        // and the real cause ("No search context found for id […]") only in root_cause[0] (measured, 9.5.6).
        if (reason is not null && rootCause is not null && !text.Contains(rootCause, StringComparison.Ordinal))
        {
            text = $"{text} ({Bounded(rootCause)})";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"The server answered HTTP {status.ToString(CultureInfo.InvariantCulture)} with no explanation.";
        }

        var marker = LinePosition.Match(reason ?? "") is { Success: true } inReason
            ? inReason
            : LinePosition.Match(details ?? "");
        var position = marker.Success && statement is not null
            ? PositionOf(statement,
                int.Parse(marker.Groups["line"].Value, CultureInfo.InvariantCulture),
                int.Parse(marker.Groups["column"].Value, CultureInfo.InvariantCulture))
            : null;

        var code = type;
        if ((code is null || !SyntaxTypes.Contains(code)) && status == 400 && reason is not null
            && LinePosition.Match(reason) is { Success: true, Index: 0 })
        {
            code = SyntaxCode;
        }
        code ??= $"HTTP {status.ToString(CultureInfo.InvariantCulture)}";

        return new QueryError(Scrub(Lead(language, text)), code, position);
    }

    /// <summary>
    /// A thrown failure as the <see cref="QueryError"/> the grid shows. <paramref name="timedOut"/> is the
    /// caller's knowledge (its own linked token fired), not something an exception can say. A
    /// <see cref="SearchTransportException"/> is terminal — its own sentence ("No connection could be made within
    /// 15 s. …"), never "canceled" from the connect timeout's cancel inside it.
    /// </summary>
    internal static QueryError ToQueryError(Exception exception, SearchEngine engine, int timeoutSeconds, bool timedOut = false)
    {
        if (timedOut)
        {
            return new QueryError(TimeoutSentence(engine, timeoutSeconds), TimeoutCode, null);
        }

        foreach (var e in Chain(exception))
        {
            switch (e)
            {
                case SearchSignInException signIn:
                    return new QueryError(signIn.Message, SignInCode, null);
                case SearchTransportException transport:
                    return new QueryError(Scrub(transport.Message), null, null);
                case OperationCanceledException:
                    return new QueryError("The statement was canceled.", CanceledCode, null);
                case SearchServerException server:
                    return server.Error;
            }
        }

        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

        return new QueryError(Scrub(message), null, null);
    }

    /// <summary>
    /// The 1-based character offset of line <paramref name="line"/>, column <paramref name="column"/> (both
    /// 1-based) in <paramref name="text"/>; <c>\r\n</c>, <c>\n</c> and <c>\r</c> each end a line. Null when the
    /// line does not exist or the column is past its end (one past the last character is allowed: a parser
    /// points there for "expected more").
    /// </summary>
    internal static int? PositionOf(string text, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            return null;
        }

        var start = 0;
        for (var current = 1; current < line; current++)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                return null;
            }
            start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }

        var lineEnd = text.IndexOfAny(['\r', '\n'], start);
        var length = (lineEnd < 0 ? text.Length : lineEnd) - start;

        return column <= length + 1 ? start + column : null;
    }

    /// <summary>
    /// A transport failure (no connection, a failed handshake, an unreadable answer) as one scrubbed sentence:
    /// every distinct message along the chain, skipping the "see inner exception" ones that say nothing.
    /// </summary>
    internal static string DescribeTransport(Exception exception)
    {
        var parts = new List<string>();
        foreach (var e in Chain(exception))
        {
            var message = e.Message.Trim();
            if (message.Length == 0
                || message.Contains("see inner exception", StringComparison.OrdinalIgnoreCase)
                || parts.Contains(message))
            {
                continue;
            }
            parts.Add(message.TrimEnd('.'));
        }

        return Scrub(parts.Count == 0 ? exception.GetType().Name : string.Join(": ", parts) + ".");
    }

    // ---- Scrubbing -------------------------------------------------------------------------------------

    // "Authorization: <anything up to the line end>" — the whole value, whatever its scheme.
    private static readonly Regex AuthorizationHeader = new(
        @"(?<key>(?:proxy-)?authorization)\s*[:=]\s*[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A scheme followed by a token: "Basic dXNlcjpwYXNz", "ApiKey VnVhQ2ZH…", "Bearer eyJ…". Short words are
    // left alone so a sentence like "Basic auth" survives; anything token-shaped of eight or more is not.
    private static readonly Regex SchemeToken = new(
        @"\b(?<scheme>Basic|ApiKey|Bearer)\s+[A-Za-z0-9+/=._~\-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "api_key=…", "apikey: …", "password: …".
    private static readonly Regex LabelledKey = new(
        @"(?<key>api[_\- ]?key|password|passwd)\s*[=:]\s*(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A SigV4 "Credential=AKID/20250101/region/service/aws4_request" or "Signature=<hex>", and the presigned
    // query forms "X-Amz-Credential=" / "X-Amz-Signature=": the access-key id and the signature, whatever their
    // shape (an id that is not AKIA-shaped escapes the AWS key-id pattern).
    private static readonly Regex SigV4Fragment = new(
        @"\b(?<key>(?:X-Amz-)?(?:Credential|Signature))\s*=\s*[^\s,;""'&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "scheme://user:password@host" — the user-info part of a URL.
    private static readonly Regex UrlUserInfo = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^/\s@]*@", RegexOptions.CultureInvariant);

    /// <summary>
    /// Redact anything in <paramref name="message"/> that can be a credential: an <c>Authorization</c> value,
    /// a Basic / ApiKey / Bearer token, a labelled API key or password, a SigV4 <c>Credential=</c> or
    /// <c>Signature=</c> fragment, the user-info of a URL, then the AWS
    /// forms (<see cref="DynamoDbErrors.Scrub"/>: access-key ids, <c>secret=…</c>, session tokens). Over-redacting
    /// is the safe direction to be wrong in.
    /// </summary>
    internal static string Scrub(string message)
    {
        var text = AuthorizationHeader.Replace(message, m => $"{m.Groups["key"].Value}: ***");
        text = SchemeToken.Replace(text, m => $"{m.Groups["scheme"].Value} ***");
        text = LabelledKey.Replace(text, m => $"{m.Groups["key"].Value}=***");
        text = SigV4Fragment.Replace(text, m => $"{m.Groups["key"].Value}=***");
        text = UrlUserInfo.Replace(text, m => $"{m.Groups["scheme"].Value}***@");

        return DynamoDbErrors.Scrub(text);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static string SizeText(long bytes)
        => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0
            ? $"{(bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB"
            : $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes";

    private static string Lead(string? language, string text)
        => string.IsNullOrWhiteSpace(language) ? text : $"{language}: {text}";

    /// <summary>The envelope's type, reason and details; all null when the body is not JSON or has no
    /// <c>error</c>. A string <c>error</c> is the reason.</summary>
    private static (string? Type, string? Reason, string? Details, string? RootCause) ReadEnvelope(byte[] body)
    {
        if (body.Length == 0)
        {
            return (null, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error))
            {
                return (null, null, null, null);
            }

            return error.ValueKind switch
            {
                JsonValueKind.String => (null, NonBlank(error.GetString()), null, null),
                JsonValueKind.Object => (StringOf(error, "type"), StringOf(error, "reason"), StringOf(error, "details"),
                    RootCauseOf(error)),
                _ => (null, null, null, null),
            };
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>The <c>reason</c> of <c>error.root_cause[0]</c>, when the envelope has one that is a non-blank
    /// string; null otherwise.</summary>
    private static string? RootCauseOf(JsonElement error)
        => error.TryGetProperty("root_cause", out var causes)
           && causes.ValueKind == JsonValueKind.Array
           && causes.GetArrayLength() > 0
           && causes[0].ValueKind == JsonValueKind.Object
            ? StringOf(causes[0], "reason")
            : null;

    /// <summary><paramref name="text"/> cut at <see cref="ExcerptLength"/> characters, with "…" when cut.</summary>
    private static string Bounded(string text)
        => text.Length <= ExcerptLength ? text : text[..ExcerptLength] + "…";

    private static string? StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NonBlank(value.GetString())
            : null;

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The start of a body that is not a JSON envelope, whitespace collapsed, cut at
    /// <see cref="ExcerptLength"/> characters.</summary>
    private static string Excerpt(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, ExcerptLength * 4));
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();

        return collapsed.Length <= ExcerptLength ? collapsed : collapsed[..ExcerptLength] + "…";
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (!seen.Add(e))
            {
                continue;
            }
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                {
                    pending.Push(inner);
                }
            }
            else if (e.InnerException is { } next)
            {
                pending.Push(next);
            }
        }
    }
}

/// <summary>
/// A connection setting that cannot be honoured (a host carrying a scheme or a path, a blank API key, an AWS
/// profile over plain HTTP off loopback). The message is the whole sentence. Thrown by the connect, never by
/// the factory's constructor.
/// </summary>
internal sealed class SearchConfigurationException : Exception
{
    public SearchConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>
/// The connection could not sign in. Two origins, one classification (<see cref="DbErrorKind.Authentication"/>):
/// the server answered HTTP 401 (<see cref="SearchErrors.SignInSentence"/>, built from the method and the user
/// name only), or — for an AWS profile — the credentials to sign with could not be produced, before anything was
/// sent (<see cref="SearchErrors.AwsSignInSentence"/>, built from the profile name and the problem only). Never
/// carries the SDK's exception: its text is not ours to show, and an inner exception is what a log prints.
/// </summary>
internal sealed class SearchSignInException : Exception
{
    public SearchSignInException(SearchEngine engine, SearchAuthMethod method, string? user)
        : base(SearchErrors.SignInSentence(engine, method, user))
    {
        Method = method;
    }

    /// <summary>An AWS profile's credentials could not be produced (<paramref name="problem"/>); nothing was sent.</summary>
    public SearchSignInException(SearchEngine engine, string? profile, AwsSignInProblem problem)
        : base(SearchErrors.AwsSignInSentence(engine, profile, problem))
    {
        Method = SearchAuthMethod.AwsSigV4;
        Problem = problem;
    }

    /// <summary>The method that was refused, or could not be used.</summary>
    public SearchAuthMethod Method { get; }

    /// <summary>Why an AWS profile's credentials could not be produced; null for an HTTP 401.</summary>
    public AwsSignInProblem? Problem { get; }
}

/// <summary>
/// The request never got an HTTP answer: no connection, a failed TLS handshake, a broken stream. The message
/// is scrubbed (<see cref="SearchErrors.DescribeTransport"/>); the original is kept as the inner exception only
/// when scrubbing changed none of its chain's text, so an inner exception never carries what the message hid.
/// </summary>
internal sealed class SearchTransportException : Exception
{
    public SearchTransportException(string message, HttpRequestError? error, Exception? inner) : base(message, inner)
    {
        Error = error;
    }

    /// <summary>What <see cref="HttpRequestException.HttpRequestError"/> said, when the failure was one.</summary>
    public HttpRequestError? Error { get; }
}

/// <summary>
/// A non-2xx answer surfaced as an exception — where the caller has no grid to put a <see cref="QueryError"/>
/// in (the connection test, a redirect). <see cref="Error"/> is the scrubbed error; the message is its text.
/// </summary>
internal sealed class SearchServerException : Exception
{
    public SearchServerException(int statusCode, QueryError error) : base(error.Message)
    {
        StatusCode = statusCode;
        Error = error;
    }

    public int StatusCode { get; }

    public QueryError Error { get; }
}

/// <summary>An answer longer than the ceiling the caller set — refused rather than buffered without bound.</summary>
internal sealed class SearchResponseTooLargeException : Exception
{
    public SearchResponseTooLargeException(long limitBytes) : base(SearchErrors.TooLargeSentence(limitBytes))
    {
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }
}
