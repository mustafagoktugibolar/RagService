using Rag.Core.Reranking;
using Rag.Evaluation.Reranking;

namespace Rag.Evaluation.Tests;

public sealed class RerankSupportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rerank-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CountingReranker(string name = "fake:m:rel-v2:score") : IReranker, IRerankUsageSource
    {
        public string Name => name;

        public List<string[]> Calls { get; } = [];

        public RerankUsage Usage => new(Calls.Count, 0, Calls.Sum(c => c.Length), Calls.Sum(c => c.Length) * 100L, 0, 0, Calls.Select(_ => 10.0).ToArray(), "resolved-1");

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
        {
            Calls.Add(candidates.Select(c => c.ChunkId).ToArray());
            return Task.FromResult<IReadOnlyList<RerankScore>>(candidates.Select(c => new RerankScore(c.ChunkId, c.Text.Length / 100.0)).ToArray());
        }
    }

    private static readonly RerankCandidate[] Pairs =
    [
        new("a:0", new string('a', 100)), new("b:0", new string('b', 200)), new("c:0", new string('c', 300))
    ];

    [Fact]
    public async Task FirstCall_GoesToTheProvider_SecondIsServedEntirelyFromDisk()
    {
        var inner = new CountingReranker();
        var cached = new FileCachingReranker(inner, _directory);

        var first = await cached.RerankAsync("q", Pairs, CancellationToken.None);
        var second = await cached.RerankAsync("q", Pairs, CancellationToken.None);

        Assert.Single(inner.Calls);
        Assert.Equal(first.Select(s => (s.ChunkId, s.Score)), second.Select(s => (s.ChunkId, s.Score)));
        Assert.Equal(3, cached.CacheHits);
        Assert.Equal(3, cached.CacheMisses);
    }

    [Fact]
    public async Task OnlyUncachedPairsAreSent_SoOverlappingCandidateSetsShareScores()
    {
        var inner = new CountingReranker();
        var cached = new FileCachingReranker(inner, _directory);
        await cached.RerankAsync("q", Pairs[..2], CancellationToken.None);            // dense candidates a, b

        var hybrid = await cached.RerankAsync("q", [Pairs[1], Pairs[2]], CancellationToken.None);   // hybrid candidates b, c

        Assert.Equal(["c:0"], inner.Calls[1]);                                         // only the new pair was sent
        Assert.Equal(["b:0", "c:0"], hybrid.Select(s => s.ChunkId));
    }

    [Fact]
    public async Task TheKey_CoversProviderIdentityQueryAndPassageText()
    {
        var a = new FileCachingReranker(new CountingReranker("p1:m:rel-v2:score"), _directory);
        var b = new FileCachingReranker(new CountingReranker("p2:m:rel-v2:score"), _directory);
        await a.RerankAsync("q", Pairs, CancellationToken.None);

        await b.RerankAsync("q", Pairs, CancellationToken.None);                       // other provider: miss
        await a.RerankAsync("other query", Pairs, CancellationToken.None);             // other query: miss
        await a.RerankAsync("q", [new RerankCandidate("a:0", "edited text")], CancellationToken.None);   // same id, other text: miss

        Assert.Equal(3, b.CacheMisses);
        Assert.Equal(3 + 3 + 1, a.CacheMisses);
        Assert.Equal(0, a.CacheHits);
    }

    [Fact]
    public async Task ACachedRun_StillExposesTheLiveUsageOfTheWrappedProvider()
    {
        var inner = new CountingReranker();
        var cached = new FileCachingReranker(inner, _directory);

        await cached.RerankAsync("q", Pairs, CancellationToken.None);
        await cached.RerankAsync("q", Pairs, CancellationToken.None);

        Assert.Equal(1, cached.Usage.Requests);          // replays are free: only the live request counts
        Assert.Equal(inner.Name, cached.Name);
    }

    [Fact]
    public void TokenEstimate_IsAboutAQuarterOfTheCharacters_PlusTheRubricPerQuestion()
    {
        var tokens = CostModel.EstimateInputTokens("a query", [new string('x', 400)]);

        Assert.InRange(tokens, 100 + CostModel.RubricTokensPerQuestion, 100 + CostModel.RubricTokensPerQuestion + 40);
    }

    [Fact]
    public void Cost_IsInputTokensTimesThePerMillionPrice_AndOutputIsFree()
    {
        Assert.Equal(0.10, CostModel.Dollars(1_000_000, CostModel.OpenAiDecisionsPerMillionInput), 9);
        Assert.Equal(0.042, CostModel.Dollars(1_000_000, CostModel.JevPerMillionInput), 9);
    }

    [Fact]
    public void SpendGuard_AllowsWithinBudget_AndStopsTheRunBeyondIt()
    {
        var guard = new SpendGuard(maxRequests: 2, maxDollars: 0.01, pricePerMillionInputTokens: 0.10);

        guard.Authorise(estimatedInputTokens: 10_000);
        guard.Authorise(estimatedInputTokens: 10_000);

        Assert.Throws<SpendLimitExceededException>(() => guard.Authorise(estimatedInputTokens: 10_000));   // third request
    }

    [Fact]
    public void SpendGuard_StopsOnDollars_EvenWithRequestsToSpare()
    {
        var guard = new SpendGuard(maxRequests: 100, maxDollars: 0.001, pricePerMillionInputTokens: 0.10);

        guard.Authorise(estimatedInputTokens: 5_000);                                                       // $0.0005

        var ex = Assert.Throws<SpendLimitExceededException>(() => guard.Authorise(estimatedInputTokens: 8_000));
        Assert.Contains("0.001", ex.Message);
    }

    [Fact]
    public async Task TheGuardedReranker_NeverCallsTheProviderOnceTheBudgetIsGone()
    {
        var inner = new CountingReranker();
        var guarded = new SpendGuardedReranker(inner, new SpendGuard(maxRequests: 1, maxDollars: 1, pricePerMillionInputTokens: 0.10));

        await guarded.RerankAsync("q", Pairs, CancellationToken.None);
        await Assert.ThrowsAsync<SpendLimitExceededException>(() => guarded.RerankAsync("q", Pairs, CancellationToken.None));

        Assert.Single(inner.Calls);
    }

    private sealed class UnderEstimatingProvider(long billedTokensPerCall) : IReranker, IRerankUsageSource
    {
        private int _calls;

        public string Name => "fake:under-estimated";

        public int Calls => _calls;

        public RerankUsage Usage => new(_calls, 0, 0, _calls * billedTokensPerCall, 0, 0, [], null);

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
        {
            _calls++;
            return Task.FromResult<IReadOnlyList<RerankScore>>(candidates.Select(c => new RerankScore(c.ChunkId, 1)).ToArray());
        }
    }

    [Fact]
    public async Task TheGuard_StopsOnWhatWasBilled_NotOnWhatWasGuessed()
    {
        // The estimate for these tiny pairs is a few hundred tokens ($0.00003); the provider bills 20 000 ($0.002) per call.
        var provider = new UnderEstimatingProvider(billedTokensPerCall: 20_000);
        var guarded = new SpendGuardedReranker(provider, new SpendGuard(maxRequests: 100, maxDollars: 0.003, pricePerMillionInputTokens: 0.10));

        await guarded.RerankAsync("q", Pairs, CancellationToken.None);    // billed $0.002
        await guarded.RerankAsync("q", Pairs, CancellationToken.None);    // billed $0.004 in total -> over the limit now

        await Assert.ThrowsAsync<SpendLimitExceededException>(() => guarded.RerankAsync("q", Pairs, CancellationToken.None));
        Assert.Equal(2, provider.Calls);                                   // the third paid call never happened
    }

    [Fact]
    public void Reconcile_NeverDrivesTheSpentAmountBelowZero()
    {
        var guard = new SpendGuard(10, 1, 0.10);

        guard.Authorise(1000);
        guard.Reconcile(estimatedInputTokens: 1000, observedInputTokens: 0);
        guard.Reconcile(estimatedInputTokens: 1000, observedInputTokens: 0);

        Assert.Equal(0, guard.EstimatedDollars);
    }
}
