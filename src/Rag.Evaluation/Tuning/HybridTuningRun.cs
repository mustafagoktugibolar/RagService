using System.Globalization;
using System.Text;
using Rag.Core.Options;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Tuning;

public sealed record CategoryDifference(int Queries, double DenseNdcg10, double HybridNdcg10);

/// <summary>The three adoption conditions of docs/eval/protocol-hybrid-tuning.md, each evaluated separately.</summary>
public sealed record AdoptionVerdict(
    bool IntervalLowerBoundAboveZero,
    bool TopRankNotWorse,
    bool NoLargeCategoryLoss,
    bool Candidate);

public sealed record HeldOutComparison(
    HybridConfig Hybrid,
    MetricSet Dense,
    MetricSet HybridMetrics,
    BootstrapResult Ndcg10Difference,
    BootstrapResult Ndcg1Difference,
    IReadOnlyDictionary<string, CategoryDifference> Categories,
    AdoptionVerdict Verdict);

public sealed record TuningReport(
    string DatasetVersion,
    int TuneQueries,
    int HeldOutQueries,
    GridRow DenseOnTune,
    GridRow DefaultHybridOnTune,
    IReadOnlyList<GridRow> Grid,
    HybridConfig Selected,
    HeldOutComparison HeldOut);

public static class HybridTuningRun
{
    public const double CategoryLossTolerance = 0.05;
    public const int MinimumCategoryQueries = 3;

    public static async Task<TuningReport> RunAsync(EvalDataset dataset, DenseBaselineHarness harness, CancellationToken ct)
    {
        var tune = dataset with { Queries = dataset.Queries.Where(q => q.Split == Splits.Tune).ToList() };
        var heldOut = dataset with { Queries = dataset.Queries.Where(q => q.Split == Splits.HeldOut).ToList() };

        var denseOptions = new RetrievalOptions { Strategy = RetrievalStrategy.Dense };
        var denseOnTune = ToRow(new HybridConfig(0, 0, 0), await EvaluateAsync(tune, harness.RetrieverFor(denseOptions), ct));
        var defaultConfig = new HybridConfig(1.0, 60, 50);
        var defaultOnTune = ToRow(defaultConfig, await EvaluateAsync(tune, harness.RetrieverFor(HybridOptions(defaultConfig)), ct));

        var grid = new List<GridRow>();
        foreach (var config in HybridGrid.Configurations())
        {
            var level = await EvaluateAsync(tune, harness.RetrieverFor(HybridOptions(config)), ct);
            grid.Add(ToRow(config, level));
        }

        var selected = HybridGrid.Select(grid);

        // The held-out split is first read here, once, with the already-selected configuration.
        var denseHeld = await EvaluateAsync(heldOut, harness.RetrieverFor(denseOptions), ct);
        var hybridHeld = await EvaluateAsync(heldOut, harness.RetrieverFor(HybridOptions(selected)), ct);

        return new TuningReport(
            dataset.DatasetVersion,
            tune.Queries.Count,
            heldOut.Queries.Count,
            denseOnTune,
            defaultOnTune,
            grid,
            selected,
            Compare(selected, denseHeld, hybridHeld));
    }

    public static RetrievalOptions HybridOptions(HybridConfig config) => new()
    {
        Strategy = RetrievalStrategy.Hybrid,
        DenseWeight = 1.0,
        LexicalWeight = config.LexicalWeight,
        RrfK = config.RrfK,
        CandidateK = config.CandidateK
    };

    private static async Task<LevelReport> EvaluateAsync(EvalDataset subset, RetrieveAsync retrieve, CancellationToken ct) =>
        (await RetrievalEvaluator.EvaluateAsync(subset, retrieve, new EvaluationOptions { Ks = [1, 3, 5, 10] }, null, ct)).Document;

    private static GridRow ToRow(HybridConfig config, LevelReport level) =>
        new(config, level.ByK[10].Ndcg, level.ByK[5].Ndcg);

    public static HeldOutComparison Compare(HybridConfig selected, LevelReport dense, LevelReport hybrid)
    {
        var denseScored = dense.PerQuery.Where(q => q.ByK[10] is not null).OrderBy(q => q.QueryId, StringComparer.Ordinal).ToList();
        var hybridScored = hybrid.PerQuery.Where(q => q.ByK[10] is not null).OrderBy(q => q.QueryId, StringComparer.Ordinal).ToList();
        if (!denseScored.Select(q => q.QueryId).SequenceEqual(hybridScored.Select(q => q.QueryId)))
        {
            throw new InvalidOperationException("Dense and hybrid were not scored on the same queries.");
        }

        var ndcg10 = PairedBootstrap.MeanDifference(
            denseScored.Select(q => q.ByK[10]!.Ndcg).ToArray(), hybridScored.Select(q => q.ByK[10]!.Ndcg).ToArray());
        var ndcg1 = PairedBootstrap.MeanDifference(
            denseScored.Select(q => q.ByK[1]!.Ndcg).ToArray(), hybridScored.Select(q => q.ByK[1]!.Ndcg).ToArray());

        var categories = denseScored
            .Where(q => q.Category is not null)
            .GroupBy(q => q.Category!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new CategoryDifference(
                    g.Count(),
                    g.Average(q => q.ByK[10]!.Ndcg),
                    hybridScored.Where(h => g.Any(d => d.QueryId == h.QueryId)).Average(h => h.ByK[10]!.Ndcg)),
                StringComparer.Ordinal);

        var verdict = Judge(ndcg10, ndcg1, categories);
        return new HeldOutComparison(selected, dense.ByK[10], hybrid.ByK[10], ndcg10, ndcg1, categories, verdict);
    }

    public static AdoptionVerdict Judge(
        BootstrapResult ndcg10, BootstrapResult ndcg1, IReadOnlyDictionary<string, CategoryDifference> categories)
    {
        var lower = ndcg10.Lower > 0;
        var top = ndcg1.MeanDifference >= 0;
        var categoriesOk = categories.Values
            .Where(c => c.Queries >= MinimumCategoryQueries)
            .All(c => Math.Round(c.HybridNdcg10 - c.DenseNdcg10, 6) >= -CategoryLossTolerance);   // rounding: 0.75 - 0.80 is -0.05000000000000004 in floating point
        return new AdoptionVerdict(lower, top, categoriesOk, lower && top && categoriesOk);
    }

    public static string ToMarkdown(TuningReport report, string embedder)
    {
        string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        string S(double v) => v.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        var held = report.HeldOut;
        var b = new StringBuilder();
        b.AppendLine("# Hybrid tuning result");
        b.AppendLine();
        b.AppendLine($"Protocol: `protocol-hybrid-tuning.md` (pre-registered). Dataset `{report.DatasetVersion}`, embedder `{embedder}`.");
        b.AppendLine($"Tuning split: {report.TuneQueries} queries. Held-out split: {report.HeldOutQueries} queries (read once, after selection).");
        b.AppendLine();
        b.AppendLine("## Tuning split (document level)");
        b.AppendLine();
        b.AppendLine("| configuration | lexical weight | RRF k | candidates | nDCG@10 | nDCG@5 |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|");
        b.AppendLine($"| dense only | — | — | — | {F(report.DenseOnTune.Ndcg10)} | {F(report.DenseOnTune.Ndcg5)} |");
        b.AppendLine($"| default hybrid | 1.0 | 60 | 50 | {F(report.DefaultHybridOnTune.Ndcg10)} | {F(report.DefaultHybridOnTune.Ndcg5)} |");
        foreach (var row in report.Grid.OrderByDescending(r => Math.Round(r.Ndcg10, 4)).ThenByDescending(r => Math.Round(r.Ndcg5, 4)).Take(10))
        {
            var mark = row.Config == report.Selected ? " **← selected**" : string.Empty;
            b.AppendLine($"| grid{mark} | {row.Config.LexicalWeight} | {row.Config.RrfK} | {row.Config.CandidateK} | {F(row.Ndcg10)} | {F(row.Ndcg5)} |");
        }

        b.AppendLine();
        b.AppendLine($"(top 10 of {report.Grid.Count} configurations shown; all are in the JSON.)");
        b.AppendLine();
        b.AppendLine("## Held-out validation (document level)");
        b.AppendLine();
        b.AppendLine($"Selected: lexical weight {held.Hybrid.LexicalWeight}, RRF k {held.Hybrid.RrfK}, {held.Hybrid.CandidateK} candidates.");
        b.AppendLine();
        b.AppendLine("| | Hit@10 | Recall@10 | MRR@10 | nDCG@10 |");
        b.AppendLine("|---|---:|---:|---:|---:|");
        b.AppendLine($"| dense | {F(held.Dense.Hit)} | {F(held.Dense.Recall)} | {F(held.Dense.Mrr)} | {F(held.Dense.Ndcg)} |");
        b.AppendLine($"| tuned hybrid | {F(held.HybridMetrics.Hit)} | {F(held.HybridMetrics.Recall)} | {F(held.HybridMetrics.Mrr)} | {F(held.HybridMetrics.Ndcg)} |");
        b.AppendLine();
        b.AppendLine($"nDCG@10 difference (hybrid − dense): {S(held.Ndcg10Difference.MeanDifference)}, 95 % bootstrap interval [{S(held.Ndcg10Difference.Lower)}, {S(held.Ndcg10Difference.Upper)}] over {held.Ndcg10Difference.Samples} queries.");
        b.AppendLine($"nDCG@1 difference: {S(held.Ndcg1Difference.MeanDifference)}.");
        b.AppendLine();
        b.AppendLine("| category | held-out queries | dense nDCG@10 | hybrid nDCG@10 | difference |");
        b.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (category, c) in held.Categories.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            b.AppendLine($"| {category} | {c.Queries} | {F(c.DenseNdcg10)} | {F(c.HybridNdcg10)} | {S(c.HybridNdcg10 - c.DenseNdcg10)} |");
        }

        b.AppendLine();
        b.AppendLine("## Verdict against the pre-registered rule");
        b.AppendLine();
        b.AppendLine($"1. Interval lower bound > 0: **{(held.Verdict.IntervalLowerBoundAboveZero ? "pass" : "fail")}**");
        b.AppendLine($"2. nDCG@1 difference ≥ 0: **{(held.Verdict.TopRankNotWorse ? "pass" : "fail")}**");
        b.AppendLine($"3. No category with ≥ {HybridTuningRun.MinimumCategoryQueries} queries loses more than {CategoryLossTolerance:F2}: **{(held.Verdict.NoLargeCategoryLoss ? "pass" : "fail")}**");
        b.AppendLine();
        b.AppendLine(held.Verdict.Candidate
            ? "**Adoption candidate.** The default is not changed here; hybrid is re-tested after reranking."
            : "**Not adopted.** Hybrid stays off by default.");
        return b.ToString();
    }
}
