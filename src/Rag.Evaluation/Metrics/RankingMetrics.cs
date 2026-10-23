namespace Rag.Evaluation.Metrics;

/// <summary>
/// Rank-based retrieval metrics for a single query. All functions are pure and deterministic.
/// <para>
/// Conventions (documented once, applied everywhere):
/// <list type="bullet">
/// <item>A ranking is an ordered list of ids; a repeated id counts only at its first position.</item>
/// <item>Judgments map id → graded relevance; grade &gt; 0 means relevant, 0 means judged non-relevant.</item>
/// <item>If a query has no relevant id the metric is <c>null</c> (undefined), never 0 — such queries must be
/// excluded from averages and reported separately, otherwise "no-answer" queries silently drag scores down.</item>
/// <item>Precision@K divides by K even if fewer than K results were returned.</item>
/// <item>Recall@K divides by the total number of relevant ids, including those never retrieved.</item>
/// <item>nDCG uses gain 2^grade − 1 and discount log2(rank + 1); the ideal ranking is built from all judged ids.</item>
/// <item>AP@K divides by min(R, K) so a perfect top-K ranking scores 1 even when R &gt; K.</item>
/// </list>
/// </para>
/// </summary>
public static class RankingMetrics
{
    public static double? HitAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        if (RelevantCount(judgments) == 0)
        {
            return null;
        }

        return Gains(ranked, judgments, k).Any(grade => grade > 0) ? 1.0 : 0.0;
    }

    public static double? PrecisionAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        if (RelevantCount(judgments) == 0)
        {
            return null;
        }

        return Gains(ranked, judgments, k).Count(grade => grade > 0) / (double)k;
    }

    public static double? RecallAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        var relevant = RelevantCount(judgments);
        if (relevant == 0)
        {
            return null;
        }

        return Gains(ranked, judgments, k).Count(grade => grade > 0) / (double)relevant;
    }

    public static double? MrrAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        if (RelevantCount(judgments) == 0)
        {
            return null;
        }

        var position = 0;
        foreach (var grade in Gains(ranked, judgments, k))
        {
            position++;
            if (grade > 0)
            {
                return 1.0 / position;
            }
        }

        return 0.0;
    }

    public static double? NdcgAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        if (RelevantCount(judgments) == 0)
        {
            return null;
        }

        var dcg = Dcg(Gains(ranked, judgments, k));
        var ideal = Dcg(judgments.Values.Where(grade => grade > 0).OrderByDescending(grade => grade).Take(k));
        return dcg / ideal;
    }

    public static double? AveragePrecisionAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        ValidateK(k);
        var relevant = RelevantCount(judgments);
        if (relevant == 0)
        {
            return null;
        }

        double sum = 0;
        var hits = 0;
        var position = 0;
        foreach (var grade in Gains(ranked, judgments, k))
        {
            position++;
            if (grade > 0)
            {
                hits++;
                sum += hits / (double)position;
            }
        }

        return sum / Math.Min(relevant, k);
    }

    /// <summary>Grades of the first <paramref name="k"/> distinct ids of the ranking (0 for unjudged ids).</summary>
    private static List<int> Gains(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var grades = new List<int>(k);
        foreach (var id in ranked)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            grades.Add(judgments.TryGetValue(id, out var grade) ? grade : 0);
            if (grades.Count == k)
            {
                break;
            }
        }

        return grades;
    }

    private static double Dcg(IEnumerable<int> grades)
    {
        double sum = 0;
        var position = 0;
        foreach (var grade in grades)
        {
            position++;
            sum += (Math.Pow(2, grade) - 1) / Math.Log2(position + 1);
        }

        return sum;
    }

    private static int RelevantCount(IReadOnlyDictionary<string, int> judgments) =>
        judgments.Values.Count(grade => grade > 0);

    private static void ValidateK(int k)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "K must be positive.");
        }
    }
}
