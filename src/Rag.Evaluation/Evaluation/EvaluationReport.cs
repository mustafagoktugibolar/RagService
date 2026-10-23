namespace Rag.Evaluation.Evaluation;

/// <summary>Mean metric values for one K over the scored queries (or the values of a single query).</summary>
public sealed record MetricSet(double Hit, double Precision, double Recall, double Mrr, double Ndcg, double Map);

public sealed record QueryScore(
    string QueryId,
    string? Category,
    IReadOnlyList<string> Ranked,
    IReadOnlyDictionary<int, MetricSet?> ByK);

public sealed record CategoryReport(int ScoredQueries, IReadOnlyDictionary<int, MetricSet> ByK);

/// <summary>Results at one granularity (document or chunk).</summary>
public sealed record LevelReport(
    int ScoredQueries,
    int SkippedQueries,
    IReadOnlyDictionary<int, MetricSet> ByK,
    IReadOnlyDictionary<string, CategoryReport> ByCategory,
    IReadOnlyList<QueryScore> PerQuery);

public sealed record LatencyStats(double MeanMs, double P50Ms, double P95Ms, double MaxMs)
{
    public static LatencyStats From(IReadOnlyList<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return new LatencyStats(0, 0, 0, 0);
        }

        var sorted = samplesMs.Order().ToArray();
        return new LatencyStats(sorted.Average(), NearestRank(sorted, 0.50), NearestRank(sorted, 0.95), sorted[^1]);
    }

    private static double NearestRank(double[] sorted, double percentile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
}

public sealed record EvaluationReport(
    string DatasetVersion,
    int QueryCount,
    LevelReport Document,
    LevelReport? Chunk,
    LatencyStats Latency);
