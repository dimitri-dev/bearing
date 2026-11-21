using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.Search;
using Bearing.Data.SqlServer;

namespace Bearing.Data;

/// <summary>Default provider registry: every engine this build ships with, keyed by
/// <see cref="IDbProvider.Id"/>. The order is the order the connection dialog offers them in, so
/// PostgreSQL — the engine every existing project file names — stays first, the relational engines follow, and
/// the non-relational ones come after them: Amazon DynamoDB, then Elasticsearch and OpenSearch (one
/// <see cref="SearchProvider"/> class, two ids — each paired with its own dialect).</summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IDbProvider> _providers;

    public ProviderRegistry(IEnumerable<IDbProvider>? providers = null)
    {
        var list = providers?.ToList()
            ?? new List<IDbProvider>
            {
                new PostgresProvider(), new SqlServerProvider(), new MySqlProvider(), new DynamoDbProvider(),
                SearchProvider.Elasticsearch(), SearchProvider.OpenSearch(),
            };
        _providers = list.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IDbProvider Get(string providerId) =>
        _providers.TryGetValue(providerId, out var p)
            ? p
            : throw new KeyNotFoundException($"No database provider registered for '{providerId}'.");

    public IReadOnlyCollection<IDbProvider> All => _providers.Values;
}
