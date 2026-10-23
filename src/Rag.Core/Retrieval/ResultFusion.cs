using Rag.Core.Models;

namespace Rag.Core.Retrieval;

/// <summary>One retriever's ranked output. <see cref="Source"/> names it for attribution ("dense", "lexical", …).</summary>
public sealed record RankedList(string Source, IReadOnlyList<VectorSearchResult> Results, double Weight = 1.0);

/// <summary>Where a fused chunk came from: the list, its 1-based rank there, and the retriever's own score.</summary>
public sealed record SourceRank(string Source, int Rank, double OriginalScore);

public sealed record FusedResult(VectorSearchResult Result, double Score, IReadOnlyList<SourceRank> Sources);

/// <summary>Merges several ranked lists into one. Chunk identity is <see cref="VectorSearchResult.ChunkId"/>.</summary>
public interface IResultFusion
{
    IReadOnlyList<FusedResult> Fuse(IReadOnlyList<RankedList> lists, int topK);
}

/// <summary>
/// Reciprocal Rank Fusion: score(chunk) = Σ over lists of weight / (k + rank), rank being 1-based.
/// Only ranks are used, so retrievers with unrelated score scales (cosine vs BM25) combine without normalisation.
/// <para>
/// Ordering is total and independent of list order: score descending, then best rank ascending, then chunk id
/// ordinal. A chunk repeated inside one list counts once, at its first rank.
/// </para>
/// </summary>
public sealed class ReciprocalRankFusion : IResultFusion
{
    /// <summary>Constant from the original RRF paper (Cormack et al., 2009); it damps the head of each list.</summary>
    public const int DefaultK = 60;

    private readonly int _k;

    public ReciprocalRankFusion(int k = DefaultK)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        _k = k;
    }

    public IReadOnlyList<FusedResult> Fuse(IReadOnlyList<RankedList> lists, int topK)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        if (lists.Any(l => l.Weight < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(lists), "Weights must not be negative.");
        }

        var accumulated = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var result in list.Results)
            {
                if (!seen.Add(result.ChunkId))
                {
                    continue;
                }

                rank++;
                if (!accumulated.TryGetValue(result.ChunkId, out var entry))
                {
                    entry = accumulated[result.ChunkId] = new Accumulator(result);
                }

                entry.Score += list.Weight / (_k + rank);
                entry.BestRank = Math.Min(entry.BestRank, rank);
                entry.Sources.Add(new SourceRank(list.Source, rank, result.Score));
            }
        }

        return accumulated.Values
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.BestRank)
            .ThenBy(a => a.Result.ChunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(a => new FusedResult(a.Result, a.Score, a.Sources))
            .ToArray();
    }

    private sealed class Accumulator(VectorSearchResult result)
    {
        public VectorSearchResult Result { get; } = result;

        public double Score { get; set; }

        public int BestRank { get; set; } = int.MaxValue;

        public List<SourceRank> Sources { get; } = [];
    }
}
