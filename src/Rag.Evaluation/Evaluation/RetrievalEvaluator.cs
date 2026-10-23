using System.Diagnostics;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Metrics;

namespace Rag.Evaluation.Evaluation;

/// <summary>One retrieved chunk, in rank order.</summary>
public sealed record RankedHit(string DocumentId, string ChunkId, string Text);

/// <summary>A chunk that exists in the index; the catalog of these defines chunk-level recall denominators.</summary>
public sealed record ChunkRecord(string DocumentId, string ChunkId, string Text);

/// <summary>The system under test: returns ranked chunks for a query, at least <c>depth</c> deep if available.</summary>
public delegate Task<IReadOnlyList<RankedHit>> RetrieveAsync(EvalQuery query, int depth, CancellationToken ct);

public sealed record EvaluationOptions
{
    public IReadOnlyList<int> Ks { get; init; } = [1, 3, 5, 10];
}

/// <summary>
/// Scores a retriever against a labeled dataset at document level and, when a chunk catalog is supplied,
/// at chunk level. Exceptions from the retriever propagate: a run with silently missing queries is not comparable.
/// </summary>
public static class RetrievalEvaluator
{
    public static async Task<EvaluationReport> EvaluateAsync(
        EvalDataset dataset,
        RetrieveAsync retrieve,
        EvaluationOptions? options,
        IReadOnlyList<ChunkRecord>? chunkCatalog,
        CancellationToken ct)
    {
        options ??= new EvaluationOptions();
        var ks = options.Ks.Distinct().Order().ToArray();
        if (ks.Length == 0 || ks[0] <= 0)
        {
            throw new ArgumentException("Ks must contain at least one positive value.", nameof(options));
        }

        var depth = ks[^1];
        var latencies = new List<double>(dataset.Queries.Count);
        var documentScores = new List<(EvalQuery Query, IReadOnlyList<string> Ranked, Dictionary<string, int> Judgments)>();
        var chunkScores = new List<(EvalQuery Query, IReadOnlyList<string> Ranked, Dictionary<string, int> Judgments)>();

        foreach (var query in dataset.Queries)
        {
            ct.ThrowIfCancellationRequested();

            var started = Stopwatch.GetTimestamp();
            var hits = await retrieve(query, depth, ct);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            var documentRanking = hits.Select(h => h.DocumentId).Distinct(StringComparer.Ordinal).ToArray();
            var documentJudgments = query.RelevantDocuments.ToDictionary(d => d.DocumentId, d => d.Grade, StringComparer.Ordinal);
            documentScores.Add((query, documentRanking, documentJudgments));

            if (chunkCatalog is not null)
            {
                var chunkRanking = hits.Select(h => h.ChunkId).Distinct(StringComparer.Ordinal).ToArray();
                chunkScores.Add((query, chunkRanking, ChunkJudgments(query, chunkCatalog)));
            }
        }

        return new EvaluationReport(
            dataset.DatasetVersion,
            dataset.Queries.Count,
            BuildLevel(documentScores, ks),
            chunkCatalog is null ? null : BuildLevel(chunkScores, ks),
            LatencyStats.From(latencies));
    }

    /// <summary>
    /// Explicit chunk ids win. Otherwise every catalog chunk of a judged document that contains the document's
    /// evidence phrase is relevant, with the document's grade. Documents without evidence contribute nothing.
    /// </summary>
    private static Dictionary<string, int> ChunkJudgments(EvalQuery query, IReadOnlyList<ChunkRecord> catalog)
    {
        if (query.RelevantChunkIds is { Count: > 0 })
        {
            return new Dictionary<string, int>(query.RelevantChunkIds, StringComparer.Ordinal);
        }

        var judgments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relevant in query.RelevantDocuments.Where(d => !string.IsNullOrWhiteSpace(d.Evidence)))
        {
            foreach (var chunk in catalog.Where(c => c.DocumentId == relevant.DocumentId
                                                     && TextMatch.Contains(c.Text, relevant.Evidence!)))
            {
                judgments[chunk.ChunkId] = relevant.Grade;
            }
        }

        return judgments;
    }

    private static LevelReport BuildLevel(
        List<(EvalQuery Query, IReadOnlyList<string> Ranked, Dictionary<string, int> Judgments)> scored,
        int[] ks)
    {
        var perQuery = scored.Select(item => new QueryScore(
            item.Query.Id,
            item.Query.Category,
            item.Ranked,
            ks.ToDictionary(k => k, k => Score(item.Ranked, item.Judgments, k)))).ToList();

        var evaluated = perQuery.Where(q => q.ByK[ks[0]] is not null).ToList();

        var byCategory = evaluated
            .Where(q => q.Category is not null)
            .GroupBy(q => q.Category!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new CategoryReport(g.Count(), Average(g.ToList(), ks)),
                StringComparer.Ordinal);

        return new LevelReport(
            evaluated.Count,
            perQuery.Count - evaluated.Count,
            Average(evaluated, ks),
            byCategory,
            perQuery);
    }

    private static MetricSet? Score(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> judgments, int k)
    {
        var hit = RankingMetrics.HitAtK(ranked, judgments, k);
        if (hit is null)
        {
            return null;
        }

        return new MetricSet(
            hit.Value,
            RankingMetrics.PrecisionAtK(ranked, judgments, k)!.Value,
            RankingMetrics.RecallAtK(ranked, judgments, k)!.Value,
            RankingMetrics.MrrAtK(ranked, judgments, k)!.Value,
            RankingMetrics.NdcgAtK(ranked, judgments, k)!.Value,
            RankingMetrics.AveragePrecisionAtK(ranked, judgments, k)!.Value);
    }

    private static Dictionary<int, MetricSet> Average(IReadOnlyList<QueryScore> queries, int[] ks) =>
        ks.ToDictionary(k => k, k => queries.Count == 0
            ? new MetricSet(0, 0, 0, 0, 0, 0)
            : new MetricSet(
                queries.Average(q => q.ByK[k]!.Hit),
                queries.Average(q => q.ByK[k]!.Precision),
                queries.Average(q => q.ByK[k]!.Recall),
                queries.Average(q => q.ByK[k]!.Mrr),
                queries.Average(q => q.ByK[k]!.Ndcg),
                queries.Average(q => q.ByK[k]!.Map)));
}
