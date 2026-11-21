using System.Text.Json;

namespace Bearing.Data.Search;

/// <summary>One mapped field as the schema tree lists it: its dotted path and its mapping type.</summary>
/// <param name="Path">The field's full name, parents first (<c>user.name</c>, <c>title.keyword</c>) — the name a
/// SQL statement selects it by.</param>
/// <param name="Type">The mapping's <c>type</c> as the server wrote it (<c>keyword</c>, <c>nested</c>,
/// <c>alias</c>, …); see <see cref="SearchMappings"/> for the two cases without one.</param>
internal sealed record SearchField(string Path, string Type);

/// <summary>
/// An index mapping's <c>properties</c> flattened into the columns the schema tree shows (spec, Behaviour 6) — the
/// same for Elasticsearch and OpenSearch, whose mapping shapes are one. Pure, so every rule is a unit test
/// (§2.5); the reader (<see cref="SearchMetadataReader"/>) only fetches.
/// <para>
/// <b>The rules.</b> A field with a <c>type</c> and no children is one column of that type, whatever the type is
/// (an unknown or future type is kept as its text — nothing is dropped for not being recognised). An object (a
/// field with <c>properties</c>, typed <c>object</c> or not typed at all) is not a column itself: it contributes
/// its children as <c>parent.child</c>. A <c>nested</c> field is both — a column of type <c>nested</c>, then its
/// children dotted — because a nested field is a thing SQL can name on its own. Multi-fields (<c>fields</c>)
/// follow their field as <c>name.sub</c>. A <c>type: alias</c> field is listed with type <c>alias</c> (its
/// <c>path</c> is not followed: the target is listed in its own place). A field with neither <c>type</c> nor
/// children is listed with type <see cref="ObjectType"/>: a mapping that omits <c>type</c> means an object, and
/// one with no children is an empty one.
/// </para>
/// <para>
/// <b>Order is the server's.</b> <see cref="JsonElement.EnumerateObject"/> walks members as they were written, so
/// the ordinals follow the mapping response. <b>Depth is bounded</b> (<see cref="MaxDepth"/>): a field that deep
/// is still listed with its own type, its children are not — so a pathological mapping costs a short list, never
/// the stack.
/// </para>
/// </summary>
internal static class SearchMappings
{
    /// <summary>
    /// The deepest field path listed, in segments. Twenty is both servers' default
    /// <c>index.mapping.depth.limit</c>; an index whose limit was raised past it shows its fields down to this
    /// depth, and the deeper ones can still be named in a statement.
    /// </summary>
    internal const int MaxDepth = 20;

    /// <summary>The type of a field whose mapping has no <c>type</c> — what both servers mean by leaving it out.</summary>
    internal const string ObjectType = "object";

    /// <summary>The type of a nested field, which is a column as well as a parent.</summary>
    internal const string NestedType = "nested";

    /// <summary>What separates the types of one column merged from indices that disagree on it.</summary>
    internal const string TypeSeparator = " | ";

    /// <summary>
    /// The fields of one index's <c>mappings</c> object (the value under <c>{index: {mappings: …}}</c>): its
    /// <c>properties</c>, flattened. An index with no <c>properties</c> (nothing indexed yet) has no fields.
    /// </summary>
    internal static IReadOnlyList<SearchField> Fields(JsonElement mappings)
        => mappings.ValueKind == JsonValueKind.Object
           && mappings.TryGetProperty("properties", out var properties)
            ? Flatten(properties)
            : [];

    /// <summary>A <c>properties</c> object flattened by the rules in the class remarks, in the server's order.</summary>
    internal static IReadOnlyList<SearchField> Flatten(JsonElement properties)
    {
        var fields = new List<SearchField>();
        Walk(properties, "", 1, fields);

        return fields;
    }

    /// <summary>
    /// A whole get-mapping response (<c>{index: {mappings: {properties: …}}, …}</c>) as each index's fields, keyed by
    /// index name. An entry that is not an object, or has no <c>mappings</c>, maps to no fields. A body that is not a
    /// JSON object is the caller's to word: this throws <see cref="JsonException"/>.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<SearchField>> ByIndex(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A mapping response is a JSON object keyed by index name.");
        }

        var byIndex = new Dictionary<string, IReadOnlyList<SearchField>>(StringComparer.Ordinal);

        foreach (var index in response.EnumerateObject())
        {
            byIndex[index.Name] = index.Value.ValueKind == JsonValueKind.Object
                && index.Value.TryGetProperty("mappings", out var mappings)
                    ? Fields(mappings)
                    : [];
        }

        return byIndex;
    }

    /// <summary>
    /// The columns of a relation made of several indices (an alias): every field of every list, in first-seen
    /// order. A name seen again keeps its place; when its type differs, the type names every type seen, first
    /// one first (<c>keyword | text</c>) — a column the indices disagree on says so rather than picking one.
    /// </summary>
    internal static IReadOnlyList<SearchField> Merge(IEnumerable<IReadOnlyList<SearchField>> lists)
    {
        var order = new List<string>();
        var types = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var list in lists)
        {
            foreach (var field in list)
            {
                if (!types.TryGetValue(field.Path, out var seen))
                {
                    types[field.Path] = [field.Type];
                    order.Add(field.Path);
                    continue;
                }

                if (!seen.Contains(field.Type, StringComparer.Ordinal))
                {
                    seen.Add(field.Type);
                }
            }
        }

        return order
            .Select(path => new SearchField(path, string.Join(TypeSeparator, types[path])))
            .ToList();
    }

    private static void Walk(JsonElement properties, string prefix, int depth, List<SearchField> fields)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix + property.Name;
            var definition = property.Value;

            // A definition that is not an object is not a mapping shape either server writes; kept as a column
            // so it is visible rather than silently missing.
            if (definition.ValueKind != JsonValueKind.Object)
            {
                fields.Add(new SearchField(path, ObjectType));
                continue;
            }

            var type = TypeOf(definition);
            var children = Children(definition);
            var descend = children is not null && depth < MaxDepth;

            if (type == NestedType || !descend)
            {
                fields.Add(new SearchField(path, type));
            }

            if (descend)
            {
                Walk(children!.Value, path + ".", depth + 1, fields);
            }
            else if (children is null)
            {
                MultiFields(definition, path, fields);
            }
        }
    }

    /// <summary>A leaf's <c>fields</c>: each one <c>name.sub</c>, with its own type, right after the field.</summary>
    private static void MultiFields(JsonElement definition, string path, List<SearchField> fields)
    {
        if (!definition.TryGetProperty("fields", out var subFields) || subFields.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var sub in subFields.EnumerateObject())
        {
            var type = sub.Value.ValueKind == JsonValueKind.Object ? TypeOf(sub.Value) : ObjectType;
            fields.Add(new SearchField($"{path}.{sub.Name}", type));
        }
    }

    /// <summary>The <c>type</c> text, or <see cref="ObjectType"/> when the mapping leaves it out.</summary>
    private static string TypeOf(JsonElement definition)
        => definition.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(type.GetString())
            ? type.GetString()!
            : ObjectType;

    /// <summary>A non-empty <c>properties</c> object, or null — an empty one has no children to list, so its field
    /// is a column like any leaf.</summary>
    private static JsonElement? Children(JsonElement definition)
        => definition.TryGetProperty("properties", out var properties)
           && properties.ValueKind == JsonValueKind.Object
           && properties.EnumerateObject().Any()
            ? properties
            : null;
}
