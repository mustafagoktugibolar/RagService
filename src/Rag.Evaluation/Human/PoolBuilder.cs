using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Human;

/// <summary>One candidate-generating system for pooling. <c>Search</c> returns chunk ids best first.</summary>
public sealed record PoolSystem(string Name, Func<string, int, IReadOnlyList<string>> Search);

/// <summary>
/// Builds the set of passages people will judge for each query: the union of several systems' top results plus a few random passages.
/// The union (rather than one system's list) keeps the judgments from favouring any one system; the random passages give annotators
/// clear non-relevant examples and let the report estimate how much relevance the pool missed. Everything here is offline and free.
/// </summary>
public static class PoolBuilder
{
    public const string RandomSource = "random";

    public static PoolKey Build(
        IReadOnlyList<PilotChunk> chunks,
        IReadOnlyList<PilotQuery> queries,
        IReadOnlyList<PoolSystem> systems,
        int depth,
        int randomNegatives,
        int seed,
        string corpusVersion)
    {
        var known = chunks.Select(c => c.ChunkId).ToHashSet(StringComparer.Ordinal);
        var pools = new Dictionary<string, IReadOnlyList<PoolItem>>(StringComparer.Ordinal);
        var rankings = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            var sources = new Dictionary<string, List<PoolSourceRank>>(StringComparer.Ordinal);
            var perSystem = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            foreach (var system in systems)
            {
                var ranked = system.Search(query.Text, depth).Where(known.Contains).Distinct(StringComparer.Ordinal).Take(depth).ToList();
                perSystem[system.Name] = ranked;
                for (var rank = 0; rank < ranked.Count; rank++)
                {
                    if (!sources.TryGetValue(ranked[rank], out var list))
                    {
                        sources[ranked[rank]] = list = [];
                    }

                    list.Add(new PoolSourceRank(system.Name, rank + 1));
                }
            }

            var random = new Random(HashCode(seed, query.Id));
            var candidates = chunks.Select(c => c.ChunkId).Where(id => !sources.ContainsKey(id)).OrderBy(_ => random.Next()).Take(randomNegatives).ToList();
            foreach (var id in candidates)
            {
                sources[id] = [new PoolSourceRank(RandomSource, 0)];
            }

            pools[query.Id] = sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new PoolItem(s.Key, s.Value)).ToList();
            rankings[query.Id] = perSystem;
        }

        return new PoolKey { CorpusVersion = corpusVersion, Seed = seed, Systems = systems.Select(s => s.Name).ToList(), Pools = pools, Rankings = rankings };
    }

    /// <summary>BM25 over the chunk texts.</summary>
    public static PoolSystem Bm25(IReadOnlyList<PilotChunk> chunks)
    {
        var index = new Bm25Index(chunks.Select(c => (c.ChunkId, (IReadOnlyList<string>)HashingEmbeddingProvider.Tokenize(c.Text).ToArray())));
        return new PoolSystem("bm25", (query, depth) =>
            index.Search(HashingEmbeddingProvider.Tokenize(query).ToArray()).Take(depth).Select(h => h.Id).ToList());
    }

    /// <summary>Deterministic hashing-embedding cosine search: a lexical-ish "dense" system that needs no network.</summary>
    public static PoolSystem HashingDense(IReadOnlyList<PilotChunk> chunks)
    {
        var embedder = new HashingEmbeddingProvider();
        var vectors = chunks.Select(c => (c.ChunkId, Vector: embedder.Embed(c.Text))).ToList();
        return new PoolSystem("hashing-dense", (query, depth) =>
        {
            var q = embedder.Embed(query);
            return vectors.Select(v => (v.ChunkId, Score: v.Vector.Zip(q, (x, y) => (double)x * y).Sum()))
                .OrderByDescending(x => x.Score).ThenBy(x => x.ChunkId, StringComparer.Ordinal).Take(depth).Select(x => x.ChunkId).ToList();
        });
    }

    private static int HashCode(int seed, string text)
    {
        unchecked
        {
            var hash = 17 * 31 + seed;
            foreach (var c in text)
            {
                hash = hash * 31 + c;
            }

            return hash;
        }
    }
}
