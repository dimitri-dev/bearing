using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.MySql;

namespace Bearing.Data.Search;

/// <summary>
/// The schema tree's data for an Elasticsearch or OpenSearch cluster (spec, Behaviour 6), read over the
/// connection's one HTTP door (<see cref="SearchConnectionFactory.SendAsync"/>). Both servers answer the same REST
/// calls with the same shapes, so one reader serves both.
/// <para>
/// <b>One database, named after the host, with one schema named <c>""</c></b> — the DynamoDB arrangement: neither
/// server has either, and an empty schema keeps every generated statement unqualified. The name is the
/// connection's real host (<see cref="SearchConnectionFactory.Info"/>), never a tunnel's loopback end.
/// </para>
/// <para>
/// <b>Relations.</b> Open indices (<c>_cat/indices</c>) and data streams (<c>_data_stream</c>) are tables; aliases
/// (<c>_alias</c>) are views, and "Show definition" lists what they point at. <b>Hidden and system indices are never
/// listed</b> — a name starting with <c>.</c> (data-stream backing indices <c>.ds-…</c> included) is left out
/// whatever the server returned, and so is an alias whose every target is left out. They can still be queried by name.
/// </para>
/// <para>
/// <b>Columns</b> are the mapped fields (<c>_mapping</c>), flattened to dotted names by <see cref="SearchMappings"/>.
/// None is <see cref="ColumnInfo.NotNull"/> (a document may leave any field out) and none is a key: <c>_id</c> is the
/// only identity a document has, and SQL cannot select it. An alias takes its targets' fields merged
/// (<see cref="SearchMappings.Merge"/>); a data stream takes its newest backing index's.
/// </para>
/// <para>
/// <b>Absence stays typed (§1.1, §9.9).</b> A kind the server does not serve is an empty list only where that is
/// known to be the answer: <c>_data_stream</c> answering 400, 404 or 405 (a service without data streams). Every
/// other refusal or failure fails the read with a sentence naming the call — a refused list is never shown as an
/// empty one. A 401 is the factory's sign-in sentence. Nothing credential-bearing is read (§1.8): no
/// <c>_security</c>, <c>_nodes</c> or cluster settings call is made.
/// </para>
/// <para>
/// <b>Ids are derived, not read</b> — <see cref="MySqlMetadataReader.StableId"/>, a hash of the name in the empty
/// schema, resolved back by re-hashing the current relation list (a name the server no longer lists is "not
/// found"). Index, alias and data-stream names share one namespace on both servers, so the name alone is the key.
/// Two names hashing alike resolve to the first listed, as the MySQL reader's resolution does.
/// </para>
/// </summary>
public sealed class SearchMetadataReader : IMetadataReader
{
    /// <summary>The ceiling on one mapping response — a cluster with thousands of indices answers <c>_mapping</c>
    /// with a very large body (spec, Risks).</summary>
    internal const long MappingLimitBytes = SearchConnectionFactory.DefaultResponseLimitBytes;

    /// <summary>The longest path one follow-up mapping request for data streams is given; more streams than fit
    /// are asked for in further requests, so a cluster with many streams never sends a line the server refuses.</summary>
    internal const int MaxStreamMappingPathLength = 2000;

    internal const string IndicesPath = "/_cat/indices?format=json&bytes=b&h=index,docs.count,store.size&expand_wildcards=open";
    internal const string AliasesPath = "/_alias";
    internal const string DataStreamsPath = "/_data_stream";
    internal const string MappingsPath = "/*/_mapping?expand_wildcards=open";

    private const string Schema = "";

    /// <summary>A mapping response nests two JSON levels per field level; this leaves room past
    /// <see cref="SearchMappings.MaxDepth"/> for an index whose depth limit was raised.</summary>
    private const int MappingJsonDepth = 256;

    private static readonly Call Indices = new("listing the indices", "_cat/indices");
    private static readonly Call Aliases = new("listing the aliases", "_alias");
    private static readonly Call DataStreams = new("listing the data streams", "_data_stream");
    private static readonly Call Mappings = new("reading the field mappings", "_mapping");

    private static readonly JsonWriterOptions CompactJson = new()
    {
        Indented = false,

        // Display text inside a comment: readable quotes and non-ASCII. Control characters are still escaped, so
        // a filter can never break out of its comment line.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SearchConnectionFactory _factory;

    public SearchMetadataReader(SearchConnectionFactory factory) => _factory = factory;

    private string EngineName => SearchErrors.EngineName(_factory.Engine);

    /// <summary>The one database, named after the connection's real host. No request.</summary>
    private string DatabaseName => _factory.Info.Host?.Trim() ?? "";

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([DatabaseName]);

    /// <summary>
    /// Indices, aliases and data streams, then the mappings of everything listed — <c>/*/_mapping</c> once, plus one
    /// request for the data streams whose newest backing index that answer did not include (backing indices are
    /// hidden, and whether <c>*</c> reaches them is the server's call). Any failure fails the whole read; nothing
    /// partial is returned.
    /// </summary>
    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        var indices = await ReadIndicesAsync(ct).ConfigureAwait(false);
        var aliases = await ReadAliasesAsync(ct).ConfigureAwait(false);
        var streams = await ReadDataStreamsAsync(ct).ConfigureAwait(false);

        var indexNames = indices.Select(index => index.Name).ToHashSet(StringComparer.Ordinal);
        var streamNames = streams.Select(stream => stream.Name).ToHashSet(StringComparer.Ordinal);
        var listedAliases = aliases
            .Where(alias => alias.Targets.Any(target => indexNames.Contains(target.Name) || streamNames.Contains(target.Name)))
            .ToList();

        var relations = new List<(string Name, RelationKind Kind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in indexNames.Concat(streamNames))
        {
            if (seen.Add(name)) relations.Add((name, RelationKind.Table));
        }
        foreach (var alias in listedAliases)
        {
            if (seen.Add(alias.Name)) relations.Add((alias.Name, RelationKind.View));
        }
        relations.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var mappings = relations.Count == 0
            ? new Dictionary<string, IReadOnlyList<SearchField>>(StringComparer.Ordinal)
            : await ReadMappingsAsync(MappingsPath, ct).ConfigureAwait(false);

        var unmapped = streams
            .Where(stream => stream.Newest is { } newest && !mappings.ContainsKey(newest))
            .Select(stream => stream.Name)
            .ToList();
        foreach (var path in StreamMappingPaths(unmapped))
        {
            foreach (var (index, fields) in await ReadMappingsAsync(path, ct).ConfigureAwait(false))
            {
                mappings.TryAdd(index, fields);
            }
        }

        var streamFields = streams.ToDictionary(
            stream => stream.Name,
            stream => stream.Newest is { } newest ? mappings.GetValueOrDefault(newest, []) : [],
            StringComparer.Ordinal);

        IReadOnlyList<SearchField> FieldsOf(string target)
            => streamFields.TryGetValue(target, out var fields) ? fields : mappings.GetValueOrDefault(target, []);

        var aliasesByName = listedAliases.ToDictionary(alias => alias.Name, StringComparer.Ordinal);
        var tables = new List<TableInfo>(relations.Count);
        var columns = new List<ColumnInfo>();

        foreach (var (name, kind) in relations)
        {
            var id = StableId(name);
            tables.Add(new TableInfo(id, Schema, name, kind));

            var fields = kind == RelationKind.View
                ? SearchMappings.Merge(aliasesByName[name].Targets
                    .Where(target => indexNames.Contains(target.Name) || streamNames.Contains(target.Name))
                    .Select(target => FieldsOf(target.Name)))
                : FieldsOf(name);

            columns.AddRange(fields.Select((field, i) => new ColumnInfo(
                TableId: id,
                Ordinal: i + 1,
                Name: field.Path,
                DataType: field.Type,
                NotNull: false,
                IsPrimaryKey: false)));
        }

        string[] schemas = [Schema];

        return new SchemaSnapshot(database, schemas, tables, columns, [], searchPath: schemas);
    }

    public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoutineInfo>>([]);

    /// <summary>
    /// An alias's "definition": what it points at, as comment lines (<c>-- alias "logs" points at: a, b</c>), plus a
    /// filter or routing line only when <c>_alias</c> reported one. It is not SQL and every line is a comment, so it
    /// cannot run by accident. Read fresh from <c>_alias</c>, so it is the alias as it is now. A target that is left out
    /// of the tree (a name starting with <c>.</c>) is counted, not named. An id that is no longer a listed alias
    /// answers an empty text, as the MySQL reader does for a view that is gone.
    /// </summary>
    public async Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
    {
        var aliases = await ReadAliasesAsync(ct).ConfigureAwait(false);
        var alias = aliases.FirstOrDefault(candidate => StableId(candidate.Name) == tableId);

        return alias is null ? "" : AliasDefinition(alias);
    }

    /// <summary>No constraints, indexes, triggers or policies: neither server has any Bearing could show. No request.</summary>
    public Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
        => Task.FromResult(TableDetails.Empty);

    /// <summary>
    /// One entry per listed index from <c>_cat/indices</c>: <c>store.size</c> as total and table bytes (it is the
    /// store as the server reports it, replica copies included), <c>docs.count</c> as the row estimate (the
    /// server's count, which includes nested documents). An index without a readable <c>store.size</c> gets no entry,
    /// and an unreadable <c>docs.count</c> is an unknown count — neither is ever reported as 0 (§5.6).
    /// <para>
    /// Aliases and data streams get no entry in this version: summing their backing indices would count an index
    /// behind two aliases twice and would read as a measured size, which it is not.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
    {
        var indices = await ReadIndicesAsync(ct).ConfigureAwait(false);

        return indices
            .Where(index => index.StoreBytes is not null)
            .Select(index => new RelationSize(
                TableId: StableId(index.Name),
                TotalBytes: index.StoreBytes!.Value,
                TableBytes: index.StoreBytes.Value,
                IndexBytes: 0,
                ToastBytes: 0,
                EstimatedRows: index.Documents))
            .ToList();
    }

    /// <summary>The one database with no size: Bearing does not add up the indices' (see
    /// <see cref="GetRelationSizesAsync"/>), and the cluster reports no figure of its own that this reads.</summary>
    public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DatabaseSize>>([new DatabaseSize(DatabaseName, null)]);

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => Task.FromException<string>(new NotSupportedException($"{EngineName} has no stored routines."));

    public Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
        => Task.FromResult(DatabaseObjectKinds.Empty);

    /// <summary>No roles are read: the security APIs that hold them are not called (§1.8).</summary>
    public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

    /// <summary>Not visible rather than "none": privileges exist on both servers, this reader does not read them.</summary>
    public Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
        => Task.FromResult(RoleGrants.NotVisible);

    public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);

    // ---- pure parts --------------------------------------------------------------------------------

    /// <summary>A relation id: the name hashed in the empty schema (see the class remarks).</summary>
    internal static long StableId(string name) => MySqlMetadataReader.StableId(Schema, name);

    /// <summary>A name the tree never lists: hidden and system indices, backing indices, and dot-named aliases.</summary>
    internal static bool IsHidden(string name) => name.StartsWith('.');

    /// <summary>The sentence for a mapping answer over <paramref name="limitBytes"/>.</summary>
    internal static string MappingsTooLargeSentence(long limitBytes)
        => $"The field mappings are larger than {MegaBytes(limitBytes)}, the most Bearing reads for one request, so the "
           + "schema tree was not loaded. Queries still run: write the index name in the editor.";

    /// <summary>
    /// A refused or failed metadata call, naming it. A 403 is a refusal — a privilege, not a sign-in — and says
    /// so; the reason is the server's own, scrubbed (<see cref="SearchErrors.ToQueryError(int, byte[], string?, string?)"/>),
    /// and nothing is guessed past it.
    /// </summary>
    internal static string CallFailedSentence(SearchEngine engine, string doing, string api, int status, string reason)
    {
        var name = SearchErrors.EngineName(engine);
        var code = status.ToString(CultureInfo.InvariantCulture);
        var why = reason.Trim().TrimEnd('.');

        return status == 403
            ? $"{name} refused {doing} ({api}, HTTP 403): {why}."
            : $"{name} answered {doing} ({api}) with HTTP {code}: {why}.";
    }

    /// <summary>The sentence for an answer that is not the JSON shape the call returns.</summary>
    internal static string UnreadableSentence(SearchEngine engine, string doing, string api)
        => $"{SearchErrors.EngineName(engine)} answered {doing} ({api}) with something Bearing could not read as the "
           + "expected JSON.";

    /// <summary>
    /// The comment text <see cref="GetViewDefinitionAsync"/> returns for <paramref name="alias"/>: the listed targets,
    /// a count of the left-out ones, then a filter / routing line when any target has one — once when every listed
    /// target has the same, else once per target.
    /// </summary>
    internal static string AliasDefinition(AliasEntry alias)
    {
        var listed = alias.Targets.Where(target => !IsHidden(target.Name)).ToList();
        var hidden = alias.Targets.Count - listed.Count;
        var text = new StringBuilder();

        text.Append("-- alias \"").Append(OneLine(alias.Name)).Append("\" points at: ");
        text.Append(listed.Count == 0 ? "nothing listed here" : string.Join(", ", listed.Select(target => OneLine(target.Name))));
        text.Append('\n');

        if (hidden > 0)
        {
            text.Append("-- and ")
                .Append(hidden.ToString(CultureInfo.InvariantCulture))
                .Append(hidden == 1 ? " hidden or system index" : " hidden or system indices")
                .Append(" (names starting with \".\"), not listed\n");
        }

        Notes("filtered", target => target.Filter);
        Notes("index routing", target => target.IndexRouting);
        Notes("search routing", target => target.SearchRouting);

        return text.ToString();

        void Notes(string label, Func<AliasTarget, string?> value)
        {
            var having = listed.Where(target => value(target) is not null).ToList();
            if (having.Count == 0) return;

            if (having.Count == listed.Count && having.Select(value).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                text.Append("-- ").Append(label).Append(": ").Append(value(having[0])).Append('\n');
                return;
            }

            foreach (var target in having)
            {
                text.Append("-- ").Append(label).Append(" on ").Append(OneLine(target.Name)).Append(": ")
                    .Append(value(target)).Append('\n');
            }
        }
    }

    /// <summary>
    /// The follow-up mapping requests for <paramref name="streams"/>: <c>/a,b,…/_mapping</c>, each name escaped,
    /// split so no path passes <see cref="MaxStreamMappingPathLength"/>. One request in the ordinary case.
    /// </summary>
    internal static IReadOnlyList<string> StreamMappingPaths(IReadOnlyList<string> streams)
    {
        var paths = new List<string>();
        var names = new StringBuilder();

        foreach (var escaped in streams.Select(Uri.EscapeDataString))
        {
            if (names.Length > 0 && names.Length + 1 + escaped.Length + "//_mapping".Length > MaxStreamMappingPathLength)
            {
                paths.Add($"/{names}/_mapping");
                names.Clear();
            }

            if (names.Length > 0) names.Append(',');
            names.Append(escaped);
        }

        if (names.Length > 0) paths.Add($"/{names}/_mapping");

        return paths;
    }

    /// <summary>One <c>_cat/indices</c> row: the name, the document count and store bytes, each null when the server
    /// sent none or something that is not a whole number.</summary>
    internal sealed record IndexEntry(string Name, long? Documents, long? StoreBytes);

    /// <summary>One data stream and its newest backing index (the last one listed), null when it listed none.</summary>
    internal sealed record StreamEntry(string Name, string? Newest);

    /// <summary>One alias and every index or data stream it points at, in the order <c>_alias</c> listed them.</summary>
    internal sealed record AliasEntry(string Name, IReadOnlyList<AliasTarget> Targets);

    /// <summary>One alias target with its filter (compact JSON) and routing values, each null when not set.</summary>
    internal sealed record AliasTarget(string Name, string? Filter, string? IndexRouting, string? SearchRouting);

    /// <summary><c>_cat/indices</c> rows, dot-named ones left out.</summary>
    internal static IReadOnlyList<IndexEntry> ParseIndices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("_cat/indices answers an array.");

        var indices = new List<IndexEntry>();
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (Text(row, "index") is not { } name || IsHidden(name)) continue;

            indices.Add(new IndexEntry(name, WholeNumber(row, "docs.count"), WholeNumber(row, "store.size")));
        }

        return indices;
    }

    /// <summary><c>_data_stream</c>'s streams, leaving out dot-named, hidden and system ones.</summary>
    internal static IReadOnlyList<StreamEntry> ParseDataStreams(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data_streams", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("_data_stream answers {\"data_streams\": [...]}.");
        }

        var streams = new List<StreamEntry>();
        foreach (var stream in list.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object) continue;
            if (Text(stream, "name") is not { } name || IsHidden(name)) continue;
            if (IsTrue(stream, "hidden") || IsTrue(stream, "system")) continue;

            string? newest = null;
            if (stream.TryGetProperty("indices", out var backing) && backing.ValueKind == JsonValueKind.Array)
            {
                foreach (var index in backing.EnumerateArray())
                {
                    if (index.ValueKind == JsonValueKind.Object && Text(index, "index_name") is { } indexName)
                    {
                        newest = indexName;
                    }
                }
            }

            streams.Add(new StreamEntry(name, newest));
        }

        return streams;
    }

    /// <summary>
    /// <c>_alias</c> turned inside out: each alias with its targets, in first-seen order. A dot-named alias and one
    /// marked <c>is_hidden</c> are left out; targets are kept whole here (the caller decides which are listed).
    /// </summary>
    internal static IReadOnlyList<AliasEntry> ParseAliases(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("_alias answers an object keyed by index.");

        var order = new List<string>();
        var targets = new Dictionary<string, List<AliasTarget>>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var index in root.EnumerateObject())
        {
            if (index.Value.ValueKind != JsonValueKind.Object
                || !index.Value.TryGetProperty("aliases", out var aliases)
                || aliases.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var alias in aliases.EnumerateObject())
            {
                if (IsHidden(alias.Name)) continue;

                var settings = alias.Value;
                if (settings.ValueKind == JsonValueKind.Object && IsTrue(settings, "is_hidden")) hidden.Add(alias.Name);

                if (!targets.TryGetValue(alias.Name, out var list))
                {
                    targets[alias.Name] = list = [];
                    order.Add(alias.Name);
                }

                list.Add(settings.ValueKind == JsonValueKind.Object
                    ? new AliasTarget(
                        index.Name,
                        settings.TryGetProperty("filter", out var filter) && filter.ValueKind != JsonValueKind.Null
                            ? Compact(filter)
                            : null,
                        Scalar(settings, "index_routing"),
                        Scalar(settings, "search_routing"))
                    : new AliasTarget(index.Name, null, null, null));
            }
        }

        return order
            .Where(name => !hidden.Contains(name))
            .Select(name => new AliasEntry(name, targets[name]))
            .ToList();
    }

    // ---- reads -------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<IndexEntry>> ReadIndicesAsync(CancellationToken ct)
    {
        using var document = await GetJsonAsync(Indices, IndicesPath, SearchConnectionFactory.DefaultResponseLimitBytes, ct)
            .ConfigureAwait(false);

        return Read(Indices, () => ParseIndices(document.RootElement));
    }

    private async Task<IReadOnlyList<AliasEntry>> ReadAliasesAsync(CancellationToken ct)
    {
        using var document = await GetJsonAsync(Aliases, AliasesPath, SearchConnectionFactory.DefaultResponseLimitBytes, ct)
            .ConfigureAwait(false);

        return Read(Aliases, () => ParseAliases(document.RootElement));
    }

    /// <summary>
    /// The data streams — the one call that is best-effort on its own: 400, 404 or 405 means the server or service
    /// has no data streams (OpenSearch Serverless), and is "none". A 403 and every other failure fail the read, as
    /// for the other calls: a stream list the user was refused is not an empty one.
    /// </summary>
    private async Task<IReadOnlyList<StreamEntry>> ReadDataStreamsAsync(CancellationToken ct)
    {
        var response = await _factory
            .SendAsync(HttpMethod.Get, DataStreamsPath, null, SearchConnectionFactory.DefaultResponseLimitBytes, ct)
            .ConfigureAwait(false);

        if (response.StatusCode is 400 or 404 or 405) return [];
        if (!response.IsSuccess) throw Failed(DataStreams, response);

        using var document = Parse(DataStreams, response.Body, maxDepth: 64);

        return Read(DataStreams, () => ParseDataStreams(document.RootElement));
    }

    /// <summary>One get-mapping request, read with <see cref="MappingLimitBytes"/> as the ceiling; over it, the
    /// snapshot fails with <see cref="MappingsTooLargeSentence"/>.</summary>
    private async Task<Dictionary<string, IReadOnlyList<SearchField>>> ReadMappingsAsync(string path, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await GetJsonAsync(Mappings, path, MappingLimitBytes, ct, MappingJsonDepth).ConfigureAwait(false);
        }
        catch (SearchResponseTooLargeException e)
        {
            throw new SearchMetadataException(MappingsTooLargeSentence(e.LimitBytes));
        }

        using (document)
        {
            return Read(Mappings, () => SearchMappings.ByIndex(document.RootElement));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Call call, string path, long limit, CancellationToken ct, int maxDepth = 64)
    {
        var response = await _factory.SendAsync(HttpMethod.Get, path, null, limit, ct).ConfigureAwait(false);
        if (!response.IsSuccess) throw Failed(call, response);

        return Parse(call, response.Body, maxDepth);
    }

    private JsonDocument Parse(Call call, byte[] body, int maxDepth)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth });
        }
        catch (JsonException)
        {
            throw new SearchMetadataException(UnreadableSentence(_factory.Engine, call.Doing, call.Api));
        }
    }

    private T Read<T>(Call call, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new SearchMetadataException(UnreadableSentence(_factory.Engine, call.Doing, call.Api));
        }
    }

    /// <summary>A non-2xx answer as the exception the tree shows: the call named, the server's reason scrubbed, and
    /// the server's error type kept as the code so the failure classifies like a statement's.</summary>
    private SearchServerException Failed(Call call, SearchResponse response)
    {
        var error = SearchErrors.ToQueryError(response.StatusCode, response.Body, null, null);

        return new SearchServerException(
            response.StatusCode,
            new QueryError(
                CallFailedSentence(_factory.Engine, call.Doing, call.Api, response.StatusCode, error.Message),
                error.SqlState,
                null));
    }

    // ---- JSON helpers ------------------------------------------------------------------------------

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A routing value: a string or a number as text, else null.</summary>
    private static string? Scalar(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => OneLine(value.GetString() ?? ""),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static bool IsTrue(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A count or a byte size as <c>_cat</c> writes it — a string of digits (or a number) — or null for anything
    /// else: missing, <c>null</c>, blank, signed, fractional. Decided here, before anything reads it as a number,
    /// so an unknown is never a 0 (§5.6).
    /// </summary>
    private static long? WholeNumber(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var number) && number >= 0 => number,
            _ => null,
        };
    }

    private static string Compact(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CompactJson))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A name or value on one comment line: any control character becomes a space.</summary>
    private static string OneLine(string text)
        => text.Any(char.IsControl) ? new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()) : text;

    private static string MegaBytes(long bytes)
        => bytes % (1024 * 1024) == 0
            ? $"{(bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB"
            : $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes";

    /// <summary>A metadata call: what it does, in words, and the API it calls — both named when it fails.</summary>
    private sealed record Call(string Doing, string Api);
}

/// <summary>
/// A metadata read that got an answer it cannot use: one over the mapping ceiling, or a body that is not the JSON
/// the call returns. The message is the whole sentence; nothing the server sent is in it.
/// </summary>
internal sealed class SearchMetadataException : Exception
{
    public SearchMetadataException(string message) : base(message)
    {
    }
}
