using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Retrieval;
using Rag.Core.Services;
using Rag.Core.Tests.Fakes;

namespace Rag.Core.Tests.Reranking;

/// <summary>
/// The six strategies of the reranking experiment, end to end through the real VectorSearchService and the real provider
/// adapters, against a scripted HTTP server: Dense, Dense+OpenAI, Dense+Jev, Hybrid, Hybrid+OpenAI, Hybrid+Jev.
/// </summary>
public sealed class RerankingPipelineTests
{
    private const string Collection = "docs";
    private const int Dim = 2;

    private static readonly RagOptions Rag = new() { EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, DefaultTopK = 5 };

    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>Dense returns insertion order a,b,c,d. Lexical for "target" finds b then d. The 'target' passages deserve the top.</summary>
    private static async Task<LexicalInMemoryVectorStore> Store()
    {
        var store = new LexicalInMemoryVectorStore();
        await store.EnsureCollectionAsync(Collection, Dim, CancellationToken.None);
        await store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = Collection, EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, IndexVersion = "v1"
        }, CancellationToken.None);
        foreach (var (id, text) in new[] { ("a", "alpha filler words"), ("b", "beta target answer"), ("c", "gamma filler words"), ("d", "delta target answer too") })
        {
            await store.UpsertAsync(Collection,
                [new VectorDocument { DocumentId = id, ChunkId = $"{id}:0", Text = text, Embedding = new float[Dim], Metadata = new() { ["topic"] = id } }],
                CancellationToken.None);
        }

        return store;
    }

    public enum Provider { None, OpenAi, Jev }

    private static IReranker? Make(Provider provider, ScriptedHandler handler, RerankingOptions options) => provider switch
    {
        Provider.OpenAi => new OpenAiDecisionsReranker(
            new FakeHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new OpenAiOptions { ApiKey = "sk-test" }),
            Microsoft.Extensions.Options.Options.Create(new OpenAiDecisionsOptions()),
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Reranking = options }),
            NullLogger<OpenAiDecisionsReranker>.Instance, RerankTestData.FastPipeline()),
        Provider.Jev => new JevReranker(
            new FakeHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new JevOptions { ApiKey = "tsk-test" }),
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Reranking = options }),
            NullLogger<JevReranker>.Instance, RerankTestData.FastPipeline()),
        _ => null
    };

    private static VectorSearchService Service(
        IVectorStore store, RetrievalStrategy strategy, Provider provider, ScriptedHandler handler,
        IDistributedCache? cache = null, RerankFailurePolicy policy = RerankFailurePolicy.Fallback, RelevanceMechanism mechanism = RelevanceMechanism.Score,
        int budget = 20)
    {
        var rerank = RerankTestData.Options(mechanism: mechanism);
        rerank.Enabled = provider != Provider.None;
        rerank.FailurePolicy = policy;
        rerank.CandidateBudget = budget;
        var embedder = Substitute.For<IEmbeddingProvider>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f, 0f });
        return new VectorSearchService(
            embedder, store, cache ?? NewCache(),
            Microsoft.Extensions.Options.Options.Create(Rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = cache is not null, TtlMinutes = 5 }),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Strategy = strategy, CandidateK = 20, Reranking = rerank }),
            Make(provider, handler, rerank));
    }

    private static QueryRequest Query(int topK = 4) => new() { RequestId = "q", Collection = Collection, Query = "target", TopK = topK };

    private static double OpenAiScore(string instructions) => instructions.Contains("target") ? 3 : 0.5;

    private static double JevScore(string instructions) => instructions.Contains("target") ? 2.4 : 0.3;

    private static ScriptedHandler HandlerFor(Provider provider, int times = 1) => provider switch
    {
        Provider.OpenAi => new ScriptedHandler().EchoOpenAi(OpenAiScore, times),
        Provider.Jev => new ScriptedHandler().EchoJev(JevScore, times),
        _ => new ScriptedHandler()
    };

    [Theory]
    [InlineData(RetrievalStrategy.Dense, Provider.None, new[] { "a:0", "b:0", "c:0", "d:0" })]
    [InlineData(RetrievalStrategy.Dense, Provider.OpenAi, new[] { "b:0", "d:0", "a:0", "c:0" })]
    [InlineData(RetrievalStrategy.Dense, Provider.Jev, new[] { "b:0", "d:0", "a:0", "c:0" })]
    [InlineData(RetrievalStrategy.Hybrid, Provider.None, new[] { "b:0", "d:0", "a:0", "c:0" })]
    [InlineData(RetrievalStrategy.Hybrid, Provider.OpenAi, new[] { "b:0", "d:0", "a:0", "c:0" })]
    [InlineData(RetrievalStrategy.Hybrid, Provider.Jev, new[] { "b:0", "d:0", "a:0", "c:0" })]
    public async Task EachOfTheSixStrategies_RunsEndToEnd_AndOrdersAsExpected(RetrievalStrategy strategy, Provider provider, string[] expected)
    {
        var service = Service(await Store(), strategy, provider, HandlerFor(provider));

        var response = await service.SearchAsync(Query(), CancellationToken.None);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(expected, response.Results.Select(r => r.ChunkId));
        Assert.Null(response.Warnings);
    }

    [Theory]
    [InlineData(RetrievalStrategy.Dense, Provider.None, null)]
    [InlineData(RetrievalStrategy.Dense, Provider.OpenAi, new[] { "dense", "rerank" })]
    [InlineData(RetrievalStrategy.Hybrid, Provider.None, new[] { "dense", "lexical" })]
    [InlineData(RetrievalStrategy.Hybrid, Provider.Jev, new[] { "dense", "lexical", "rerank" })]
    public async Task SourceAttribution_ShowsEveryStageThatContributed(RetrievalStrategy strategy, Provider provider, string[]? expectedSources)
    {
        var service = Service(await Store(), strategy, provider, HandlerFor(provider));

        var top = (await service.SearchAsync(Query(), CancellationToken.None)).Results.Single(r => r.ChunkId == "b:0");

        Assert.Equal(expectedSources, top.Sources?.Select(s => s.Source));
        Assert.Equal("beta target answer", top.Text);
        Assert.Equal("b", top.Metadata!["topic"]);
    }

    [Fact]
    public async Task RerankedScores_AreProviderCalibrated_NotComparableAcrossProviders()
    {
        var openAi = (await Service(await Store(), RetrievalStrategy.Dense, Provider.OpenAi, HandlerFor(Provider.OpenAi)).SearchAsync(Query(), CancellationToken.None)).Results;
        var jev = (await Service(await Store(), RetrievalStrategy.Dense, Provider.Jev, HandlerFor(Provider.Jev)).SearchAsync(Query(), CancellationToken.None)).Results;

        Assert.Equal(openAi.Select(r => r.ChunkId), jev.Select(r => r.ChunkId));   // same order…
        Assert.Equal(1.0, openAi[0].Score, 9);                                     // …different numbers: 3/3 vs 2.4/3
        Assert.Equal(0.8, jev[0].Score, 9);
    }

    [Fact]
    public async Task AllSixStrategies_SharingOneCache_NeverSeeEachOthersResults()
    {
        var cache = NewCache();
        var store = await Store();
        var seen = new List<string>();

        foreach (var (strategy, provider) in new[]
        {
            (RetrievalStrategy.Dense, Provider.None), (RetrievalStrategy.Dense, Provider.OpenAi), (RetrievalStrategy.Dense, Provider.Jev),
            (RetrievalStrategy.Hybrid, Provider.None), (RetrievalStrategy.Hybrid, Provider.OpenAi), (RetrievalStrategy.Hybrid, Provider.Jev)
        })
        {
            var handler = HandlerFor(provider);
            var response = await Service(store, strategy, provider, handler, cache).SearchAsync(Query(), CancellationToken.None);

            // a cached payload from another strategy would have skipped the provider call: every rerank run must have made its own request
            if (provider != Provider.None)
            {
                Assert.Single(handler.Calls);
            }

            seen.Add($"{strategy}/{provider}: {string.Join(",", response.Results.Select(r => $"{r.ChunkId}[{string.Join("+", r.Sources?.Select(s => s.Source) ?? ["-"])}]@{r.Score:F2}"))}");
        }

        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task ARepeatedQuery_IsServedFromTheCache_WithoutAnotherProviderCall()
    {
        var cache = NewCache();
        var store = await Store();
        var handler = HandlerFor(Provider.OpenAi);

        await Service(store, RetrievalStrategy.Dense, Provider.OpenAi, handler, cache).SearchAsync(Query(), CancellationToken.None);
        var second = await Service(store, RetrievalStrategy.Dense, Provider.OpenAi, handler, cache).SearchAsync(Query(), CancellationToken.None);

        Assert.Single(handler.Calls);
        Assert.Equal("b:0", second.Results[0].ChunkId);
    }

    [Fact]
    public async Task ProviderOutage_ServesRetrievalOrder_WithAWarning_AndNeverCachesIt()
    {
        var cache = NewCache();
        var store = await Store();
        var outage = new ScriptedHandler().Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable);

        var degraded = await Service(store, RetrievalStrategy.Dense, Provider.OpenAi, outage, cache).SearchAsync(Query(), CancellationToken.None);
        var recovered = await Service(store, RetrievalStrategy.Dense, Provider.OpenAi, HandlerFor(Provider.OpenAi), cache).SearchAsync(Query(), CancellationToken.None);

        Assert.True(degraded.Success);
        Assert.Equal(["a:0", "b:0", "c:0", "d:0"], degraded.Results.Select(r => r.ChunkId));   // retrieval order
        Assert.Equal([RerankingRetriever.Failed], degraded.Warnings);
        Assert.Equal("b:0", recovered.Results[0].ChunkId);                                      // the degraded answer was not pinned in the cache
        Assert.Null(recovered.Warnings);
    }

    [Fact]
    public async Task ProviderOutage_WithTheFailPolicy_FailsTheQuery_WithAGenericMessage()
    {
        var outage = new ScriptedHandler().Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable);

        var response = await Service(await Store(), RetrievalStrategy.Dense, Provider.Jev, outage, policy: RerankFailurePolicy.Fail)
            .SearchAsync(Query(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("Reranking with 'jev:jev-latest:rel-v1:score' failed.", response.ErrorMessage);
    }

    [Fact]
    public async Task AnInvalidProviderResponse_ChangesNothing_TheQueryKeepsRetrievalOrder()
    {
        var garbage = new ScriptedHandler().Reply(HttpStatusCode.OK, """{"answers":[{"type":"score","name":"zzz","score":3}],"usage":{"input_tokens":1}}""");

        var response = await Service(await Store(), RetrievalStrategy.Dense, Provider.OpenAi, garbage).SearchAsync(Query(), CancellationToken.None);

        Assert.Equal(["a:0", "b:0", "c:0", "d:0"], response.Results.Select(r => r.ChunkId));
        Assert.Equal([RerankingRetriever.Failed], response.Warnings);
    }

    [Fact]
    public async Task OneCandidateBudget_IsUsedIdenticallyByDenseAndHybrid()
    {
        var denseHandler = HandlerFor(Provider.OpenAi);
        var hybridHandler = HandlerFor(Provider.OpenAi);

        await Service(await Store(), RetrievalStrategy.Dense, Provider.OpenAi, denseHandler).SearchAsync(Query(topK: 2), CancellationToken.None);
        await Service(await Store(), RetrievalStrategy.Hybrid, Provider.OpenAi, hybridHandler).SearchAsync(Query(topK: 2), CancellationToken.None);

        // budget is 20 in both, but only 4 chunks exist: both pipelines send every available candidate, never fewer
        Assert.Equal(4, System.Text.Json.Nodes.JsonNode.Parse(denseHandler.Calls[0].Body)!["questions"]!.AsArray().Count);
        Assert.Equal(4, System.Text.Json.Nodes.JsonNode.Parse(hybridHandler.Calls[0].Body)!["questions"]!.AsArray().Count);
    }

    [Fact]
    public async Task ReturnedResultCount_RespectsTopK_EvenThoughManyMoreCandidatesWereScored()
    {
        var handler = HandlerFor(Provider.OpenAi);

        var response = await Service(await Store(), RetrievalStrategy.Dense, Provider.OpenAi, handler).SearchAsync(Query(topK: 2), CancellationToken.None);

        Assert.Equal(["b:0", "d:0"], response.Results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task WithRerankingDisabled_TheLegacyQueryContractIsUnchanged()
    {
        var store = await Store();

        var response = await Service(store, RetrievalStrategy.Dense, Provider.None, new ScriptedHandler()).SearchAsync(Query(), CancellationToken.None);

        Assert.True(response.Success);
        Assert.Null(response.Warnings);
        Assert.All(response.Results, r => Assert.Null(r.Sources));
        Assert.Equal(1.0, response.Results[0].Score);   // the store's own score, untouched
    }

    [Fact]
    public async Task ThePredicateMechanism_RunsThroughTheSamePipeline()
    {
        var handler = new ScriptedHandler().Add(async request =>
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var answers = new System.Text.Json.Nodes.JsonArray(body["questions"]!.AsArray().Select(q =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "predicate",
                    ["name"] = q!["name"]!.GetValue<string>(),
                    ["probability"] = q["instructions"]!.GetValue<string>().Contains("target") ? 0.95 : 0.05
                }).ToArray());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new System.Text.Json.Nodes.JsonObject { ["answers"] = answers, ["usage"] = new System.Text.Json.Nodes.JsonObject { ["input_tokens"] = 1 } }.ToJsonString())
            };
        });

        var response = await Service(await Store(), RetrievalStrategy.Dense, Provider.OpenAi, handler, mechanism: RelevanceMechanism.Predicate)
            .SearchAsync(Query(), CancellationToken.None);

        Assert.Equal(["b:0", "d:0", "a:0", "c:0"], response.Results.Select(r => r.ChunkId));
        Assert.Equal(0.95, response.Results[0].Score, 9);
    }

    [Fact]
    public async Task CacheEntries_AreNotSharedBetweenCandidateBudgets()
    {
        var cache = NewCache();
        var store = await Store();
        var first = HandlerFor(Provider.OpenAi);
        var second = HandlerFor(Provider.OpenAi);

        await Service(store, RetrievalStrategy.Dense, Provider.OpenAi, first, cache).SearchAsync(Query(), CancellationToken.None);
        var tight = Service(store, RetrievalStrategy.Dense, Provider.OpenAi, second, cache, budget: 2);
        var response = await tight.SearchAsync(Query(), CancellationToken.None);

        Assert.Single(second.Calls);   // a different budget is a different strategy: it must not reuse the budget-20 entry
        Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(second.Calls[0].Body)!["questions"]!.AsArray().Count);
        Assert.Equal(4, response.Results.Count);
    }
}
