using System.Text.Json;
using Rag.Core.Options;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Tests;

/// <summary>
/// The CI quality gate. It re-runs the offline, deterministic pipeline (hashing embedder, in-memory index) on the frozen golden-v1 dataset
/// through the SAME VectorSearchService production uses, and compares document-level metrics with the committed baseline reports.
/// Everything here is local and free. The embedder is lexical, so these numbers prove the pipeline did not regress; they say nothing about
/// the quality of a real embedding model (see docs/eval/real-*.md for that).
/// A deliberate change of retrieval behaviour regenerates the baselines (<c>--label ... --strategy ...</c>) in the same commit, which makes
/// the change reviewable in the diff.
/// </summary>
public sealed class RegressionGateTests
{
    private const double Tolerance = 0.005;

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RagService.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("repository root not found"), .. parts]);
    }

    private static async Task<EvaluationReport> RunAsync(RetrievalStrategy strategy)
    {
        var dataset = await DatasetLoader.LoadAsync(RepoFile("datasets", "golden-v1", "dataset.json"), CancellationToken.None);
        var configuration = new HarnessConfiguration { Strategy = strategy };
        var harness = await DenseBaselineHarness.BuildAsync(dataset, configuration, CancellationToken.None);
        return await RetrievalEvaluator.EvaluateAsync(dataset, harness.Retriever, new EvaluationOptions(), harness.ChunkCatalog, CancellationToken.None);
    }

    private static JsonElement Baseline(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("docs", "eval", file)));
        return document.RootElement.GetProperty("report").GetProperty("document").Clone();
    }

    [Theory]
    [InlineData(RetrievalStrategy.Dense, "baseline-dense.json")]
    [InlineData(RetrievalStrategy.Lexical, "offline-lexical.json")]
    [InlineData(RetrievalStrategy.Hybrid, "offline-hybrid.json")]
    public async Task DocumentLevelMetrics_DoNotFallBelowTheCommittedBaseline(RetrievalStrategy strategy, string baselineFile)
    {
        var report = await RunAsync(strategy);
        var baseline = Baseline(baselineFile);
        Assert.Equal(baseline.GetProperty("scoredQueries").GetInt32(), report.Document.ScoredQueries);

        foreach (var k in new[] { 1, 5, 10 })
        {
            var expected = baseline.GetProperty("byK").GetProperty(k.ToString());
            var actual = report.Document.ByK[k];
            Assert.True(actual.Ndcg >= expected.GetProperty("ndcg").GetDouble() - Tolerance, $"{strategy} nDCG@{k} regressed: {actual.Ndcg:F4} < {expected.GetProperty("ndcg").GetDouble():F4}");
            Assert.True(actual.Recall >= expected.GetProperty("recall").GetDouble() - Tolerance, $"{strategy} Recall@{k} regressed: {actual.Recall:F4}");
            Assert.True(actual.Mrr >= expected.GetProperty("mrr").GetDouble() - Tolerance, $"{strategy} MRR@{k} regressed: {actual.Mrr:F4}");
        }
    }

    [Fact]
    public async Task ARerankerThatReversesTheOrder_IsVisibleToTheMetrics()
    {
        var dataset = await DatasetLoader.LoadAsync(RepoFile("datasets", "golden-v1", "dataset.json"), CancellationToken.None);
        var harness = await DenseBaselineHarness.BuildAsync(dataset, new HarnessConfiguration(), CancellationToken.None);
        var normal = await RetrievalEvaluator.EvaluateAsync(dataset, harness.Retriever, new EvaluationOptions(), harness.ChunkCatalog, CancellationToken.None);
        var reversed = await RetrievalEvaluator.EvaluateAsync(dataset, harness.RetrieverFor(new RetrievalOptions { Strategy = RetrievalStrategy.Dense, Reranking = new RerankingOptions { Enabled = true, CandidateBudget = 10 } },
            new ReverseReranker()) , new EvaluationOptions(), harness.ChunkCatalog, CancellationToken.None);

        Assert.True(reversed.Document.ByK[1].Ndcg < normal.Document.ByK[1].Ndcg);
    }

    private sealed class ReverseReranker : Rag.Core.Reranking.IReranker
    {
        public string Name => "reverse:v1";

        public Task<IReadOnlyList<Rag.Core.Reranking.RerankScore>> RerankAsync(string query, IReadOnlyList<Rag.Core.Reranking.RerankCandidate> candidates, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Rag.Core.Reranking.RerankScore>>(candidates.Select((c, i) => new Rag.Core.Reranking.RerankScore(c.ChunkId, i)).ToList());
    }
}
