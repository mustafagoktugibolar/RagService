using Rag.Core.Abstractions;
using Rag.Core.Models;

namespace Rag.Core.Retrieval;

[Flags]
public enum RetrievalCapabilities
{
    None = 0,
    Dense = 1,
    Lexical = 2
}

/// <summary>
/// Optional capability of a vector store: ranked lexical (term-based) search over chunk text, honouring the same
/// exact-match metadata filters as dense search. Kept separate from <see cref="IVectorStore"/> so stores that cannot
/// do it need no stub, and the business layer asks what a provider can do instead of assuming.
/// </summary>
public interface ILexicalSearchStore
{
    Task<IReadOnlyList<VectorSearchResult>> LexicalSearchAsync(
        string collection,
        string query,
        int topK,
        Dictionary<string, string>? filters,
        CancellationToken ct);
}

public static class StoreCapabilities
{
    public static RetrievalCapabilities Of(IVectorStore store) =>
        RetrievalCapabilities.Dense
        | (store is ILexicalSearchStore ? RetrievalCapabilities.Lexical : RetrievalCapabilities.None);
}

/// <summary>
/// The store supports lexical search in general, but this particular collection was created without the structures it
/// needs (for example a Qdrant collection from before sparse vectors). Retrievers treat it as a failed leg and degrade.
/// </summary>
public sealed class LexicalSearchUnavailableException(string collection)
    : InvalidOperationException(
        $"Collection '{collection}' has no lexical index. It was created before lexical search existed; create a new " +
        "collection and re-index its documents to enable lexical and hybrid retrieval.")
{
    public string Collection { get; } = collection;
}
