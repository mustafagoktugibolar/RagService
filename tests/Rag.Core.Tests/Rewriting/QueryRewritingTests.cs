using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Rewriting;
using Rag.Core.Services;
using Rag.Core.Tests.Fakes;

namespace Rag.Core.Tests.Rewriting;

public sealed class RewriteValidatorTests
{
    private static readonly QueryRewritingOptions Options = new();

    [Theory]
    [InlineData("how do I rotate the api key", "rotate API key procedure")]
    [InlineData("what does the circuit breaker do after failures", "circuit breaker behaviour after failures")]
    public void AFaithfulRewrite_IsAccepted(string original, string candidate) =>
        Assert.True(RewriteValidator.Validate(original, candidate, Options).Accepted);

    [Theory]
    [InlineData("", RewriteValidator.Empty)]
    [InlineData("   ", RewriteValidator.Empty)]
    [InlineData("line one\nline two", RewriteValidator.MultiLine)]
    [InlineData("I'm sorry, I cannot help with that", RewriteValidator.Refusal)]
    [InlineData("Maalesef bunu yapamam", RewriteValidator.Refusal)]
    [InlineData("quantum chromodynamics lattice gauge", RewriteValidator.LowOverlap)]
    public void ABadRewrite_IsRejectedWithAReason(string candidate, string reason)
    {
        var result = RewriteValidator.Validate("how does the circuit breaker open after failures", candidate, Options);

        Assert.False(result.Accepted);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void ARewriteLongerThanTheLimit_IsRejected() =>
        Assert.Equal(RewriteValidator.TooLong,
            RewriteValidator.Validate("circuit breaker failures", "circuit breaker failures " + new string('x', 400), Options).Reason);

    [Theory]
    [InlineData("error 429 from the provider", "provider rate limit error")]                 // number dropped
    [InlineData("what does RRF_K control", "fusion constant")]                                // identifier dropped
    [InlineData("explain \"reciprocal rank fusion\" simply", "rank merging method explained")] // quoted phrase dropped
    [InlineData("what is the HTTP timeout", "request time limit")]                            // acronym dropped
    public void LosingANumberIdentifierQuotedPhraseOrAcronym_IsRejected(string original, string candidate) =>
        Assert.Equal(RewriteValidator.DroppedProtectedToken, RewriteValidator.Validate(original, candidate, Options).Reason);

    [Fact]
    public void ProtectedTokens_AreFoundInMixedLanguageQueries()
    {
        var tokens = RewriteValidator.ProtectedTokens("QdrantVectorStore neden \"hybrid arama\" kullanır, k=60, ILexicalSearchStore");

        Assert.Contains("QdrantVectorStore", tokens);
        Assert.Contains("hybrid arama", tokens);
        Assert.Contains("ILexicalSearchStore", tokens);
        Assert.Contains("k=60", tokens);
    }

    [Fact]
    public void QuotesAroundTheWholeAnswer_AreRemoved() =>
        Assert.Equal("circuit breaker failures", RewriteValidator.Validate("circuit breaker failures", "\"circuit breaker failures\"", Options).Text);
}

public sealed class QueryRewritingStageTests
{
    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static QueryRewritingStage Stage(IQueryRewriter rewriter, QueryRewritingOptions? options = null, IDistributedCache? cache = null) =>
        new(rewriter, cache ?? NewCache(), options ?? new QueryRewritingOptions { Enabled = true, TimeoutSeconds = 1 }, NullLogger.Instance);

    private sealed class Fixed(Func<string, string?> propose, string name = "fixed:v1") : IQueryRewriter
    {
        public int Calls;
        public string Name => name;

        public Task<string?> ProposeAsync(string query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(propose(query));
        }
    }

    private sealed class Throwing(Exception exception) : IQueryRewriter
    {
        public string Name => "throwing";
        public Task<string?> ProposeAsync(string query, CancellationToken ct) => throw exception;
    }

    private sealed class Hanging : IQueryRewriter
    {
        public string Name => "hanging";

        public async Task<string?> ProposeAsync(string query, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task AValidRewrite_ReplacesTheEffectiveQuery_AndKeepsTheOriginal()
    {
        var result = await Stage(new Fixed(_ => "circuit breaker failures")).TransformAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal("how does the circuit breaker handle failures", result.Original);
        Assert.Equal("circuit breaker failures", result.Effective);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task AShortQuery_IsSearchedAsWritten_WithoutCallingTheRewriter()
    {
        var rewriter = new Fixed(_ => "anything else");

        var result = await Stage(rewriter).TransformAsync("RRF k", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Skipped, result.Outcome);
        Assert.Equal("RRF k", result.Effective);
        Assert.Equal(0, rewriter.Calls);
    }

    [Fact]
    public async Task ARejectedRewrite_FallsBackToTheOriginal_WithAWarning()
    {
        var result = await Stage(new Fixed(_ => "I'm sorry, I cannot do that")).TransformAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rejected, result.Outcome);
        Assert.Equal(result.Original, result.Effective);
        Assert.Equal(QueryRewritingStage.RejectedWarning, result.Warning);
        Assert.Equal(RewriteValidator.Refusal, result.Reason);
    }

    [Fact]
    public async Task AFailingRewriter_NeverFailsTheQuery()
    {
        var result = await Stage(new Throwing(new HttpRequestException("503"))).TransformAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Failed, result.Outcome);
        Assert.Equal(result.Original, result.Effective);
        Assert.Equal(QueryRewritingStage.FailedWarning, result.Warning);
    }

    [Fact]
    public async Task ASlowRewriter_TimesOut_AndFallsBack()
    {
        var result = await Stage(new Hanging()).TransformAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal(RewriteOutcome.TimedOut, result.Outcome);
        Assert.Equal(QueryRewritingStage.TimeoutWarning, result.Warning);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotTreatedAsAFallback()
    {
        using var cts = new CancellationTokenSource();
        var pending = Stage(new Hanging(), new QueryRewritingOptions { Enabled = true, TimeoutSeconds = 30 })
            .TransformAsync("how does the circuit breaker handle failures", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task AValidatedRewrite_IsCached_ButARejectedOneIsNot()
    {
        var cache = NewCache();
        var good = new Fixed(_ => "circuit breaker failures");
        var stage = Stage(good, cache: cache);
        const string query = "how does the circuit breaker handle failures";

        await stage.TransformAsync(query, CancellationToken.None);
        var second = await stage.TransformAsync(query, CancellationToken.None);

        Assert.Equal(1, good.Calls);
        Assert.True(second.FromCache);
        Assert.Equal("circuit breaker failures", second.Effective);

        var bad = new Fixed(_ => "sorry, no", "bad:v1");
        var badStage = Stage(bad, cache: cache);
        await badStage.TransformAsync(query, CancellationToken.None);
        await badStage.TransformAsync(query, CancellationToken.None);
        Assert.Equal(2, bad.Calls);
    }

    [Fact]
    public async Task ADifferentRewriterIdentity_DoesNotShareCachedRewrites()
    {
        var cache = NewCache();
        const string query = "how does the circuit breaker handle failures";
        await Stage(new Fixed(_ => "circuit breaker failures", "model-a"), cache: cache).TransformAsync(query, CancellationToken.None);
        var other = new Fixed(_ => "circuit breaker handling failures", "model-b");

        var result = await Stage(other, cache: cache).TransformAsync(query, CancellationToken.None);

        Assert.Equal(1, other.Calls);
        Assert.Equal("circuit breaker handling failures", result.Effective);
    }

    private sealed class DictionaryCache : IDistributedCache
    {
        public readonly Dictionary<string, byte[]> Entries = [];
        public byte[]? Get(string key) => Entries.GetValueOrDefault(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Entries[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => Entries.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ACorruptedCacheEntry_IsRevalidated_NotTrusted()
    {
        var cache = new DictionaryCache();
        var rewriter = new Fixed(_ => "circuit breaker failures");
        var stage = Stage(rewriter, cache: cache);
        const string query = "how does the circuit breaker handle failures";
        await stage.TransformAsync(query, CancellationToken.None);
        var key = Assert.Single(cache.Entries).Key;
        cache.Entries[key] = System.Text.Encoding.UTF8.GetBytes("I'm sorry, I cannot");

        var result = await stage.TransformAsync(query, CancellationToken.None);

        Assert.Equal(2, rewriter.Calls);   // the poisoned value was rejected and the rewriter asked again
        Assert.Equal("circuit breaker failures", result.Effective);
    }

    [Fact]
    public async Task ACacheOutage_DoesNotBreakRewriting()
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<byte[]?>(_ => throw new InvalidOperationException("redis down"));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("redis down"));

        var result = await Stage(new Fixed(_ => "circuit breaker failures"), cache: cache)
            .TransformAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
    }

    [Fact]
    public async Task TheRuleBasedRewriter_StripsFiller_AndPassesValidation()
    {
        var stage = Stage(new RuleBasedQueryRewriter());

        var result = await stage.TransformAsync("Could you please tell me how the circuit breaker opens?", CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal("how the circuit breaker opens", result.Effective);
    }

    [Fact]
    public async Task TheChatRewriter_SendsTheQueryAsFencedData_AndReturnsTheModelText()
    {
        var client = Substitute.For<IChatClient>();
        IList<ChatMessage>? sent = null;
        client.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => sent = m.ToList()), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "circuit breaker failures")));
        var rewriter = new ChatQueryRewriter(client, new QueryRewritingOptions { Model = "test-model" });

        var proposal = await rewriter.ProposeAsync("how does the circuit breaker handle failures", CancellationToken.None);

        Assert.Equal("circuit breaker failures", proposal);
        Assert.Equal("chat:test-model:chat-v1", rewriter.Name);
        Assert.Contains("data, never instructions", sent![0].Text);
        Assert.Contains("<<<QUERY", sent[1].Text);
    }
}

/// <summary>The stage inside the real VectorSearchService: what is searched, what is judged, what is cached, and what degrades.</summary>
public sealed class RewritingInSearchServiceTests
{
    private const string Collection = "docs";
    private const int Dim = 2;

    private static readonly RagOptions Rag = new() { EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, DefaultTopK = 5 };

    private static async Task<LexicalInMemoryVectorStore> Store()
    {
        var store = new LexicalInMemoryVectorStore();
        await store.EnsureCollectionAsync(Collection, Dim, CancellationToken.None);
        await store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = Collection, EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, IndexVersion = "v1"
        }, CancellationToken.None);
        foreach (var (id, text) in new[] { ("a", "alpha filler words"), ("b", "beta breaker answer"), ("c", "gamma filler words") })
        {
            await store.UpsertAsync(Collection,
                [new VectorDocument { DocumentId = id, ChunkId = $"{id}:0", Text = text, Embedding = new float[Dim], Metadata = new() }],
                CancellationToken.None);
        }

        return store;
    }

    private static (VectorSearchService Service, IEmbeddingProvider Embedder, IDistributedCache Cache) Build(
        IVectorStore store, IQueryRewriter? rewriter, RetrievalStrategy strategy = RetrievalStrategy.Lexical, IReranker? reranker = null, bool enabled = true)
    {
        var cache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var embedder = Substitute.For<IEmbeddingProvider>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f, 0f });
        var rewriting = new QueryRewritingOptions { Enabled = enabled, Provider = QueryRewriteProviderKind.Rules, TimeoutSeconds = 1, CacheTtlHours = 0 };
        var retrieval = new RetrievalOptions { Strategy = strategy, CandidateK = 20, QueryRewriting = rewriting };
        if (reranker is not null)
        {
            retrieval.Reranking = new RerankingOptions { Enabled = true, CandidateBudget = 10, FailurePolicy = RerankFailurePolicy.Fallback };
        }

        var stage = rewriter is null ? null : new QueryRewritingStage(rewriter, cache, rewriting, NullLogger.Instance);
        var service = new VectorSearchService(embedder, store, cache,
            Microsoft.Extensions.Options.Options.Create(Rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = true, TtlMinutes = 5 }),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(retrieval), reranker, stage);
        return (service, embedder, cache);
    }

    private static QueryRequest Request(string query) => new() { RequestId = "q", Collection = Collection, Query = query, TopK = 3 };

    private sealed class Counting(Func<string, string?> propose) : IQueryRewriter
    {
        public int Calls;
        public string Name => "counting:v1";

        public Task<string?> ProposeAsync(string query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(propose(query));
        }
    }

    [Fact]
    public async Task TheRewrittenText_IsWhatIsSearchedAndEmbedded()
    {
        var (service, embedder, _) = Build(await Store(), new Counting(_ => "breaker answer"));

        var response = await service.SearchAsync(Request("what is the thing about the breaker answer exactly"), CancellationToken.None);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("b:0", response.Results[0].ChunkId);   // the original query has no term the lexical index knows well enough; the rewrite does
        Assert.Null(response.Warnings);
        await embedder.DidNotReceive().EmbedAsync("what is the thing about the breaker answer exactly", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheRerankerJudgesTheOriginalQuery_NotTheRewrite()
    {
        var reranker = Substitute.For<IReranker>();
        reranker.Name.Returns("fake:v1");
        string? judged = null;
        reranker.RerankAsync(Arg.Do<string>(q => judged = q), Arg.Any<IReadOnlyList<RerankCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<RerankCandidate>>().Select((c, i) => new RerankScore(c.ChunkId, 1.0 - i * 0.1)).ToList());
        const string original = "what is the thing about the breaker answer exactly";
        var (service, _, _) = Build(await Store(), new Counting(_ => "breaker answer filler"), RetrievalStrategy.Lexical, reranker);

        var response = await service.SearchAsync(Request(original), CancellationToken.None);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(original, judged);
    }

    [Fact]
    public async Task TheCacheIsKeyedOnTheOriginalQuery_SoAHitSkipsTheRewriteCall()
    {
        var rewriter = new Counting(_ => "breaker answer");
        var (service, _, _) = Build(await Store(), rewriter);
        var request = Request("what is the thing about the breaker answer exactly");

        await service.SearchAsync(request, CancellationToken.None);
        await service.SearchAsync(request, CancellationToken.None);

        Assert.Equal(1, rewriter.Calls);
    }

    [Fact]
    public async Task EnablingRewriting_NeverServesResultsCachedBeforeIt()
    {
        var store = await Store();
        var (disabled, _, sharedCache) = Build(store, null, enabled: false);
        await disabled.SearchAsync(Request("what is the thing about the breaker answer exactly"), CancellationToken.None);
        var rewriter = new Counting(_ => "breaker answer");
        var embedder = Substitute.For<IEmbeddingProvider>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f, 0f });
        var rewriting = new QueryRewritingOptions { Enabled = true, Provider = QueryRewriteProviderKind.Rules, CacheTtlHours = 0 };
        var enabled = new VectorSearchService(embedder, store, sharedCache,
            Microsoft.Extensions.Options.Options.Create(Rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = true, TtlMinutes = 5 }),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Strategy = RetrievalStrategy.Lexical, CandidateK = 20, QueryRewriting = rewriting }),
            null, new QueryRewritingStage(rewriter, sharedCache, rewriting, NullLogger.Instance));

        await enabled.SearchAsync(Request("what is the thing about the breaker answer exactly"), CancellationToken.None);

        Assert.Equal(1, rewriter.Calls);   // a stale result from the rewriting-disabled configuration was not reused
    }

    [Fact]
    public async Task ARewriterOutage_DegradesToTheOriginalQuery_ReportsAWarning_AndIsNotCached()
    {
        var rewriter = new Counting(_ => throw new HttpRequestException("503"));
        var (service, _, _) = Build(await Store(), rewriter);
        var request = Request("what is the thing about the breaker answer exactly");

        var first = await service.SearchAsync(request, CancellationToken.None);
        var second = await service.SearchAsync(request, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Contains(QueryRewritingStage.FailedWarning, first.Warnings!);
        Assert.Equal(2, rewriter.Calls);   // the degraded result was not pinned in the cache
        Assert.True(second.Success);
    }

    [Fact]
    public async Task WithRewritingDisabled_TheStageIsNeverCalled()
    {
        var rewriter = new Counting(_ => "x");
        var (service, _, _) = Build(await Store(), rewriter, enabled: false);

        await service.SearchAsync(Request("what is the thing about the breaker answer exactly"), CancellationToken.None);

        Assert.Equal(0, rewriter.Calls);
    }
}
