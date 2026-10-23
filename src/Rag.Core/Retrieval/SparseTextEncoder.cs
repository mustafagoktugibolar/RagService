using System.Text;

namespace Rag.Core.Retrieval;

/// <summary>Lowercased runs of letters/digits. Single-character tokens are dropped as noise.</summary>
public static class LexicalTokenizer
{
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 1)
            {
                tokens.Add(current.ToString());
            }

            current.Clear();
        }
    }
}

public readonly record struct SparseVector(uint[] Indices, float[] Values)
{
    public bool IsEmpty => Indices.Length == 0;
}

/// <summary>
/// Turns text into the sparse vectors a lexical index stores. Each distinct token maps to a stable 32-bit dimension
/// (FNV-1a), so no vocabulary has to be persisted or shared between indexers and queriers.
/// <para>
/// Document side carries the BM25 term-frequency component, tf·(k1+1)/(tf + k1·(1 − b + b·dl/avgdl)). Query side is 1 per
/// distinct term. The index applies IDF (Qdrant's <c>Modifier.Idf</c>), so the dot product equals a BM25 score.
/// </para>
/// <para>
/// avgdl is a fixed assumption (<see cref="AssumedAverageLength"/>): a streaming indexer cannot know the corpus average
/// when it writes a chunk. Length normalisation is therefore approximate; revisit if retrieval evaluation shows it matters.
/// </para>
/// </summary>
public static class SparseTextEncoder
{
    public const double K1 = 1.2;
    public const double B = 0.75;
    public const double AssumedAverageLength = 200;

    public static SparseVector EncodeDocument(string text)
    {
        var tokens = LexicalTokenizer.Tokenize(text);
        var frequencies = Count(tokens);
        var normaliser = K1 * (1 - B + B * tokens.Count / AssumedAverageLength);
        return Build(frequencies, tf => (float)(tf * (K1 + 1) / (tf + normaliser)));
    }

    public static SparseVector EncodeQuery(string text) =>
        Build(Count(LexicalTokenizer.Tokenize(text)), _ => 1f);

    /// <summary>FNV-1a, 32 bit. Pinned by a test: changing it silently invalidates every existing lexical index.</summary>
    public static uint IndexForToken(string token)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    private static Dictionary<uint, int> Count(IReadOnlyList<string> tokens)
    {
        var frequencies = new Dictionary<uint, int>();
        foreach (var token in tokens)
        {
            var index = IndexForToken(token);
            frequencies[index] = frequencies.GetValueOrDefault(index) + 1;
        }

        return frequencies;
    }

    private static SparseVector Build(Dictionary<uint, int> frequencies, Func<int, float> weight)
    {
        var ordered = frequencies.OrderBy(pair => pair.Key).ToArray();
        return new SparseVector(
            ordered.Select(pair => pair.Key).ToArray(),
            ordered.Select(pair => weight(pair.Value)).ToArray());
    }
}
