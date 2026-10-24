using System.Globalization;
using System.Text;
using Rag.Evaluation.Metrics;

namespace Rag.Evaluation.Human;

public sealed record PilotInputs(
    CorpusManifest Manifest,
    IReadOnlyList<Annotator> Annotators,
    IReadOnlyList<PilotQuery> Queries,
    IReadOnlyList<PilotChunk> Chunks,
    PoolKey Pool,
    AnnotatorJudgments A,
    AnnotatorJudgments B,
    IReadOnlyList<FinalJudgment> Final,
    IReadOnlyList<SplitIsolation.Overlap> Overlaps,
    int PlannedFinalScoredQueries = 160,
    int PlannedFinalNoAnswerQueries = 30);

public sealed record CriterionResult(string Id, string Description, string Threshold, string Observed, string Status);

public sealed record SystemHeadroom(string System, double MeanNdcg10, double ShareBelow90, int ScoredQueries);

public sealed record PilotReportData(
    string Decision,
    IReadOnlyList<CriterionResult> Criteria,
    double? WeightedKappa,
    KappaInterval? KappaInterval,
    double? BinaryKappa,
    double ExactAgreement,
    double AdjacentAgreement,
    int[,] Confusion,
    int Disagreements,
    IReadOnlyList<SystemHeadroom> Headroom,
    double? MedianSecondsPerItem,
    double? ExtrapolatedAnnotatorHours,
    double? MedianPoolSize,
    double? ProxySigma,
    int? ProxyRequiredQueries);

/// <summary>
/// The pilot's pre-registered acceptance criteria (docs/eval/protocol-human-pilot.md) as code. Every threshold lives here, once, and the
/// protocol's table is checked against these constants by a test, so neither can drift from the other unnoticed.
/// </summary>
public static class PilotReport
{
    public const string Pass = "pass";
    public const string Review = "review";
    public const string Fail = "FAIL";

    public const double KappaTarget = 0.60;
    public const double KappaFloor = 0.40;
    public const double MinimumCompleteness = 0.98;
    public const double MinimumAnswerable = 0.85;
    public const double HeadroomMeanCeiling = 0.90;
    public const double HeadroomFailMean = 0.95;
    public const double HeadroomMinimumShareBelow = 0.30;
    public const double MedianSecondsPass = 45;
    public const double MedianSecondsFail = 60;
    public const int PoolMedianMin = 15;
    public const int PoolMedianMax = 30;
    public const double DeltaOfInterest = 0.05;
    public const int RelevantGrade = 2;

    public static string ClassifyKappa(double? weighted, double? binary)
    {
        if (weighted is not { } w)
        {
            return Fail;   // undefined: the raters' marginals carry no information
        }

        if (w < KappaFloor)
        {
            return Fail;
        }

        return w >= KappaTarget && binary is { } b && b >= KappaTarget ? Pass : Review;
    }

    public static string ClassifyHeadroom(double bestMean, double shareBelow90) =>
        bestMean >= HeadroomFailMean ? Fail
        : bestMean <= HeadroomMeanCeiling && shareBelow90 >= HeadroomMinimumShareBelow ? Pass
        : Review;

    public static string ClassifyEffort(double? medianSeconds) =>
        medianSeconds is not { } s ? Review : s <= MedianSecondsPass ? Pass : s <= MedianSecondsFail ? Review : Fail;

    public static PilotReportData Build(PilotInputs input)
    {
        var criteria = new List<CriterionResult>();

        // P1 — safety
        var signed = CorpusBuilder.IsSignedOff(input.Manifest);
        var findings = input.Manifest.Safety.Findings;
        criteria.Add(new("P1", "Corpus is safe to send to an external provider", "scanner clean and owner sign-off bound to the manifest",
            $"findings: {findings}; signed off: {(signed ? "yes" : "no")}", findings == 0 && signed ? Pass : Fail));

        // P2 — queries (count, quotas, human writers)
        string queryStatus;
        string queryObserved;
        try
        {
            PilotValidator.ValidateAnnotators(input.Annotators);
            PilotValidator.ValidateQueries(input.Queries, input.Annotators);
            queryStatus = Pass;
            queryObserved = $"{input.Queries.Count} queries, quotas met, all by registered humans";
        }
        catch (PilotValidationException ex)
        {
            queryStatus = Fail;
            queryObserved = $"{ex.Errors.Count} problem(s): {ex.Errors[0]}";
        }

        criteria.Add(new("P2", "Query set: 20-30 human-written queries meeting the category minimums", "valid", queryObserved, queryStatus));

        // P3 — isolation from other query sets
        criteria.Add(new("P3", "No pilot query matches a golden-v1 or final-set query", "0 overlaps",
            $"{input.Overlaps.Count} overlap(s)", input.Overlaps.Count == 0 ? Pass : Fail));

        // P4 — completeness
        var pooled = input.Pool.Pools.SelectMany(p => p.Value.Select(i => (p.Key, i.ChunkId))).ToHashSet();
        var gradedByBoth = input.A.Items.Select(i => (i.QueryId, i.ChunkId)).Intersect(input.B.Items.Select(i => (i.QueryId, i.ChunkId))).Count(pair => pooled.Contains(pair));
        var completeness = pooled.Count == 0 ? 0 : gradedByBoth / (double)pooled.Count;
        criteria.Add(new("P4", "Pooled items graded by both annotators", $">= {MinimumCompleteness:P0}", completeness.ToString("P1", CultureInfo.InvariantCulture),
            completeness >= MinimumCompleteness ? Pass : Fail));

        // P5 — agreement (on items both graded)
        var bKey = input.B.Items.ToDictionary(i => (i.QueryId, i.ChunkId));
        var both = input.A.Items.Where(i => bKey.ContainsKey((i.QueryId, i.ChunkId))).ToList();
        var ga = both.Select(i => i.Grade).ToArray();
        var gb = both.Select(i => bKey[(i.QueryId, i.ChunkId)].Grade).ToArray();
        double? weighted = null, binary = null;
        KappaInterval? interval = null;
        var exact = 0.0;
        var adjacent = 0.0;
        var confusion = new int[PilotValidator.Grades, PilotValidator.Grades];
        if (both.Count > 0)
        {
            weighted = AgreementMetrics.WeightedKappa(ga, gb, PilotValidator.Grades);
            binary = AgreementMetrics.BinaryKappa(ga, gb, RelevantGrade);
            interval = AgreementMetrics.WeightedKappaInterval(both.Select(i => i.QueryId).ToArray(), ga, gb, PilotValidator.Grades);
            exact = AgreementMetrics.ExactAgreement(ga, gb);
            adjacent = AgreementMetrics.AdjacentAgreement(ga, gb);
            confusion = AgreementMetrics.Confusion(ga, gb, PilotValidator.Grades);
        }

        criteria.Add(new("P5", "Inter-annotator agreement (quadratic-weighted kappa; binary kappa at grade >= 2)",
            $"weighted >= {KappaTarget:0.00} and binary >= {KappaTarget:0.00}; {KappaFloor:0.00}-{KappaTarget:0.00} review; below {KappaFloor:0.00} fail",
            $"weighted {Format(weighted)}, binary {Format(binary)}", ClassifyKappa(weighted, binary)));

        // P6 — answerability
        var finalGrades = input.Final.ToDictionary(f => (f.QueryId, f.ChunkId), f => f.Grade);
        var answerable = input.Queries.Where(q => q.Category != PilotCategories.NoAnswer).ToList();
        var withRelevant = answerable.Count(q => input.Final.Any(f => f.QueryId == q.Id && f.Grade >= RelevantGrade));
        var share = answerable.Count == 0 ? 0 : withRelevant / (double)answerable.Count;
        var noAnswerWithRelevant = input.Queries.Count(q => q.Category == PilotCategories.NoAnswer && input.Final.Any(f => f.QueryId == q.Id && f.Grade >= RelevantGrade));
        criteria.Add(new("P6", "Answerable queries have a relevant passage; no-answer queries have none",
            $">= {MinimumAnswerable:P0} of answerable queries; 0 no-answer queries with grade >= {RelevantGrade}",
            $"{share:P0} answerable; {noAnswerWithRelevant} no-answer with a relevant passage",
            share >= MinimumAnswerable && noAnswerWithRelevant == 0 ? Pass : Fail));

        // P7 — headroom of the free pooling systems on the final labels
        var headroom = new List<SystemHeadroom>();
        var perQueryNdcg = new Dictionary<string, Dictionary<string, double>>();
        foreach (var system in input.Pool.Systems)
        {
            var values = new Dictionary<string, double>();
            foreach (var query in input.Queries)
            {
                var judgments = input.Final.Where(f => f.QueryId == query.Id).ToDictionary(f => f.ChunkId, f => f.Grade);
                if (input.Pool.Rankings.TryGetValue(query.Id, out var ranks) && ranks.TryGetValue(system, out var ranking)
                    && RankingMetrics.NdcgAtK(ranking, judgments, 10) is { } ndcg)
                {
                    values[query.Id] = ndcg;
                }
            }

            perQueryNdcg[system] = values;
            if (values.Count > 0)
            {
                headroom.Add(new SystemHeadroom(system, values.Values.Average(), values.Values.Count(v => v < 0.9) / (double)values.Count, values.Count));
            }
        }

        if (headroom.Count > 0)
        {
            var best = headroom.MaxBy(h => h.MeanNdcg10)!;
            criteria.Add(new("P7", "Headroom: the best free baseline leaves room to measure a gain of 0.05 nDCG@10",
                $"best mean nDCG@10 <= {HeadroomMeanCeiling:0.00} and >= {HeadroomMinimumShareBelow:P0} of queries below 0.90; >= {HeadroomFailMean:0.00} fails (ceiling)",
                $"best system {best.System}: mean {best.MeanNdcg10:0.000}, {best.ShareBelow90:P0} of queries below 0.90", ClassifyHeadroom(best.MeanNdcg10, best.ShareBelow90)));
        }
        else
        {
            criteria.Add(new("P7", "Headroom of the free baselines", "computable", "no system has a scorable query", Fail));
        }

        // P8 — effort
        var seconds = input.A.Items.Concat(input.B.Items).Where(i => i.Seconds is > 0).Select(i => i.Seconds!.Value).Order().ToList();
        double? median = seconds.Count == 0 ? null : Median(seconds);
        criteria.Add(new("P8", "Annotation effort per judgment (median)", $"<= {MedianSecondsPass:0} s; {MedianSecondsPass:0}-{MedianSecondsFail:0} s review; above fails",
            median is null ? "no timings recorded" : $"{median:0.0} s", ClassifyEffort(median)));

        // P9 — pool size
        var sizes = input.Pool.Pools.Values.Select(p => (double)p.Count).Order().ToList();
        double? poolMedian = sizes.Count == 0 ? null : Median(sizes);
        criteria.Add(new("P9", "Unique candidates per query (median)", $"{PoolMedianMin}-{PoolMedianMax}",
            poolMedian is null ? "no pools" : $"{poolMedian:0.0}", poolMedian is { } m && m >= PoolMedianMin && m <= PoolMedianMax ? Pass : Review));

        // P10 — final labels are all human-sourced
        var pendingResolved = input.Final.All(f => f.Source is "agreed" or "adjudicated");
        criteria.Add(new("P10", "Every final label is an agreement of two humans or a recorded human adjudication", "all labels",
            pendingResolved ? "yes" : "unresolved labels exist", pendingResolved && input.Final.Count > 0 ? Pass : Fail));

        // proxy sigma and the re-planned number of queries
        double? sigma = null;
        int? required = null;
        var comparable = headroom.OrderByDescending(h => h.MeanNdcg10).Take(2).Select(h => h.System).ToList();
        if (comparable.Count == 2)
        {
            var shared = perQueryNdcg[comparable[0]].Keys.Intersect(perQueryNdcg[comparable[1]].Keys).ToList();
            if (shared.Count > 2)
            {
                var diffs = shared.Select(q => perQueryNdcg[comparable[0]][q] - perQueryNdcg[comparable[1]][q]).ToList();
                var mean = diffs.Average();
                sigma = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1));
                required = (int)Math.Ceiling(Math.Pow((1.959964 + 0.841621) * sigma.Value / DeltaOfInterest, 2));
            }
        }

        double? hours = null;
        if (median is { } secondsPerItem && poolMedian is { } pool)
        {
            hours = secondsPerItem * (input.PlannedFinalScoredQueries + input.PlannedFinalNoAnswerQueries) * pool * 2 / 3600.0;
        }

        var decision = criteria.Any(c => c.Status == Fail) ? "NO-GO: fix the failed criteria and repeat the affected pilot steps"
            : criteria.Any(c => c.Status == Review) ? "REVISE: adjust guidelines, pool depth or passage length as indicated, then re-run the affected steps"
            : "GO: proceed to the final-dataset phase with guidelines unchanged";

        return new PilotReportData(decision, criteria, weighted, interval, binary, exact, adjacent, confusion,
            FinalJudgments.Disagreements(input.A, input.B).Count, headroom, median, hours, poolMedian, sigma, required);
    }

    public static string ToMarkdown(PilotReportData r, PilotInputs input)
    {
        var b = new StringBuilder();
        b.AppendLine("# Human-labeling pilot — report");
        b.AppendLine();
        b.AppendLine($"**Decision: {r.Decision}**");
        b.AppendLine();
        b.AppendLine($"Corpus `{input.Manifest.CorpusVersion}` ({input.Manifest.ChunkCount} chunks, chunking {input.Manifest.Chunking.ChunkSize}/{input.Manifest.Chunking.ChunkOverlap}), {input.Queries.Count} pilot queries, guidelines `{input.A.GuidelinesVersion}`.");
        b.AppendLine("Pooled with free offline systems only; passages that only an OpenAI-embedding dense run would retrieve are not in the pool (a top-up judging round is needed before such a system is scored).");
        b.AppendLine();
        b.AppendLine("| id | criterion | threshold | observed | status |");
        b.AppendLine("|---|---|---|---|---|");
        foreach (var c in r.Criteria)
        {
            b.AppendLine($"| {c.Id} | {c.Description} | {c.Threshold} | {c.Observed} | **{c.Status}** |");
        }

        b.AppendLine();
        b.AppendLine("## Agreement");
        b.AppendLine();
        b.AppendLine($"Weighted kappa {Format(r.WeightedKappa)}" + (r.KappaInterval is { } ci ? $" (95 % query-cluster bootstrap [{ci.Lower:0.00}, {ci.Upper:0.00}])" : "") +
                     $"; binary kappa (grade >= {RelevantGrade}) {Format(r.BinaryKappa)}; exact {r.ExactAgreement:P0}; within one grade {r.AdjacentAgreement:P0}; items needing adjudication: {r.Disagreements}.");
        b.AppendLine();
        b.AppendLine("Confusion (rows: annotator A, columns: annotator B):");
        b.AppendLine();
        b.AppendLine("| A \\ B | 0 | 1 | 2 | 3 |");
        b.AppendLine("|---|---:|---:|---:|---:|");
        for (var i = 0; i < r.Confusion.GetLength(0); i++)
        {
            b.AppendLine($"| {i} | {string.Join(" | ", Enumerable.Range(0, r.Confusion.GetLength(1)).Select(j => r.Confusion[i, j]))} |");
        }

        b.AppendLine();
        b.AppendLine("## Headroom of the free pooling systems on the final labels");
        b.AppendLine();
        b.AppendLine("| system | scored queries | mean nDCG@10 | share of queries below 0.90 |");
        b.AppendLine("|---|---:|---:|---:|");
        foreach (var h in r.Headroom)
        {
            b.AppendLine($"| {h.System} | {h.ScoredQueries} | {h.MeanNdcg10:0.000} | {h.ShareBelow90:P0} |");
        }

        b.AppendLine();
        b.AppendLine("## Effort and re-planning");
        b.AppendLine();
        b.AppendLine($"Median seconds per judgment: {(r.MedianSecondsPerItem is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) : "not recorded")}. " +
                     $"Median pool size: {(r.MedianPoolSize is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")} candidates per query. " +
                     (r.ExtrapolatedAnnotatorHours is { } h2 ? $"Extrapolated for {input.PlannedFinalScoredQueries + input.PlannedFinalNoAnswerQueries} final queries with two annotators: about {h2:0} annotator-hours in total." : "Extrapolation needs recorded timings."));
        b.AppendLine();
        b.AppendLine(r.ProxySigma is { } sg && r.ProxyRequiredQueries is { } n
            ? $"Proxy variance: the per-query nDCG@10 difference between the two best free systems has standard deviation {sg:0.000}, which would need about **{n}** held-out queries to detect delta = {DeltaOfInterest:0.00} (80 % power, alpha 0.05). **This is a proxy**: the comparison that matters (dense vs dense + reranker) is not measured here and may have a different variance."
            : "Proxy variance could not be computed (fewer than three shared scorable queries).");
        b.AppendLine();
        b.AppendLine("## What this pilot cannot show");
        b.AppendLine();
        b.AppendLine("- It does not measure any reranker, OpenAI or otherwise; no paid call was made.");
        b.AppendLine("- Its labels exist only for the pooled passages; unpooled passages are treated as non-relevant, which can understate systems unlike the pooling systems.");
        b.AppendLine("- With two annotators, kappa is an estimate with a wide interval at this size.");
        return b.ToString();
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static string Format(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "undefined";
}
