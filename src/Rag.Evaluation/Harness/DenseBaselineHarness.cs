using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Providers.Chunking;
using Rag.Core.Services;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;

namespace Rag.Evaluation.Harness;

/// <summary>
/// Runs the repository's real <see cref="IndexDocumentService"/>, <see cref="DataIngestionTokenChunker"/> and
/// <see cref="VectorSearchService"/> (the current dense pipeline) over a dataset corpus, replacing only the network
/// edges: an in-memory vector store, a deterministic embedder and an in-memory text source.
/// </summary>
public sealed class DenseBaselineHarness
{
    private const string Collection = "eval";

    public required VectorSearchService Search { get; init; }

    public required IVectorStore Store { get; init; }

    public required IEmbeddingProvider Embedder { get; init; }

    public required RagOptions RagSettings { get; init; }

    public required IReadOnlyList<ChunkRecord> ChunkCatalog { get; init; }

    public required HarnessConfiguration Configuration { get; init; }

    public static async Task<DenseBaselineHarness> BuildAsync(
        EvalDataset dataset,
        HarnessConfiguration configuration,
        CancellationToken ct,
        EmbedderSpec? embedderSpec = null)
    {
        embedderSpec ??= EmbedderSpec.Hashing(configuration.VectorSize);
        configuration = configuration with { VectorSize = embedderSpec.VectorSize };
        var embedder = embedderSpec.Provider;
        var store = new InMemoryCosineVectorStore();
        var chunker = new RecordingChunker(new DataIngestionTokenChunker(
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions
            {
                ChunkSize = configuration.ChunkSize,
                ChunkOverlap = configuration.ChunkOverlap
            }),
            Microsoft.Extensions.Options.Options.Create(RagOptionsFor(configuration, embedderSpec))));

        var indexer = new IndexDocumentService(
            new CorpusTextExtractor(dataset.Corpus),
            chunker,
            embedder,
            store,
            Microsoft.Extensions.Options.Options.Create(RagOptionsFor(configuration, embedderSpec)),
            NullLogger<IndexDocumentService>.Instance);

        foreach (var document in dataset.Corpus)
        {
            var response = await indexer.IndexAsync(new IndexDocumentRequest
            {
                RequestId = $"index-{document.DocumentId}",
                Collection = Collection,
                DocumentId = document.DocumentId,
                FilePath = document.DocumentId,
                Metadata = document.Metadata
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(
                    $"Indexing '{document.DocumentId}' failed: {response.ErrorMessage}");
            }
        }

        var search = new VectorSearchService(
            embedder,
            store,
            new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            Microsoft.Extensions.Options.Options.Create(RagOptionsFor(configuration, embedderSpec)),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = false }),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions
            {
                Strategy = configuration.Strategy,
                CandidateK = configuration.CandidateK,
                RrfK = configuration.RrfK
            }));

        return new DenseBaselineHarness
        {
            Search = search,
            Store = store,
            Embedder = embedder,
            RagSettings = RagOptionsFor(configuration, embedderSpec),
            ChunkCatalog = chunker.Produced,
            Configuration = configuration
        };
    }

    /// <summary>The retriever under test (the harness configuration's strategy), shaped for <see cref="RetrievalEvaluator"/>.</summary>
    public RetrieveAsync Retriever => ToRetriever(Search);

    /// <summary>
    /// A retriever over the same already-built index but with different retrieval options. Lets a tuning run compare many
    /// configurations without re-chunking or re-embedding the corpus.
    /// </summary>
    public RetrieveAsync RetrieverFor(RetrievalOptions options, IReranker? reranker = null) => ToRetriever(new VectorSearchService(
        Embedder,
        Store,
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
        Microsoft.Extensions.Options.Options.Create(RagSettings),
        Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = false }),
        NullLogger<VectorSearchService>.Instance,
        Microsoft.Extensions.Options.Options.Create(options),
        reranker));

    private static RetrieveAsync ToRetriever(VectorSearchService search) => async (query, depth, ct) =>
    {
        var response = await search.SearchAsync(new QueryRequest
        {
            RequestId = $"eval-{query.Id}",
            Collection = Collection,
            Query = query.Query,
            TopK = depth,
            Filters = query.Filters
        }, ct);

        if (!response.Success)
        {
            throw new InvalidOperationException($"Query '{query.Id}' failed: {response.ErrorMessage}");
        }

        return response.Results.Select(r => new RankedHit(r.DocumentId, r.ChunkId, r.Text)).ToArray();
    };

    private static RagOptions RagOptionsFor(HarnessConfiguration configuration, EmbedderSpec embedderSpec) => new()
    {
        EmbeddingProvider = embedderSpec.ProviderName,
        EmbeddingModel = embedderSpec.Model,
        VectorSize = configuration.VectorSize,
        DefaultTopK = 5
    };
}

/// <summary>Everything that defines a run besides the dataset; recorded verbatim in reports.</summary>
public sealed record HarnessConfiguration
{
    public int ChunkSize { get; init; } = 1000;

    public int ChunkOverlap { get; init; } = 200;

    public int VectorSize { get; init; } = 256;

    public RetrievalStrategy Strategy { get; init; } = RetrievalStrategy.Dense;

    public int CandidateK { get; init; } = 50;

    public int RrfK { get; init; } = 60;
}

internal sealed class CorpusTextExtractor(IReadOnlyList<CorpusDocument> corpus) : ITextExtractor
{
    public Task<string> ExtractAsync(string filePath, CancellationToken ct) =>
        Task.FromResult(corpus.First(d => d.DocumentId == filePath).Text);
}

/// <summary>Decorates a chunker and remembers every chunk it produced: that list is the chunk-level catalog.</summary>
internal sealed class RecordingChunker(IChunker inner) : IChunker
{
    private readonly List<ChunkRecord> _produced = [];

    public IReadOnlyList<ChunkRecord> Produced => _produced;

    public async Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string documentId, string text, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var chunks = await inner.ChunkAsync(documentId, text, metadata, ct);
        _produced.AddRange(chunks.Select(c => new ChunkRecord(c.DocumentId, c.ChunkId, c.Text)));
        return chunks;
    }
}
