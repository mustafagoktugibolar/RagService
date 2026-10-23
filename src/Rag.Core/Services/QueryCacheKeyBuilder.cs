using System.Security.Cryptography;
using System.Text;

namespace Rag.Core.Services;

/// <summary>
/// Everything that can change the result list of a query. A cached result is valid only for an
/// identical fingerprint; later retrieval stages (hybrid, reranker, transformer) add fields here.
/// </summary>
public sealed record RetrievalFingerprint
{
    public required string Collection { get; init; }

    /// <summary>Content version of the collection; changes on every index/delete.</summary>
    public required string IndexVersion { get; init; }

    public required string EmbeddingProvider { get; init; }

    public required string EmbeddingModel { get; init; }

    public required int VectorSize { get; init; }

    /// <summary>Retrieval strategy identifier, e.g. "dense".</summary>
    public required string Strategy { get; init; }

    public required int TopK { get; init; }

    public required string Query { get; init; }

    public IReadOnlyDictionary<string, string>? Filters { get; init; }
}

public static class QueryCacheKeyBuilder
{
    /// <summary>Bump when the field set or encoding changes, so old entries become unreachable.</summary>
    public const int SchemaVersion = 2;

    public static string Build(RetrievalFingerprint fingerprint)
    {
        var builder = new StringBuilder();
        Append(builder, SchemaVersion.ToString());
        Append(builder, fingerprint.Collection);
        Append(builder, fingerprint.IndexVersion);
        Append(builder, fingerprint.EmbeddingProvider.ToLowerInvariant());
        Append(builder, fingerprint.EmbeddingModel);
        Append(builder, fingerprint.VectorSize.ToString());
        Append(builder, fingerprint.Strategy);
        Append(builder, fingerprint.TopK.ToString());
        Append(builder, fingerprint.Query);

        var filters = fingerprint.Filters;
        Append(builder, (filters?.Count ?? 0).ToString());
        if (filters is not null)
        {
            foreach (var (key, value) in filters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                Append(builder, key);
                Append(builder, value);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"rag:query:v{SchemaVersion}:{Convert.ToHexString(hash)}";
    }

    // Length-prefixing makes the encoding injective: no value can imitate a delimiter.
    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}
