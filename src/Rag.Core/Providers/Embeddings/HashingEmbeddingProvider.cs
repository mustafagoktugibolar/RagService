using System.Text;
using Rag.Core.Abstractions;

namespace Rag.Core.Providers.Embeddings;

/// <summary>
/// Deterministic bag-of-words embedder (feature hashing, sublinear tf, L2-normalised). It needs no network and
/// produces identical vectors on every machine. Selected with <c>Rag:EmbeddingProvider=Hashing</c> for local development, tests and
/// the Docker end-to-end check, and used by the evaluation harness, so none of those ever call a paid embedding API.
/// <para>
/// It is NOT a semantic model: it only sees shared surface tokens. Scores obtained with it measure the pipeline and
/// the dataset, never the retrieval quality of a real embedding model, and must be labelled accordingly. Not for production relevance.
/// </para>
/// </summary>
public sealed class HashingEmbeddingProvider(int dimensions = 256) : IEmbeddingProvider
{
    public const string ModelName = "hashing-bow-v1";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is",
        "it", "of", "on", "or", "that", "the", "to", "was", "what", "when", "which", "who", "why", "with", "you"
    };

    public int Dimensions => dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToArray());

    public float[] Embed(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in Tokenize(text))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        var vector = new float[dimensions];
        foreach (var (token, count) in counts)
        {
            var hash = Fnv1a64(token);
            var index = (int)(hash % (ulong)dimensions);
            var sign = ((hash >> 63) & 1UL) == 0 ? 1f : -1f;
            vector[index] += sign * (1f + MathF.Log(count));
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    public static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (Flush(token) is { } word)
            {
                yield return word;
            }
        }

        if (Flush(token) is { } last)
        {
            yield return last;
        }
    }

    private static string? Flush(StringBuilder token)
    {
        if (token.Length == 0)
        {
            return null;
        }

        var word = token.ToString();
        token.Clear();
        return StopWords.Contains(word) ? null : word;
    }

    // string.GetHashCode is randomised per process; FNV-1a keeps vectors stable across runs and machines.
    private static ulong Fnv1a64(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
