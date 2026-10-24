using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;
using Rag.Evaluation.Tuning;

namespace Rag.Evaluation.Reranking;

public sealed record RerankExperimentSettings
{
    /// <summary>Candidates sent to the reranker; identical for the dense and the hybrid pipeline.</summary>
    public int CandidateBudget { get; init; } = 20;

    /// <summary>The hybrid configuration selected by the pre-registered tuning run (docs/eval/hybrid-tuning.md). Not re-tuned here.</summary>
    public HybridConfig Hybrid { get; init; } = new(0.5, 30, 20);

    /// <summary>Policy limit, not derived from data: p95 latency of one rerank request (20 candidates) must stay below this.</summary>
    public double RerankLatencyLimitMs { get; init; } = 1500;
}

public sealed record PipelineMetrics(
    string Pipeline,
    string Split,
    int ScoredQueries,
    double Ndcg1,
    double Ndcg5,
    double Ndcg10,
    double Mrr10,
    double Recall10,
    double CandidateRecall20);

public sealed record RerankVerdict(
    bool IntervalLowerBoundAboveZero,
    bool TopRankNotWorse,
    bool NoLargeCategoryLoss,
    bool? RerankLatencyWithinLimit,
    bool Candidate);

public sealed record RerankComparison(
    string Name,
    string Baseline,
    string Challenger,
    BootstrapResult Ndcg10Difference,
    BootstrapResult Ndcg1Difference,
    IReadOnlyDictionary<string, CategoryDifference> Categories,
    RerankVerdict Verdict);

/// <summary>Per-query evidence for explaining why one pipeline beat another: scores, and whether the relevant documents were even candidates.</summary>
public sealed record QueryEvidence(
    string QueryId,
    string? Category,
    string Split,
    IReadOnlyDictionary<string, double> Ndcg10,
    int RelevantDocuments,
    int RelevantInDenseCandidates,
    int RelevantInHybridCandidates,
    int RescuedByHybrid);

public sealed record PipelineCost(string Pipeline, int Requests, int FailedRequests, long InputTokens, long CachedInputTokens, double ObservedDollars);

public sealed record RerankReport(
    string DatasetVersion,
    string Provider,
    string? ResolvedModel,
    RerankExperimentSettings Settings,
    IReadOnlyList<PipelineMetrics> Metrics,
    IReadOnlyList<RerankComparison> Comparisons,
    IReadOnlyList<QueryEvidence> Evidence,
    IReadOnlyList<PipelineCost> Costs,
    double? RerankRequestP95Ms,
    double? RerankRequestP50Ms,
    int LiveRequests);

/// <summary>
/// The four pipelines of one provider — dense, dense+rerank, hybrid, hybrid+rerank — over the same queries, relevance judgments,
/// candidate budget and bootstrap method as the hybrid tuning run. Each pipeline runs once over every scored query; the tune and
/// held-out numbers are slices of those same per-query scores, and only the held-out slice decides. Failure policy is Fail: a
/// silent fallback to retrieval order would be indistinguishable from "the reranker did not help".
/// </summary>
public static class RerankExperiment
{
    public const string Dense = "dense";
    public const string DenseRerank = "dense+rerank";
    public const string Hybrid = "hybrid";
    public const string HybridRerank = "hybrid+rerank";
    private static readonly int[] Ks = [1, 3, 5, 10, 20];

    public static async Task<RerankReport> RunAsync(
        EvalDataset dataset,
        DenseBaselineHarness harness,
        IReranker reranker,
        double pricePerMillionInputTokens,
        RerankExperimentSettings settings,
        CancellationToken ct)
    {
        var scored = dataset with { Queries = dataset.Queries.Where(q => q.RelevantDocuments.Count > 0).ToList() };
        var usageSource = reranker as IRerankUsageSource;

        RetrievalOptions Options(RetrievalStrategy strategy, bool rerank) => new()
        {
            Strategy = strategy,
            DenseWeight = 1.0,
            LexicalWeight = settings.Hybrid.LexicalWeight,
            RrfK = settings.Hybrid.RrfK,
            CandidateK = settings.Hybrid.CandidateK,
            Reranking = new RerankingOptions
            {
                Enabled = rerank,
                Provider = RerankProviderKind.None,
                CandidateBudget = settings.CandidateBudget,
                QuestionsPerRequest = settings.CandidateBudget,
                MaxConcurrentRequests = 1,
                TimeoutSeconds = 120,
                FailurePolicy = RerankFailurePolicy.Fail
            }
        };

        var reports = new Dictionary<string, EvaluationReport>();
        var costs = new List<PipelineCost>();
        foreach (var (id, strategy, rerank) in new[]
        {
            (Dense, RetrievalStrategy.Dense, false), (DenseRerank, RetrievalStrategy.Dense, true),
            (Hybrid, RetrievalStrategy.Hybrid, false), (HybridRerank, RetrievalStrategy.Hybrid, true)
        })
        {
            var before = usageSource?.Usage;
            reports[id] = await RetrievalEvaluator.EvaluateAsync(
                scored, harness.RetrieverFor(Options(strategy, rerank), rerank ? reranker : null),
                new EvaluationOptions { Ks = Ks }, null, ct);

            if (rerank && usageSource is not null)
            {
                var after = usageSource.Usage;
                costs.Add(new PipelineCost(id, after.Requests - before!.Requests, after.FailedRequests - before.FailedRequests,
                    after.InputTokens - before.InputTokens, after.CachedInputTokens - before.CachedInputTokens,
                    CostModel.Dollars(after.InputTokens - before.InputTokens, pricePerMillionInputTokens)));
            }
        }

        var splits = scored.Queries.ToDictionary(q => q.Id, q => q.Split ?? "unassigned", StringComparer.Ordinal);
        var metrics = new List<PipelineMetrics>();
        foreach (var split in new[] { Splits.Tune, Splits.HeldOut, "all" })
        {
            foreach (var id in reports.Keys)
            {
                metrics.Add(Summarise(id, split, reports[id], splits));
            }
        }

        var latencies = usageSource?.Usage.RequestLatenciesMs ?? [];
        var p95 = latencies.Count == 0 ? (double?)null : Percentile(latencies, 0.95);
        var p50 = latencies.Count == 0 ? (double?)null : Percentile(latencies, 0.50);

        var held = splits.Where(s => s.Value == Splits.HeldOut).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var comparisons = new[]
        {
            Compare("rerank on dense candidates", Dense, DenseRerank, reports, held, p95, settings, addsRerank: true),
            Compare("rerank on hybrid candidates", Hybrid, HybridRerank, reports, held, p95, settings, addsRerank: true),
            Compare("hybrid candidates vs dense candidates, both reranked", DenseRerank, HybridRerank, reports, held, p95, settings, addsRerank: false),
            Compare("best reranked hybrid vs production default (dense)", Dense, HybridRerank, reports, held, p95, settings, addsRerank: true),
            Compare("hybrid vs dense, no reranking (reference)", Dense, Hybrid, reports, held, p95, settings, addsRerank: false)
        };

        return new RerankReport(
            dataset.DatasetVersion, reranker.Name, usageSource?.Usage.ResolvedModel, settings, metrics, comparisons,
            BuildEvidence(scored, reports, splits), costs, p95, p50, latencies.Count);
    }

    private static PipelineMetrics Summarise(string id, string split, EvaluationReport report, IReadOnlyDictionary<string, string> splits)
    {
        var rows = report.Document.PerQuery
            .Where(q => q.ByK[10] is not null && (split == "all" || splits[q.QueryId] == split)).ToList();
        double Mean(Func<MetricSet, double> pick, int k) => rows.Count == 0 ? 0 : rows.Average(q => pick(q.ByK[k]!));
        return new PipelineMetrics(
            id, split, rows.Count,
            Mean(m => m.Ndcg, 1), Mean(m => m.Ndcg, 5), Mean(m => m.Ndcg, 10), Mean(m => m.Mrr, 10), Mean(m => m.Recall, 10), Mean(m => m.Recall, 20));
    }

    private static RerankComparison Compare(
        string name, string baseline, string challenger, Dictionary<string, EvaluationReport> reports,
        HashSet<string> heldOut, double? p95Ms, RerankExperimentSettings settings, bool addsRerank)
    {
        IReadOnlyList<QueryScore> Held(string id) => reports[id].Document.PerQuery
            .Where(q => q.ByK[10] is not null && heldOut.Contains(q.QueryId)).OrderBy(q => q.QueryId, StringComparer.Ordinal).ToList();

        var a = Held(baseline);
        var b = Held(challenger);
        var ndcg10 = PairedBootstrap.MeanDifference(a.Select(q => q.ByK[10]!.Ndcg).ToArray(), b.Select(q => q.ByK[10]!.Ndcg).ToArray());
        var ndcg1 = PairedBootstrap.MeanDifference(a.Select(q => q.ByK[1]!.Ndcg).ToArray(), b.Select(q => q.ByK[1]!.Ndcg).ToArray());
        var categories = a.Where(q => q.Category is not null).GroupBy(q => q.Category!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new CategoryDifference(g.Count(), g.Average(q => q.ByK[10]!.Ndcg),
                b.Where(h => g.Any(d => d.QueryId == h.QueryId)).Average(h => h.ByK[10]!.Ndcg)),
            StringComparer.Ordinal);

        var judged = HybridTuningRun.Judge(ndcg10, ndcg1, categories);
        // Latency can only be judged from live provider requests; with none (everything cached) the condition is "not measured"
        // and a reranking pipeline cannot be a candidate.
        bool? latencyOk = addsRerank ? p95Ms is { } p ? p <= settings.RerankLatencyLimitMs : false : null;
        var candidate = judged.Candidate && latencyOk != false;
        return new RerankComparison(name, baseline, challenger, ndcg10, ndcg1, categories,
            new RerankVerdict(judged.IntervalLowerBoundAboveZero, judged.TopRankNotWorse, judged.NoLargeCategoryLoss, latencyOk, candidate));
    }

    private static IReadOnlyList<QueryEvidence> BuildEvidence(
        EvalDataset scored, Dictionary<string, EvaluationReport> reports, IReadOnlyDictionary<string, string> splits)
    {
        QueryScore Of(string id, string queryId) => reports[id].Document.PerQuery.Single(q => q.QueryId == queryId);
        return scored.Queries.Select(query =>
        {
            var relevant = query.RelevantDocuments.Select(r => r.DocumentId).ToHashSet(StringComparer.Ordinal);
            var dense = Of(Dense, query.Id).Ranked.Take(20).ToHashSet(StringComparer.Ordinal);
            var hybrid = Of(Hybrid, query.Id).Ranked.Take(20).ToHashSet(StringComparer.Ordinal);
            return new QueryEvidence(
                query.Id, query.Category, splits[query.Id],
                reports.Keys.ToDictionary(id => id, id => Of(id, query.Id).ByK[10]!.Ndcg),
                relevant.Count,
                relevant.Count(dense.Contains),
                relevant.Count(hybrid.Contains),
                relevant.Count(d => hybrid.Contains(d) && !dense.Contains(d)));
        }).ToList();
    }

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }
}
