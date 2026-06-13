using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Options;

namespace Rag.Core.Providers.Embeddings;

public sealed class CachingEmbeddingProvider(
    IEmbeddingProvider inner,
    IDistributedCache cache,
    IOptions<RagOptions> ragOptions,
    IOptions<EmbeddingCacheOptions> cacheOptions,
    int? dimensions) : IEmbeddingProvider
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var key = BuildKey(text);
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null)
            return Deserialize(cached);

        var embedding = await inner.EmbedAsync(text, ct);
        await cache.SetAsync(key, Serialize(embedding), BuildEntryOptions(), ct);
        return embedding;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
            return [];

        var results = new float[texts.Count][];
        var missIndexes = new List<int>();

        for (var i = 0; i < texts.Count; i++)
        {
            var cached = await cache.GetAsync(BuildKey(texts[i]), ct);
            if (cached is not null)
                results[i] = Deserialize(cached);
            else
                missIndexes.Add(i);
        }

        if (missIndexes.Count > 0)
        {
            var missTexts = missIndexes.Select(i => texts[i]).ToArray();
            var embeddings = await inner.EmbedBatchAsync(missTexts, ct);
            var entryOptions = BuildEntryOptions();

            for (var j = 0; j < missIndexes.Count; j++)
            {
                var originalIndex = missIndexes[j];
                results[originalIndex] = embeddings[j];
                await cache.SetAsync(BuildKey(texts[originalIndex]), Serialize(embeddings[j]), entryOptions, ct);
            }
        }

        return results;
    }

    private string BuildKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var dimSegment = dimensions.HasValue ? $":{dimensions}" : string.Empty;
        return $"rag:emb:{ragOptions.Value.EmbeddingModel}{dimSegment}:{Convert.ToHexString(hash)}";
    }

    private DistributedCacheEntryOptions BuildEntryOptions() => new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(cacheOptions.Value.TtlHours)
    };

    private static byte[] Serialize(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        MemoryMarshal.AsBytes(embedding.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static float[] Deserialize(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(embedding);
        return embedding;
    }
}
