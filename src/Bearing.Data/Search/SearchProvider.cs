using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.SqlServer;

namespace Bearing.Data.Search;

/// <summary>
/// Elasticsearch and OpenSearch — one class, two engines, each registered under its own <see cref="Id"/>. They
/// share the HTTP transport, the protocol and the schema read (<see cref="SearchConnectionFactory"/>,
/// <see cref="SearchQueryExecutor"/>, <see cref="SearchMetadataReader"/>), and differ in what a provider declares:
/// the editor's fields, the sign-in methods and the name. Two ids rather than one engine with a switch because a
/// provider is paired with exactly one dialect by id (<c>ProviderTraits</c>), and the two servers differ in
/// everything a dialect owns — quoting, endpoints, statement lists. The SQL text belongs to <c>Bearing.Sql</c>'s
/// <c>ElasticsearchDialect</c> / <c>OpenSearchDialect</c>.
/// <para>
/// Query-only, so several of the interface's features are answered "no" here rather than approximated (§1.1:
/// never offer a setting or make a claim nobody arranged) — manual commit, session time zone, server activity,
/// Entra and integrated sign-in. Each flag below says why.
/// </para>
/// <para>
/// Sign-in: HTTP Basic on both, an API key on Elasticsearch (<c>Options["Authentication"]</c>), and on OpenSearch
/// an AWS profile whose credentials the AWS SDK resolves and Bearing uses, in memory, to sign each request with
/// SigV4 (§1.9c's OpenSearch counterpart). Which one is decided by the connection's credential kind and that
/// option, never by whether a secret is present — see <see cref="SearchConnectionFactory"/>.
/// </para>
/// </summary>
public sealed class SearchProvider : IDbProvider
{
    public const string ElasticsearchId = "elasticsearch";
    public const string OpenSearchId = "opensearch";

    /// <summary>The hint under Elasticsearch's Authentication choice: what the API key choice changes in the
    /// other two boxes.</summary>
    internal const string AuthenticationHint =
        "With API key, enter the encoded key in the password box; User is not sent.";

    /// <summary>The hint under OpenSearch's User field: what it means with the AWS profile credential.</summary>
    internal const string OpenSearchUserHint =
        "With the AWS profile credential this is the profile name; blank uses the AWS SDK's default credential chain.";

    /// <summary>The hint under OpenSearch's AWS region field.</summary>
    internal const string RegionHint =
        "Only used with the AWS profile credential. Blank reads it from an amazonaws.com or on.aws host name.";

    private readonly SearchEngine _engine;

    private SearchProvider(SearchEngine engine)
    {
        _engine = engine;
        ConnectionFields = engine == SearchEngine.OpenSearch ? OpenSearchFields() : ElasticsearchFields();
    }

    /// <summary>The Elasticsearch engine (<see cref="ElasticsearchId"/>).</summary>
    public static SearchProvider Elasticsearch() => new(SearchEngine.Elasticsearch);

    /// <summary>The OpenSearch engine (<see cref="OpenSearchId"/>).</summary>
    public static SearchProvider OpenSearch() => new(SearchEngine.OpenSearch);

    /// <summary>Which server this provider speaks to — the factory's, reader's and executor's engine.</summary>
    internal SearchEngine Engine => _engine;

    public string Id => _engine == SearchEngine.OpenSearch ? OpenSearchId : ElasticsearchId;

    public string DisplayName => SearchErrors.EngineName(_engine);

    /// <summary>
    /// Host and port (9200, both servers' own), an optional user, and the password box. No Database: a cluster has
    /// one namespace of indices, which the tree shows under one node named after the host.
    /// <para>
    /// Elasticsearch adds the Authentication choice, and its password box is labelled for both secrets it can
    /// hold. OpenSearch adds the AWS region (AWS profile sign-in only), and its User field says it names the
    /// profile under that credential kind. Anything but <c>Host</c>, <c>Port</c>, <c>User</c> and
    /// <c>Password</c> lands in <see cref="ConnectionInfo.Options"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<ConnectionField> ConnectionFields { get; }

    private static IReadOnlyList<ConnectionField> ElasticsearchFields() =>
    [
        new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
        new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true,
            Default: SearchConnectionFactory.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: false),
        new ConnectionField("Password", "Password or API key", ConnectionFieldKind.Password, Required: false),
        new ConnectionField(SearchConnectionFactory.AuthenticationOption, "Authentication", ConnectionFieldKind.Choice,
            Required: false,
            Default: SearchConnectionFactory.AuthenticationPassword,
            Choices: [SearchConnectionFactory.AuthenticationPassword, SearchConnectionFactory.AuthenticationApiKey],
            Hint: AuthenticationHint),
    ];

    private static IReadOnlyList<ConnectionField> OpenSearchFields() =>
    [
        new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
        new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true,
            Default: SearchConnectionFactory.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: false, Hint: OpenSearchUserHint),
        new ConnectionField("Password", "Password", ConnectionFieldKind.Password, Required: false),
        new ConnectionField(SearchConnectionFactory.RegionOption, "AWS region", ConnectionFieldKind.Text,
            Required: false, Suggestions: RegionSuggestions, Hint: RegionHint),
    ];

    /// <summary>The SDK's named regions, the code saved and the name only shown — built as
    /// <see cref="DynamoDbProvider"/> builds its own.</summary>
    private static IReadOnlyList<ConnectionSuggestion> RegionSuggestions()
        => RegionList.Value;

    private static readonly Lazy<IReadOnlyList<ConnectionSuggestion>> RegionList = new(()
        => AwsRegions.All.Select(r => new ConnectionSuggestion(r.Code, r.Name)).ToList());

    /// <summary>
    /// False: there is no session to make read-only — each request stands alone. Bearing refuses writes before
    /// they are sent (<c>WriteRefusal</c>); what stops one at the server is a role, API key or IAM policy without
    /// write privileges.
    /// </summary>
    public bool EnforcesReadOnlyOnServer => false;

    /// <summary>False: no request Bearing sends carries a session time zone, so the dialog offers none.</summary>
    public bool SupportsSessionTimeZone => false;

    /// <summary>False: neither server takes the OS identity over HTTP from this client.</summary>
    public bool SupportsIntegratedAuth => false;

    /// <summary>False: the factory has nowhere to put an Entra access token.</summary>
    public bool SupportsEntraToken => false;

    /// <summary>True: a password for HTTP Basic, or (Elasticsearch, Authentication "API key") the encoded API key,
    /// held by <c>ISecretStore</c> exactly as a password is (§1.1).</summary>
    public bool SupportsPasswordCredentials => true;

    /// <summary>True on OpenSearch only: Amazon OpenSearch Service and Serverless sign requests with SigV4.
    /// Elasticsearch has no AWS sign-in for the factory to honour.</summary>
    public bool SupportsAwsProfile => _engine == SearchEngine.OpenSearch;

    /// <summary>False: neither server has a transaction to hold open across requests.</summary>
    public bool SupportsManualCommit => false;

    /// <summary>True: the TLS mode decides the scheme and the certificate rule (<see cref="SearchConnectionFactory"/>).</summary>
    public bool SupportsTlsModes => true;

    /// <summary>True: the server is a host and port to forward to. The factory dials the tunnel's loopback end
    /// while still naming the real host — see <see cref="CreateConnectionFactory(ConnectionInfo, string?, TunnelEndpoint)"/>.</summary>
    public bool SupportsSshTunnel => true;

    /// <summary>False: Bearing has no read of the servers' running tasks to show or cancel.</summary>
    public bool SupportsServerActivity => false;

    public DbErrorKind Classify(QueryError error) => SearchErrors.ClassifyState(error.SqlState);

    public DbErrorKind ClassifyException(Exception exception) => SearchErrors.Classify(exception);

    /// <summary>The password or API key is <paramref name="password"/>; it is ignored for an AWS profile.</summary>
    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new SearchConnectionFactory(_engine, info, password);

    /// <summary>
    /// Through an SSH tunnel: the factory keeps <paramref name="info"/>'s real host — for the request URI, the
    /// <c>Host</c> header, the certificate check and the SigV4 signature — and dials <paramref name="tunnel"/>.
    /// Declared here, not inherited: the interface's default would replace the host with the loopback end.
    /// </summary>
    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password, TunnelEndpoint tunnel)
        => new SearchConnectionFactory(_engine, info, password, tunnel);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new SearchMetadataReader((SearchConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new SearchQueryExecutor((SearchConnectionFactory)factory);

    public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory)
        => throw new NotSupportedException(
            $"{DisplayName} cannot hold a transaction open across requests; check {nameof(IDbProvider)}.{nameof(SupportsManualCommit)} first.");

    public IServerActivity CreateServerActivity(IDbConnectionFactory factory) => UnsupportedServerActivity.Instance;
}
