using System.Net;
using System.Net.Sockets;
using System.Text;
using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What the Elasticsearch and OpenSearch providers answer before anything connects: their ids and names, the
/// fields the editor shows, every capability flag (§1.9a: a new engine answers each one), the trio they build,
/// the tunnel hand-over that keeps the real host, and what they refuse outright (SC1). The one round trip here
/// is to a listener on this machine's loopback, standing in for an SSH tunnel's end.
/// </summary>
public class SearchProviderTests
{
    private static readonly SearchProvider Elastic = SearchProvider.Elasticsearch();
    private static readonly SearchProvider Open = SearchProvider.OpenSearch();

    public static TheoryData<string> Engines => new() { SearchProvider.ElasticsearchId, SearchProvider.OpenSearchId };

    private static SearchProvider For(string id) => id == SearchProvider.OpenSearchId ? Open : Elastic;

    private static ConnectionInfo Info(string providerId, string host = "127.0.0.1", int port = 9200) => new()
    {
        Id = Guid.NewGuid(),
        Name = "search",
        ProviderId = providerId,
        Host = host,
        Port = port,
        Tls = TlsMode.Disable,
    };

    // ---- Identity ---------------------------------------------------------------------------------------

    [Fact]
    public void The_ids_are_elasticsearch_and_opensearch_and_each_is_named_after_its_server()
    {
        Assert.Equal("elasticsearch", SearchProvider.ElasticsearchId);
        Assert.Equal("opensearch", SearchProvider.OpenSearchId);
        Assert.Equal("elasticsearch", Elastic.Id);
        Assert.Equal("opensearch", Open.Id);
        Assert.Equal("Elasticsearch", Elastic.DisplayName);
        Assert.Equal("OpenSearch", Open.DisplayName);
    }

    [Fact]
    public void Each_creator_makes_a_provider_for_its_own_engine()
    {
        Assert.Equal(SearchEngine.Elasticsearch, SearchProvider.Elasticsearch().Engine);
        Assert.Equal(SearchEngine.OpenSearch, SearchProvider.OpenSearch().Engine);
    }

    // ---- Fields -----------------------------------------------------------------------------------------

    [Fact]
    public void Elasticsearch_fields_are_host_port_user_the_password_or_api_key_box_and_the_authentication_choice()
    {
        var fields = Elastic.ConnectionFields;

        Assert.Equal(
            [
                ("Host", "Host", ConnectionFieldKind.Text, true, (string?)"localhost"),
                ("Port", "Port", ConnectionFieldKind.Number, true, "9200"),
                ("User", "User", ConnectionFieldKind.Text, false, null),
                ("Password", "Password or API key", ConnectionFieldKind.Password, false, null),
                ("Authentication", "Authentication", ConnectionFieldKind.Choice, false, "User and password"),
            ],
            fields.Select(f => (f.Key, f.Label, f.Kind, f.Required, f.Default)));

        var authentication = fields.Single(f => f.Key == SearchConnectionFactory.AuthenticationOption);
        Assert.Equal(["User and password", "API key"], authentication.Choices!);
        Assert.Equal(
            [SearchConnectionFactory.AuthenticationPassword, SearchConnectionFactory.AuthenticationApiKey],
            authentication.Choices!);
        Assert.Equal("With API key, enter the encoded key in the password box; User is not sent.", authentication.Hint);

        // No region and no AWS hint: Elasticsearch has no AWS sign-in.
        Assert.DoesNotContain(fields, f => f.Key == SearchConnectionFactory.RegionOption);
        Assert.Null(fields.Single(f => f.Key == "User").Hint);
        // A cluster has no database to name.
        Assert.DoesNotContain(fields, f => f.Key == "Database");
    }

    [Fact]
    public void Opensearch_fields_are_host_port_user_password_and_the_aws_region()
    {
        var fields = Open.ConnectionFields;

        Assert.Equal(
            [
                ("Host", "Host", ConnectionFieldKind.Text, true, (string?)"localhost"),
                ("Port", "Port", ConnectionFieldKind.Number, true, "9200"),
                ("User", "User", ConnectionFieldKind.Text, false, null),
                ("Password", "Password", ConnectionFieldKind.Password, false, null),
                ("Region", "AWS region", ConnectionFieldKind.Text, false, null),
            ],
            fields.Select(f => (f.Key, f.Label, f.Kind, f.Required, f.Default)));

        Assert.Equal(
            "With the AWS profile credential this is the profile name; blank uses the AWS SDK's default credential chain.",
            fields.Single(f => f.Key == "User").Hint);
        var region = fields.Single(f => f.Key == SearchConnectionFactory.RegionOption);
        Assert.Equal(
            "Only used with the AWS profile credential. Blank reads it from an amazonaws.com or on.aws host name.",
            region.Hint);

        // No Authentication choice: OpenSearch has no API key sign-in.
        Assert.DoesNotContain(fields, f => f.Key == SearchConnectionFactory.AuthenticationOption);
        Assert.DoesNotContain(fields, f => f.Key == "Database");
    }

    [Fact]
    public void The_region_suggestions_are_the_sdks_named_regions_code_saved_and_name_shown()
    {
        var region = Open.ConnectionFields.Single(f => f.Key == SearchConnectionFactory.RegionOption);

        var suggestions = region.Suggestions!();

        Assert.NotEmpty(suggestions);
        Assert.Equal(AwsRegions.All.Select(r => (r.Code, (string?)r.Name)), suggestions.Select(s => (s.Value, s.Description)));
        Assert.Contains(suggestions, s => s.Value == "eu-west-1");
        Assert.All(suggestions, s => Assert.Null(s.Fills));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void No_other_field_suggests_anything(string id)
        => Assert.All(
            For(id).ConnectionFields.Where(f => f.Key != SearchConnectionFactory.RegionOption),
            f => Assert.Null(f.Suggestions));

    // ---- Flags ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Engines))]
    public void Both_engines_answer_every_flag_for_a_query_only_engine_over_http(string id)
    {
        IDbProvider provider = For(id);

        Assert.False(provider.EnforcesReadOnlyOnServer);
        Assert.False(provider.SupportsSessionTimeZone);
        Assert.False(provider.SupportsIntegratedAuth);
        Assert.False(provider.SupportsEntraToken);
        Assert.True(provider.SupportsPasswordCredentials);
        Assert.False(provider.SupportsManualCommit);
        Assert.True(provider.SupportsTlsModes);
        Assert.True(provider.SupportsSshTunnel);
        Assert.False(provider.SupportsServerActivity);
    }

    [Fact]
    public void Only_opensearch_offers_the_aws_profile()
    {
        Assert.False(Elastic.SupportsAwsProfile);
        Assert.True(Open.SupportsAwsProfile);
    }

    // ---- The trio ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task It_builds_the_search_factory_reader_and_executor_for_its_engine(string id)
    {
        var provider = For(id);
        await using var built = provider.CreateConnectionFactory(Info(id), password: "secret");

        var factory = Assert.IsType<SearchConnectionFactory>(built);
        Assert.Equal(provider.Engine, factory.Engine);
        Assert.Equal("127.0.0.1", factory.Info.Host);
        Assert.IsType<SearchMetadataReader>(provider.CreateMetadataReader(factory));
        Assert.IsType<SearchQueryExecutor>(provider.CreateQueryExecutor(factory));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_transaction_scope_is_refused_naming_the_flag_to_check(string id)
    {
        var provider = For(id);
        await using var factory = provider.CreateConnectionFactory(Info(id), password: null);

        var refused = Assert.Throws<NotSupportedException>(() => provider.CreateTransactionScope(factory));

        Assert.Contains(nameof(IDbProvider.SupportsManualCommit), refused.Message);
        Assert.Contains(provider.DisplayName, refused.Message);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Server_activity_is_the_unsupported_one(string id)
    {
        var provider = For(id);
        await using var factory = provider.CreateConnectionFactory(Info(id), password: null);

        Assert.IsType<Bearing.Data.SqlServer.UnsupportedServerActivity>(provider.CreateServerActivity(factory));
    }

    // ---- Through a tunnel -------------------------------------------------------------------------------

    /// <summary>
    /// Called through the interface, as <c>ConnectionFactoryBuilder</c> calls it: the provider's own overload must
    /// win over the interface default, which would have replaced the host with the loopback end. Proved twice —
    /// the factory keeps the real host, and a real request dialled to the tunnel's end still names that host.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Through_a_tunnel_the_factory_keeps_the_real_host_and_dials_the_loopback_end(string id)
    {
        const string realHost = "search.example.invalid";
        await using var tunnelEnd = new OneRequestServer();
        IDbProvider provider = For(id);

        await using var built = provider.CreateConnectionFactory(
            Info(id, host: realHost, port: 9200), password: null, new TunnelEndpoint("127.0.0.1", tunnelEnd.Port));

        var factory = Assert.IsType<SearchConnectionFactory>(built);
        Assert.Equal(realHost, factory.Info.Host);
        Assert.Equal(9200, factory.Info.Port);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
        Assert.Equal($"{realHost}:9200", await tunnelEnd.HostHeader);
        Assert.Equal(new Uri($"http://{realHost}:9200/"), factory.BaseUri);
    }

    // ---- Classification ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Engines))]
    public void Classification_is_search_errors_verdict(string id)
    {
        var provider = For(id);

        Assert.Equal(DbErrorKind.Authentication, provider.Classify(new QueryError("x", SearchErrors.SignInCode, null)));
        Assert.Equal(DbErrorKind.Canceled, provider.Classify(new QueryError("x", SearchErrors.TimeoutCode, null)));
        Assert.Equal(DbErrorKind.SyntaxOrShape, provider.Classify(new QueryError("x", "parsing_exception", null)));
        Assert.Equal(DbErrorKind.Unknown, provider.Classify(new QueryError("x", "security_exception", null)));

        Assert.Equal(DbErrorKind.Authentication, provider.ClassifyException(
            new InvalidOperationException("wrapped", new SearchSignInException(provider.Engine, SearchAuthMethod.Basic, "elastic"))));
        Assert.Equal(DbErrorKind.Canceled, provider.ClassifyException(new OperationCanceledException()));
        Assert.Equal(DbErrorKind.Unknown, provider.ClassifyException(new Exception("connection refused")));
    }

    /// <summary>
    /// A loopback listener that answers one request with <c>200</c> and a small JSON body, and reports the
    /// <c>Host</c> header it was sent — what a tunnel's local end would pass through to the server.
    /// </summary>
    private sealed class OneRequestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));

        public OneRequestServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            HostHeader = ServeAsync();
        }

        public int Port { get; }

        public Task<string?> HostHeader { get; }

        private async Task<string?> ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            const string body = """{"name":"node-1","version":{"number":"9.0.0"}}""";
            var answer = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n"
                         + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer), _stop.Token);
            await stream.FlushAsync(_stop.Token);

            return received.ToString()
                .Split("\r\n")
                .Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["Host:".Length..].Trim())
                .FirstOrDefault();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await HostHeader;
            }
            catch (Exception)
            {
                // A test that failed before the request leaves the accept to be ended by the stop.
            }
            _stop.Dispose();
        }
    }
}
