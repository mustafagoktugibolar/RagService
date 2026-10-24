using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rag.Evaluation.Reranking;

public static class RerankReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // reports are read by people: keep '+' etc. readable
    };

    public static string ToJson(RerankReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string ToMarkdown(RerankReport report, string? gitCommit)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Reranking experiment — {report.Provider}");
        b.AppendLine();
        b.AppendLine("| Setting | Value |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| Reranker identity | `{report.Provider}` |");
        b.AppendLine($"| Resolved model reported by provider | `{report.ResolvedModel ?? "none reported"}` |");
        b.AppendLine($"| Dataset | `{report.DatasetVersion}` (scored queries only) |");
        b.AppendLine($"| Candidate budget (dense and hybrid alike) | {report.Settings.CandidateBudget} |");
        b.AppendLine($"| Hybrid configuration (from the tuning run, not re-tuned) | lexical weight {report.Settings.Hybrid.LexicalWeight}, RRF k {report.Settings.Hybrid.RrfK}, {report.Settings.Hybrid.CandidateK} candidates per leg |");
        b.AppendLine("| Failure policy during the experiment | Fail (a silent fallback would be indistinguishable from \"no gain\") |");
        b.AppendLine($"| Git commit | `{gitCommit ?? "unknown"}` |");
        b.AppendLine();
        b.AppendLine("**The held-out slice decides. Tune and pooled slices are descriptive.** No parameter was tuned on any of them.");
        b.AppendLine();

        foreach (var (split, title) in new[] { ("heldout", "Held-out (decides)"), ("tune", "Tune (descriptive)"), ("all", "All scored queries (descriptive)") })
        {
            var rows = report.Metrics.Where(m => m.Split == split).ToList();
            b.AppendLine($"## {title} — {rows[0].ScoredQueries} queries");
            b.AppendLine();
            b.AppendLine("| pipeline | nDCG@1 | nDCG@5 | nDCG@10 | MRR@10 | Recall@10 | candidate Recall@20 |");
            b.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var m in rows)
            {
                b.AppendLine($"| {m.Pipeline} | {F(m.Ndcg1)} | {F(m.Ndcg5)} | {F(m.Ndcg10)} | {F(m.Mrr10)} | {F(m.Recall10)} | {F(m.CandidateRecall20)} |");
            }

            b.AppendLine();
        }

        b.AppendLine("## Comparisons on the held-out split (paired bootstrap, 10 000 resamples, 95 % interval)");
        b.AppendLine();
        b.AppendLine("| comparison | nDCG@10 difference [interval] | nDCG@1 difference | interval > 0 | nDCG@1 ≥ 0 | no category loss > 0.05 | rerank latency ≤ limit | candidate |");
        b.AppendLine("|---|---|---:|:-:|:-:|:-:|:-:|:-:|");
        foreach (var c in report.Comparisons)
        {
            b.AppendLine($"| {c.Name} ({c.Baseline} → {c.Challenger}) | {S(c.Ndcg10Difference.MeanDifference)} [{S(c.Ndcg10Difference.Lower)}, {S(c.Ndcg10Difference.Upper)}] | {S(c.Ndcg1Difference.MeanDifference)} | {Tick(c.Verdict.IntervalLowerBoundAboveZero)} | {Tick(c.Verdict.TopRankNotWorse)} | {Tick(c.Verdict.NoLargeCategoryLoss)} | {Tick(c.Verdict.RerankLatencyWithinLimit)} | **{Tick(c.Verdict.Candidate)}** |");
        }

        b.AppendLine();
        b.AppendLine("`n/a` = not applicable to that comparison; `not measured` would mean no live request was made.");
        b.AppendLine();

        b.AppendLine("## Latency and cost");
        b.AppendLine();
        b.AppendLine(report.RerankRequestP95Ms is null
            ? "No live provider request was made (everything replayed from cache): rerank latency was **not measured**."
            : $"Rerank request latency over {report.LiveRequests} live requests (20 candidates each): p50 {report.RerankRequestP50Ms:F0} ms, p95 {report.RerankRequestP95Ms:F0} ms; policy limit {report.Settings.RerankLatencyLimitMs:F0} ms. Local retrieval latency is excluded (embeddings were replayed from disk).");
        b.AppendLine();
        b.AppendLine("| pipeline | provider requests | failed | input tokens | cached input tokens | observed cost (USD) |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var c in report.Costs)
        {
            b.AppendLine($"| {c.Pipeline} | {c.Requests} | {c.FailedRequests} | {c.InputTokens} | {c.CachedInputTokens} | {c.ObservedDollars.ToString("0.000000", CultureInfo.InvariantCulture)} |");
        }

        b.AppendLine();
        b.AppendLine("Requests and tokens count only live calls: pairs already scored by an earlier pipeline are replayed from the pair cache, so the hybrid pipeline pays only for candidates dense did not already cover.");
        b.AppendLine();

        var contrast = report.Evidence
            .Select(e => (Row: e, Delta: e.Ndcg10[RerankExperiment.HybridRerank] - e.Ndcg10[RerankExperiment.DenseRerank]))
            .Where(x => Math.Abs(x.Delta) >= 0.10).OrderByDescending(x => Math.Abs(x.Delta)).Take(15).ToList();
        b.AppendLine("## Query-level evidence: where hybrid candidates changed the reranked outcome (|Δ nDCG@10| ≥ 0.10)");
        b.AppendLine();
        if (contrast.Count == 0)
        {
            b.AppendLine("No query differed by 0.10 or more between dense+rerank and hybrid+rerank.");
        }
        else
        {
            b.AppendLine("| query | category | split | dense+rerank | hybrid+rerank | Δ | relevant docs | in dense top-20 | in hybrid top-20 | rescued by hybrid |");
            b.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var (e, delta) in contrast)
            {
                b.AppendLine($"| {e.QueryId} | {e.Category} | {e.Split} | {F(e.Ndcg10[RerankExperiment.DenseRerank])} | {F(e.Ndcg10[RerankExperiment.HybridRerank])} | {S(delta)} | {e.RelevantDocuments} | {e.RelevantInDenseCandidates} | {e.RelevantInHybridCandidates} | {e.RescuedByHybrid} |");
            }
        }

        b.AppendLine();
        b.AppendLine("A reranked hybrid can beat reranked dense only when its candidate set contains relevant documents dense missed (\"rescued by hybrid\" > 0) or when the extra candidates are ordered better; the columns above show which.");
        return b.ToString();
    }

    private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string S(double v) => v.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);

    private static string Tick(bool? v) => v switch { true => "yes", false => "no", null => "n/a" };
}
