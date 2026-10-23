using Microsoft.Extensions.Logging.Abstractions;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Retrieval;
using static Rag.Core.Tests.Reranking.RerankTestData;

namespace Rag.Core.Tests.Reranking;

public sealed class RerankingRetrieverTests
{
    private sealed class FakeInner(IReadOnlyList<FusedResult> results, IReadOnlyList<string>? warnings = null) : IRetriever
    {
        public List<RetrievalRequest> Requests { get; } = [];

        public Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new RetrievalOutcome(results.Take(request.TopK).ToArray(), warnings ?? []));
        }
    }

    private sealed class FakeReranker(Func<IReadOnlyList<RerankCandidate>, CancellationToken, Task<IReadOnlyList<RerankScore>>> score) : IReranker
    {
        public string Name => "fake:model";

        public List<IReadOnlyList<RerankCandidate>> Calls { get; } = [];

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
        {
            Calls.Add(candidates);
            return score(candidates, ct);
        }

        public static FakeReranker Scoring(params (string Id, double Score)[] scores) =>
            new((_, _) => Task.FromResult<IReadOnlyList<RerankScore>>(scores.Select(s => new RerankScore(s.Id, s.Score)).ToArray()));
    }

    private static readonly RetrievalRequest Request = new("c", "the query", [1f], 3, null);

    private static RerankingRetriever Build(FakeInner inner, IReranker reranker, RerankingOptions? options = null) =>
        new(inner, reranker, options ?? new RerankingOptions { Enabled = true, CandidateBudget = 5, TimeoutSeconds = 5 }, NullLogger.Instance);

    private static FakeInner FiveChunks() =>
        new([Fused("a:0", 0.9), Fused("b:0", 0.8), Fused("c:0", 0.7), Fused("d:0", 0.6), Fused("e:0", 0.5)]);

    [Fact]
    public async Task Candidates_AreReorderedByRerankScore_Descending()
    {
        var outcome = await Build(FiveChunks(), FakeReranker.Scoring(("a:0", 0.1), ("b:0", 0.9), ("c:0", 0.5), ("d:0", 0.3), ("e:0", 0.7)))
            .RetrieveAsync(Request with { TopK = 5 }, CancellationToken.None);

        Assert.Equal(["b:0", "e:0", "c:0", "d:0", "a:0"], outcome.Results.Select(r => r.Result.ChunkId));
    }

    [Fact]
    public async Task EqualScores_KeepRetrievalOrder_SoTheOutcomeIsDeterministic()
    {
        var outcome = await Build(FiveChunks(), FakeReranker.Scoring(("a:0", 0.5), ("b:0", 0.9), ("c:0", 0.5), ("d:0", 0.9), ("e:0", 0.5)))
            .RetrieveAsync(Request with { TopK = 5 }, CancellationToken.None);

        Assert.Equal(["b:0", "d:0", "a:0", "c:0", "e:0"], outcome.Results.Select(r => r.Result.ChunkId));
    }

    [Fact]
    public async Task IdentityTextMetadataAndOriginalScores_SurviveReranking_AndARerankSourceIsAdded()
    {
        var metadata = new Dictionary<string, string> { ["lang"] = "tr", ["doc"] = "x" };
        var inner = new FakeInner([Fused("a:0", 0.9, source: "dense", metadata: metadata, text: "alpha text"), Fused("b:0", 0.8, source: "lexical")]);

        var outcome = await Build(inner, FakeReranker.Scoring(("a:0", 0.2), ("b:0", 0.8))).RetrieveAsync(Request, CancellationToken.None);

        var a = outcome.Results.Single(r => r.Result.ChunkId == "a:0");
        Assert.Equal("alpha text", a.Result.Text);
        Assert.Same(metadata, a.Result.Metadata);
        Assert.Equal(0.9, a.Result.Score);                                  // retrieval score kept on the chunk itself
        Assert.Equal(0.2, a.Score);                                         // the fused/served score is now the rerank score
        Assert.Equal(new SourceRank("dense", 1, 0.9), a.Sources[0]);        // original attribution preserved
        Assert.Equal(new SourceRank("rerank", 2, 0.2), a.Sources[^1]);      // plus where the reranker put it
    }

    [Fact]
    public async Task TheInnerRetrieverIsAskedForTheWholeBudget_NotJustTopK()
    {
        var inner = FiveChunks();

        await Build(inner, FakeReranker.Scoring()).RetrieveAsync(Request with { TopK = 3 }, CancellationToken.None);

        Assert.Equal(5, inner.Requests.Single().TopK);
    }

    [Fact]
    public async Task OnlyTheBudgetIsScored_AndTheTailFollowsInRetrievalOrder()
    {
        var inner = FiveChunks();
        var reranker = FakeReranker.Scoring(("a:0", 0.1), ("b:0", 0.9), ("c:0", 0.5));

        var outcome = await Build(inner, reranker, new RerankingOptions { Enabled = true, CandidateBudget = 3, TimeoutSeconds = 5 })
            .RetrieveAsync(Request with { TopK = 5 }, CancellationToken.None);

        Assert.Equal(["a:0", "b:0", "c:0"], reranker.Calls.Single().Select(c => c.ChunkId));
        Assert.Equal(["b:0", "c:0", "a:0", "d:0", "e:0"], outcome.Results.Select(r => r.Result.ChunkId));
    }

    [Fact]
    public async Task FewerThanTwoCandidates_AreReturnedWithoutCallingTheReranker()
    {
        var reranker = FakeReranker.Scoring();

        var outcome = await Build(new FakeInner([Fused("a:0")]), reranker).RetrieveAsync(Request, CancellationToken.None);

        Assert.Single(outcome.Results);
        Assert.Empty(reranker.Calls);
    }

    [Fact]
    public async Task ProviderFailure_FallsBackToRetrievalOrder_AndReportsIt()
    {
        var reranker = new FakeReranker((_, _) => throw new HttpRequestException("503"));

        var outcome = await Build(FiveChunks(), reranker).RetrieveAsync(Request, CancellationToken.None);

        Assert.Equal(["a:0", "b:0", "c:0"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Contains(RerankingRetriever.Failed, outcome.Warnings);
        Assert.All(outcome.Results, r => Assert.DoesNotContain(r.Sources, s => s.Source == "rerank"));
    }

    [Fact]
    public async Task ProviderFailure_WithTheFailPolicy_FailsTheQuery_WithoutLeakingContent()
    {
        var reranker = new FakeReranker((_, _) => throw new InvalidOperationException("CONFIDENTIAL detail"));
        var options = new RerankingOptions { Enabled = true, CandidateBudget = 5, TimeoutSeconds = 5, FailurePolicy = RerankFailurePolicy.Fail };

        var ex = await Assert.ThrowsAsync<RerankFailedException>(() => Build(FiveChunks(), reranker, options).RetrieveAsync(Request, CancellationToken.None));

        Assert.Equal("Reranking with 'fake:model' failed.", ex.Message);
    }

    [Fact]
    public async Task ATimeout_FallsBackToRetrievalOrder_AndSaysTimeout()
    {
        var reranker = new FakeReranker(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; });
        var options = new RerankingOptions { Enabled = true, CandidateBudget = 5, TimeoutSeconds = 1 };

        var outcome = await Build(FiveChunks(), reranker, options).RetrieveAsync(Request, CancellationToken.None);

        Assert.Contains(RerankingRetriever.TimedOut, outcome.Warnings);
        Assert.Equal(["a:0", "b:0", "c:0"], outcome.Results.Select(r => r.Result.ChunkId));
    }

    [Fact]
    public async Task ATimeout_WithTheFailPolicy_FailsTheQuery()
    {
        var reranker = new FakeReranker(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; });
        var options = new RerankingOptions { Enabled = true, CandidateBudget = 5, TimeoutSeconds = 1, FailurePolicy = RerankFailurePolicy.Fail };

        await Assert.ThrowsAsync<RerankFailedException>(() => Build(FiveChunks(), reranker, options).RetrieveAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellation_Propagates_NotTreatedAsAFallback()
    {
        using var cts = new CancellationTokenSource();
        var reranker = new FakeReranker(async (_, ct) => { await cts.CancelAsync(); await Task.Delay(Timeout.Infinite, ct); return []; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(FiveChunks(), reranker).RetrieveAsync(Request, cts.Token));
    }

    [Fact]
    public async Task MissingScores_PlaceThoseCandidatesAfterTheScoredOnes_InRetrievalOrder_AndWarn()
    {
        var outcome = await Build(FiveChunks(), FakeReranker.Scoring(("c:0", 0.9), ("e:0", 0.2)))
            .RetrieveAsync(Request with { TopK = 5 }, CancellationToken.None);

        Assert.Equal(["c:0", "e:0", "a:0", "b:0", "d:0"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Contains(RerankingRetriever.Incomplete, outcome.Warnings);
    }

    [Fact]
    public async Task UnknownAndDuplicateScores_AreIgnored_FirstScorePerChunkWins()
    {
        var outcome = await Build(FiveChunks(), FakeReranker.Scoring(("zzz:0", 9.9), ("b:0", 0.9), ("b:0", 0.0), ("a:0", 0.5), ("c:0", 0.1), ("d:0", 0.1), ("e:0", 0.1)))
            .RetrieveAsync(Request, CancellationToken.None);

        Assert.Equal(["b:0", "a:0", "c:0"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.DoesNotContain(outcome.Results, r => r.Result.ChunkId == "zzz:0");
    }

    [Fact]
    public async Task InnerWarnings_AreKept_AlongsideRerankWarnings()
    {
        var inner = new FakeInner([Fused("a:0"), Fused("b:0")], warnings: [HybridRetriever.LexicalFailed]);

        var outcome = await Build(inner, FakeReranker.Scoring(("a:0", 1.0))).RetrieveAsync(Request, CancellationToken.None);

        Assert.Equal([HybridRetriever.LexicalFailed, RerankingRetriever.Incomplete], outcome.Warnings);
    }

    [Fact]
    public async Task NoOpReranker_KeepsRetrievalOrderExactly()
    {
        var outcome = await Build(FiveChunks(), new NoOpReranker()).RetrieveAsync(Request with { TopK = 5 }, CancellationToken.None);

        Assert.Equal(["a:0", "b:0", "c:0", "d:0", "e:0"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Empty(outcome.Warnings);
    }
}
