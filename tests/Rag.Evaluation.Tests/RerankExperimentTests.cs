using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Harness;
using Rag.Evaluation.Reranking;

namespace Rag.Evaluation.Tests;

public sealed class RerankExperimentTests
{
    private static string DatasetPath => Path.Combine(AppContext.BaseDirectory, "datasets", "golden-v1", "dataset.json");

    /// <summary>Knows the judgments: a candidate scores its document's grade (0 if not relevant to that query).</summary>
    private sealed class OracleReranker(EvalDataset dataset, bool invert = false, IReadOnlyList<double>? latencies = null)
        : IReranker, IRerankUsageSource
    {
        private int _requests;

        public string Name => invert ? "fake:anti-oracle" : "fake:oracle";

        public RerankUsage Usage => new(_requests, 0, 0, _requests * 1000L, 0, 0, latencies ?? [], "fake-1");

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
        {
            _requests++;
            var judged = dataset.Queries.First(q => q.Query == query).RelevantDocuments.ToDictionary(r => r.DocumentId, r => (double)r.Grade);
            return Task.FromResult<IReadOnlyList<RerankScore>>(candidates.Select(c =>
            {
                var grade = judged.GetValueOrDefault(c.ChunkId.Split(':')[0]);
                return new RerankScore(c.ChunkId, invert ? 3 - grade : grade);
            }).ToArray());
        }
    }

    private sealed class BrokenReranker : IReranker
    {
        public string Name => "fake:broken";

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct) =>
            throw new HttpRequestException("provider is down");
    }

    private static async Task<(EvalDataset Dataset, DenseBaselineHarness Harness)> Setup()
    {
        var dataset = await DatasetLoader.LoadAsync(DatasetPath, CancellationToken.None);
        return (dataset, await DenseBaselineHarness.BuildAsync(dataset, new HarnessConfiguration(), CancellationToken.None));
    }

    private static PipelineMetrics Row(RerankReport report, string pipeline, string split) =>
        report.Metrics.Single(m => m.Pipeline == pipeline && m.Split == split);

    [Fact]
    public async Task ReportsAllFourPipelines_OnEverySplit()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        Assert.Equal(12, report.Metrics.Count);   // 4 pipelines × (tune, heldout, all)
        Assert.Equal(40, Row(report, RerankExperiment.Dense, "all").ScoredQueries);
        Assert.Equal(40, report.Metrics.Where(m => m.Split == "tune").Sum(m => m.ScoredQueries) / 4 + report.Metrics.Where(m => m.Split == "heldout").Sum(m => m.ScoredQueries) / 4);
        Assert.Equal("fake:oracle", report.Provider);
        Assert.Equal("fake-1", report.ResolvedModel);
    }

    [Fact]
    public async Task ARerankerCannotChangeTheCandidateSet_SoCandidateRecallIsIdenticalWithAndWithoutIt()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        foreach (var split in new[] { "tune", "heldout", "all" })
        {
            Assert.Equal(Row(report, RerankExperiment.Dense, split).CandidateRecall20, Row(report, RerankExperiment.DenseRerank, split).CandidateRecall20, 12);
            Assert.Equal(Row(report, RerankExperiment.Hybrid, split).CandidateRecall20, Row(report, RerankExperiment.HybridRerank, split).CandidateRecall20, 12);
        }
    }

    [Fact]
    public async Task APerfectReranker_LiftsRankingQualityToTheCeilingTheCandidatesAllow()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, latencies: [120, 150, 200]), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        var dense = Row(report, RerankExperiment.Dense, "all");
        var reranked = Row(report, RerankExperiment.DenseRerank, "all");
        Assert.True(reranked.Ndcg10 > dense.Ndcg10);
        Assert.True(reranked.Ndcg1 > dense.Ndcg1);
        Assert.Equal(dense.CandidateRecall20, reranked.CandidateRecall20, 12);   // quality gained by ordering, not by new candidates
        Assert.True(reranked.Ndcg10 <= 1.0 + 1e-9);
    }

    [Fact]
    public async Task APerfectReranker_PassesTheAdoptionRule_WhenItsLatencyIsWithinTheLimit()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, latencies: [120, 150, 200]), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        var comparison = report.Comparisons.Single(c => c.Name == "rerank on dense candidates");
        Assert.True(comparison.Verdict.IntervalLowerBoundAboveZero);
        Assert.True(comparison.Verdict.TopRankNotWorse);
        Assert.True(comparison.Verdict.NoLargeCategoryLoss);
        Assert.True(comparison.Verdict.RerankLatencyWithinLimit);
        Assert.True(comparison.Verdict.Candidate);
    }

    [Fact]
    public async Task EvenAPerfectReranker_IsNotACandidate_WhenItIsTooSlow()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, latencies: [900, 1200, 4000]), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        var comparison = report.Comparisons.Single(c => c.Name == "rerank on dense candidates");
        Assert.False(comparison.Verdict.RerankLatencyWithinLimit);
        Assert.False(comparison.Verdict.Candidate);
        Assert.True(comparison.Verdict.IntervalLowerBoundAboveZero);   // the quality part still passed: only latency blocks it
    }

    [Fact]
    public async Task WithoutAnyLiveRequest_LatencyIsNotMeasured_SoNoRerankPipelineCanBeACandidate()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, latencies: []), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        Assert.Null(report.RerankRequestP95Ms);
        Assert.False(report.Comparisons.Single(c => c.Name == "rerank on dense candidates").Verdict.Candidate);
    }

    [Fact]
    public async Task ABadReranker_IsRejected_AndTheReportShowsItHurt()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, invert: true, latencies: [100]), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        var comparison = report.Comparisons.Single(c => c.Name == "rerank on dense candidates");
        Assert.True(comparison.Ndcg10Difference.MeanDifference < 0);
        Assert.False(comparison.Verdict.Candidate);
    }

    [Fact]
    public async Task ReferenceComparisonWithoutReranking_NeverClaimsLatencyWasChecked()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset, latencies: [100]), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        Assert.Null(report.Comparisons.Single(c => c.Name == "hybrid vs dense, no reranking (reference)").Verdict.RerankLatencyWithinLimit);
        Assert.Null(report.Comparisons.Single(c => c.Name.StartsWith("hybrid candidates vs dense candidates")).Verdict.RerankLatencyWithinLimit);
    }

    [Fact]
    public async Task AProviderFailure_AbortsTheExperiment_InsteadOfSilentlyReportingUnrerankedNumbers()
    {
        var (dataset, harness) = await Setup();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            RerankExperiment.RunAsync(dataset, harness, new BrokenReranker(), 0.1, new RerankExperimentSettings(), CancellationToken.None));
    }

    [Fact]
    public async Task QueryEvidence_ShowsWhetherRelevantDocumentsWereCandidatesAtAll()
    {
        var (dataset, harness) = await Setup();

        var report = await RerankExperiment.RunAsync(dataset, harness, new OracleReranker(dataset), 0.1, new RerankExperimentSettings(), CancellationToken.None);

        Assert.Equal(40, report.Evidence.Count);
        Assert.All(report.Evidence, e =>
        {
            Assert.InRange(e.RelevantInDenseCandidates, 0, e.RelevantDocuments);
            Assert.InRange(e.RelevantInHybridCandidates, 0, e.RelevantDocuments);
            Assert.True(e.RescuedByHybrid <= e.RelevantInHybridCandidates);
            Assert.Equal(4, e.Ndcg10.Count);
        });
        Assert.Equal(19, report.Evidence.Count(e => e.Split == "heldout"));
    }

    [Fact]
    public async Task CostsAreReported_PerRerankedPipeline_FromObservedUsage()
    {
        var (dataset, harness) = await Setup();
        var reranker = new OracleReranker(dataset, latencies: [100]);

        var report = await RerankExperiment.RunAsync(dataset, harness, reranker, 0.1, new RerankExperimentSettings(), CancellationToken.None);

        Assert.Equal([RerankExperiment.DenseRerank, RerankExperiment.HybridRerank], report.Costs.Select(c => c.Pipeline));
        Assert.Equal(40, report.Costs[0].Requests);                       // one request (20 candidates) per scored query
        Assert.Equal(40_000, report.Costs[0].InputTokens);
        Assert.Equal(0.004, report.Costs[0].ObservedDollars, 9);          // 40 000 tokens × $0.10 / 1M
    }
}

public sealed class RerankReportWriterTests
{
    [Fact]
    public async Task Markdown_StatesWhatDecides_TheBudgets_TheVerdicts_AndTheLatencyBasis()
    {
        var dataset = await DatasetLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "datasets", "golden-v1", "dataset.json"), CancellationToken.None);
        var harness = await DenseBaselineHarness.BuildAsync(dataset, new HarnessConfiguration(), CancellationToken.None);
        var oracle = new RerankExperimentTestsAccess.Oracle(dataset);

        var report = await RerankExperiment.RunAsync(dataset, harness, oracle, 0.1, new RerankExperimentSettings(), CancellationToken.None);
        var markdown = RerankReportWriter.ToMarkdown(report, "abc1234");

        Assert.Contains("The held-out slice decides", markdown);
        Assert.Contains("Candidate budget (dense and hybrid alike) | 20", markdown);
        Assert.Contains("Failure policy during the experiment | Fail", markdown);
        Assert.Contains("rerank on dense candidates (dense → dense+rerank)", markdown);
        Assert.Contains("candidate Recall@20", markdown);
        Assert.Contains("`abc1234`", markdown);
        Assert.Contains("| not measured |", markdown);   // the oracle reports no live latency: the cell must say so, not "no"
        Assert.DoesNotContain("sk-", markdown);
        Assert.Contains("\"pipeline\": \"dense+rerank\"", RerankReportWriter.ToJson(report));
    }
}

internal static class RerankExperimentTestsAccess
{
    internal sealed class Oracle(EvalDataset dataset) : IReranker
    {
        public string Name => "fake:oracle";

        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
        {
            var judged = dataset.Queries.First(q => q.Query == query).RelevantDocuments.ToDictionary(r => r.DocumentId, r => (double)r.Grade);
            return Task.FromResult<IReadOnlyList<RerankScore>>(candidates.Select(c => new RerankScore(c.ChunkId, judged.GetValueOrDefault(c.ChunkId.Split(':')[0]))).ToArray());
        }
    }
}
