using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Rag.Core.Abstractions;

namespace Rag.Evaluation.Harness;

/// <summary>
/// Persists embeddings on disk, keyed by model and a hash of the text, so repeated evaluation runs (different strategies
/// over the same corpus and queries) pay for each embedding once. Embeddings are a pure function of model and text, so
/// entries never go stale. The cache directory must not be committed.
/// </summary>
public sealed class FileCachingEmbeddingProvider(IEmbeddingProvider inner, string model, string directory) : IEmbeddingProvider
{
    public int CacheHits { get; private set; }

    public int CacheMisses { get; private set; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        (await EmbedBatchAsync([text], ct))[0];

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var results = new float[texts.Count][];
        var missing = new List<int>();

        for (var i = 0; i < texts.Count; i++)
        {
            var path = PathFor(texts[i]);
            if (File.Exists(path))
            {
                results[i] = Read(path);
                CacheHits++;
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var embedded = await inner.EmbedBatchAsync(missing.Select(i => texts[i]).ToArray(), ct);
            for (var j = 0; j < missing.Count; j++)
            {
                results[missing[j]] = embedded[j];
                Write(PathFor(texts[missing[j]]), embedded[j]);
                CacheMisses++;
            }
        }

        return results;
    }

    private string PathFor(string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{model}\n{text}")));
        return Path.Combine(directory, $"{hash}.bin");
    }

    private static float[] Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var vector = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(vector);
        return vector;
    }

    private static void Write(string path, float[] vector) =>
        File.WriteAllBytes(path, MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
}
