using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Human;

/// <summary>Pure measures of how much of one ranking a pool of other rankings captures.</summary>
public static class PoolCoverage
{
    /// <summary>|top-k(a) ∩ top-k(b)| / k.</summary>
    public static double Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b, int k)
    {
        var top = b.Take(k).ToHashSet(StringComparer.Ordinal);
        return a.Take(k).Count(top.Contains) / (double)k;
    }

    /// <summary>Share of a reference's top-k that is present in a pool. An empty reference is fully covered.</summary>
    public static double Coverage(IReadOnlyList<string> reference, IReadOnlySet<string> pool, int k)
    {
        var top = reference.Take(k).ToList();
        return top.Count == 0 ? 1.0 : top.Count(pool.Contains) / (double)top.Count;
    }

    /// <summary>The coverage a pool of this size would give a reference by luck alone: poolSize / corpusSize, capped at 1.</summary>
    public static double ChanceCoverage(int poolSize, int corpusSize) => Math.Min(1.0, poolSize / (double)corpusSize);

    public static int UnionSize(IReadOnlyDictionary<string, IReadOnlyList<string>> rankings, int depth) =>
        rankings.Values.SelectMany(r => r.Take(depth)).Distinct(StringComparer.Ordinal).Count();

    public static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b, int depth)
    {
        var x = a.Take(depth).ToHashSet(StringComparer.Ordinal);
        var y = b.Take(depth).ToHashSet(StringComparer.Ordinal);
        var union = x.Union(y).Count();
        return union == 0 ? 1.0 : x.Intersect(y).Count() / (double)union;
    }

    /// <summary>For each system, the number of top-depth passages that no other system returned.</summary>
    public static IReadOnlyDictionary<string, int> UniqueContribution(IReadOnlyDictionary<string, IReadOnlyList<string>> rankings, int depth) =>
        rankings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Take(depth).Count(id => !rankings.Any(other => other.Key != pair.Key && other.Value.Take(depth).Contains(id))));
}

public sealed record ProxyQuery(string Text, string SourceChunkId);

/// <summary>
/// Synthetic queries made from a passage's own mid-frequency terms, used ONLY to estimate pool sizes and how much the pooling systems overlap
/// before people have written real queries. They are keyword-style by construction, so they overstate the overlap between lexical systems and
/// say nothing about paraphrase behaviour. They are never labeled, never shown to annotators and never enter any dataset.
/// </summary>
public static class ProxyQueries
{
    public static IReadOnlyList<ProxyQuery> Generate(IReadOnlyList<PilotChunk> chunks, int count, int termsPerQuery, int seed)
    {
        var tokens = chunks.ToDictionary(c => c.ChunkId, c => HashingEmbeddingProvider.Tokenize(c.Text).Where(t => t.Length >= 4 && !t.All(char.IsDigit)).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var documentFrequency = tokens.Values.SelectMany(t => t).GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var maxFrequency = Math.Max(2, chunks.Count / 4);

        var random = new Random(seed);
        var queries = new List<ProxyQuery>();
        foreach (var chunk in chunks.OrderBy(_ => random.Next()))
        {
            var usable = tokens[chunk.ChunkId].Where(t => documentFrequency[t] >= 2 && documentFrequency[t] <= maxFrequency).ToList();
            if (usable.Count < termsPerQuery)
            {
                continue;
            }

            queries.Add(new ProxyQuery(string.Join(' ', usable.OrderBy(_ => random.Next()).Take(termsPerQuery)), chunk.ChunkId));
            if (queries.Count == count)
            {
                break;
            }
        }

        return queries;
    }
}
