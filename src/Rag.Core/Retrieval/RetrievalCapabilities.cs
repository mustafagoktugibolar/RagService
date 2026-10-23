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
