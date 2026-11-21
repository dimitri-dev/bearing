using System.Text;
using System.Text.Json;
using Bearing.Data.Search;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// An Elasticsearch / OpenSearch mapping flattened into the schema tree's columns (SC11): objects contribute their
/// children dotted and are not columns themselves, a nested field is both, multi-fields follow their field, alias
/// fields and unknown types are kept with their type text, the server's order is kept, and a pathological depth
/// costs a short list rather than the stack. Pure — no server (§2.5).
/// </summary>
public class SearchMappingsTests
{
    [Fact]
    public void An_object_contributes_its_children_dotted_and_is_not_a_column_itself()
    {
        var fields = Flatten("""
            {"user":{"properties":{
                "name":{"type":"keyword"},
                "address":{"type":"object","properties":{"city":{"type":"text"},"zip":{"type":"keyword"}}}}}}
            """);

        Assert.Equal(
            [("user.name", "keyword"), ("user.address.city", "text"), ("user.address.zip", "keyword")],
            fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void A_nested_field_is_a_column_of_type_nested_and_also_lists_its_children()
    {
        var fields = Flatten("""
            {"comments":{"type":"nested","properties":{"author":{"type":"keyword"},"stars":{"type":"integer"}}}}
            """);

        Assert.Equal(
            [("comments", "nested"), ("comments.author", "keyword"), ("comments.stars", "integer")],
            fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void Multi_fields_follow_their_field_as_name_dot_sub()
    {
        var fields = Flatten("""
            {"title":{"type":"text","fields":{"keyword":{"type":"keyword","ignore_above":256},"raw":{"type":"wildcard"}}},
             "n":{"type":"long"}}
            """);

        Assert.Equal(
            [("title", "text"), ("title.keyword", "keyword"), ("title.raw", "wildcard"), ("n", "long")],
            fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void An_alias_field_is_listed_with_type_alias_and_its_path_is_not_followed()
    {
        var fields = Flatten("""
            {"route_length":{"type":"alias","path":"distance"},"distance":{"type":"long"}}
            """);

        Assert.Equal([("route_length", "alias"), ("distance", "long")], fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void A_field_with_neither_type_nor_properties_is_an_empty_object_column()
    {
        // ES and OpenSearch read a missing type as object; with nothing under it, the field itself is what there is.
        var fields = Flatten("""
            {"meta":{"enabled":false},"empty":{"type":"object","properties":{}},"untyped":{"properties":{"a":{"type":"keyword"}}}}
            """);

        Assert.Equal(
            [("meta", "object"), ("empty", "object"), ("untyped.a", "keyword")],
            fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void An_unknown_type_is_kept_with_its_own_text()
    {
        var fields = Flatten("""
            {"embedding":{"type":"dense_vector","dims":3},"loc":{"type":"geo_point"},"tags":{"type":"flattened"}}
            """);

        Assert.Equal(
            [("embedding", "dense_vector"), ("loc", "geo_point"), ("tags", "flattened")],
            fields.Select(f => (f.Path, f.Type)));
    }

    [Fact]
    public void The_servers_order_is_kept_not_sorted()
    {
        var fields = Flatten("""{"zeta":{"type":"long"},"alpha":{"type":"keyword"},"mid":{"type":"date"}}""");

        Assert.Equal(["zeta", "alpha", "mid"], fields.Select(f => f.Path));
    }

    [Fact]
    public void Depth_is_bounded_the_deepest_listed_field_keeps_its_own_type_and_nothing_below_it_is_listed()
    {
        // 40 object levels, then a leaf: far past the bound, and deep enough that an unbounded walk would be wrong.
        var json = new StringBuilder();
        for (var i = 0; i < 40; i++) json.Append("{\"f").Append(i).Append("\":{\"properties\":");
        json.Append("{\"leaf\":{\"type\":\"keyword\"}}");
        for (var i = 0; i < 40; i++) json.Append("}}");

        using var document = JsonDocument.Parse(json.ToString(), new JsonDocumentOptions { MaxDepth = 256 });
        var fields = SearchMappings.Flatten(document.RootElement);

        var only = Assert.Single(fields);
        Assert.Equal(SearchMappings.MaxDepth, only.Path.Split('.').Length);
        Assert.Equal("object", only.Type);
        Assert.StartsWith("f0.f1.f2.", only.Path, StringComparison.Ordinal);
        Assert.EndsWith(".f19", only.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_at_the_bound_lists_every_field()
    {
        // 19 object levels and a leaf: 20 segments, exactly the bound.
        var json = new StringBuilder();
        for (var i = 0; i < 19; i++) json.Append("{\"f").Append(i).Append("\":{\"properties\":");
        json.Append("{\"leaf\":{\"type\":\"keyword\"}}");
        for (var i = 0; i < 19; i++) json.Append("}}");

        var only = Assert.Single(Flatten(json.ToString()));
        Assert.Equal(SearchMappings.MaxDepth, only.Path.Split('.').Length);
        Assert.EndsWith(".leaf", only.Path, StringComparison.Ordinal);
        Assert.Equal("keyword", only.Type);
    }

    [Fact]
    public void Fields_reads_the_properties_of_a_mappings_object_and_an_index_with_none_has_no_fields()
    {
        using var mapped = JsonDocument.Parse("""{"dynamic":"strict","properties":{"a":{"type":"keyword"}}}""");
        using var bare = JsonDocument.Parse("{}");

        Assert.Equal(["a"], SearchMappings.Fields(mapped.RootElement).Select(f => f.Path));
        Assert.Empty(SearchMappings.Fields(bare.RootElement));
    }

    [Fact]
    public void ByIndex_keys_each_indexs_fields_by_its_name()
    {
        using var document = JsonDocument.Parse("""
            {"logs":{"mappings":{"properties":{"msg":{"type":"text"}}}},
             "empty":{"mappings":{}},
             "odd":{}}
            """);

        var byIndex = SearchMappings.ByIndex(document.RootElement);

        Assert.Equal(["msg"], byIndex["logs"].Select(f => f.Path));
        Assert.Empty(byIndex["empty"]);
        Assert.Empty(byIndex["odd"]);
    }

    [Fact]
    public void ByIndex_refuses_a_body_that_is_not_an_object()
    {
        using var document = JsonDocument.Parse("[]");

        Assert.Throws<JsonException>(() => SearchMappings.ByIndex(document.RootElement));
    }

    [Fact]
    public void Merge_keeps_first_seen_order_and_names_both_types_when_indices_disagree()
    {
        IReadOnlyList<SearchField> first = [new("msg", "keyword"), new("n", "long")];
        IReadOnlyList<SearchField> second = [new("host", "keyword"), new("msg", "text"), new("n", "long")];
        IReadOnlyList<SearchField> third = [new("msg", "keyword"), new("msg2", "match_only_text")];

        var merged = SearchMappings.Merge([first, second, third]);

        Assert.Equal(
            [("msg", "keyword | text"), ("n", "long"), ("host", "keyword"), ("msg2", "match_only_text")],
            merged.Select(f => (f.Path, f.Type)));
    }

    private static IReadOnlyList<SearchField> Flatten(string properties)
    {
        using var document = JsonDocument.Parse(properties);

        return SearchMappings.Flatten(document.RootElement);
    }
}
