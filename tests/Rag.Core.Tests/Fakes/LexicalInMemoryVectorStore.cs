using Rag.Core.Models;
using Rag.Core.Retrieval;

namespace Rag.Core.Tests.Fakes;

/// <summary>
/// <see cref="InMemoryVectorStore"/> that also supports lexical search (count of distinct shared lowercase words).
/// Lets tests force dense and lexical to disagree by indexing chunks directly.
/// </summary>
public sealed class LexicalInMemoryVectorStore : InMemoryVectorStore, ILexicalSearchStore
{
    public Exception? LexicalFailure { get; set; }

    public int LexicalCalls { get; private set; }

    public Task<IReadOnlyList<VectorSearchResult>> LexicalSearchAsync(
        string collection, string query, int topK, Dictionary<string, string>? filters, CancellationToken ct)
    {
        LexicalCalls++;
        if (LexicalFailure is not null)
        {
            return Task.FromException<IReadOnlyList<VectorSearchResult>>(LexicalFailure);
        }

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        IReadOnlyList<VectorSearchResult> results = Documents(collection, filters)
            .Select(d => (Doc: d, Hits: d.Text.ToLowerInvariant().Split(' ').Distinct().Count(terms.Contains)))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Doc.ChunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => new VectorSearchResult
            {
                DocumentId = x.Doc.DocumentId, ChunkId = x.Doc.ChunkId, Text = x.Doc.Text, Score = x.Hits, Metadata = x.Doc.Metadata
            })
            .ToArray();
        return Task.FromResult(results);
    }
}
