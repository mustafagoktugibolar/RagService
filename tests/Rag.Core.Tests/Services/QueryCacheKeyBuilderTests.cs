using Rag.Core.Services;

namespace Rag.Core.Tests.Services;

public sealed class QueryCacheKeyBuilderTests
{
    private static RetrievalFingerprint Base => new()
    {
        Collection = "c",
        IndexVersion = "v1",
        EmbeddingProvider = "OpenAI",
        EmbeddingModel = "m",
        VectorSize = 8,
        Strategy = "dense",
        TopK = 5,
        Query = "q",
        Filters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }
    };

    [Fact]
    public void Build_IsDeterministic() =>
        Assert.Equal(QueryCacheKeyBuilder.Build(Base), QueryCacheKeyBuilder.Build(Base with { }));

    [Fact]
    public void Build_FilterInsertionOrderDoesNotMatter() =>
        Assert.Equal(
            QueryCacheKeyBuilder.Build(Base),
            QueryCacheKeyBuilder.Build(Base with { Filters = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" } }));

    [Fact]
    public void Build_NullAndEmptyFiltersAreEquivalent() =>
        Assert.Equal(
            QueryCacheKeyBuilder.Build(Base with { Filters = null }),
            QueryCacheKeyBuilder.Build(Base with { Filters = new Dictionary<string, string>() }));

    public static TheoryData<string, RetrievalFingerprint> Variants => new()
    {
        { "collection", Base with { Collection = "c2" } },
        { "index version", Base with { IndexVersion = "v2" } },
        { "provider", Base with { EmbeddingProvider = "AzureOpenAI" } },
        { "model", Base with { EmbeddingModel = "m2" } },
        { "vector size", Base with { VectorSize = 16 } },
        { "strategy", Base with { Strategy = "hybrid" } },
        { "topK", Base with { TopK = 6 } },
        { "query", Base with { Query = "q2" } },
        { "filter value", Base with { Filters = new Dictionary<string, string> { ["a"] = "x", ["b"] = "2" } } },
        { "filter removed", Base with { Filters = null } },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Build_ChangesWhenAnyFieldChanges(string field, RetrievalFingerprint changed) =>
        Assert.NotEqual(QueryCacheKeyBuilder.Build(Base), QueryCacheKeyBuilder.Build(changed));

    [Fact]
    public void Build_ProviderNameIsCaseInsensitive() =>
        Assert.Equal(
            QueryCacheKeyBuilder.Build(Base),
            QueryCacheKeyBuilder.Build(Base with { EmbeddingProvider = "openai" }));

    [Fact]
    public void Build_AdjacentFieldsCannotImitateEachOther() =>
        Assert.NotEqual(
            QueryCacheKeyBuilder.Build(Base with { Collection = "ab", Query = "c" }),
            QueryCacheKeyBuilder.Build(Base with { Collection = "a", Query = "bc" }));

    [Fact]
    public void Build_KeyCarriesSchemaVersionPrefix() =>
        Assert.StartsWith($"rag:query:v{QueryCacheKeyBuilder.SchemaVersion}:", QueryCacheKeyBuilder.Build(Base));
}
