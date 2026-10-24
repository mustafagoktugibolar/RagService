using Microsoft.Extensions.Logging;
using Rag.Core.Abstractions;
using Rag.Core.Options;

namespace Rag.Core.Retrieval;

public sealed record RetrievalRequest(
    string Collection,
    string Query,
    float[]? Embedding,
    int TopK,
    Dictionary<string, string>? Filters,
    string? OriginalQuery = null)
{
    /// <summary>What the user typed. Retrieval searches <see cref="Query"/> (possibly rewritten); relevance is always judged against this.</summary>
    public string JudgedQuery => OriginalQuery ?? Query;
}

/// <summary>Ranked chunks plus machine-readable notes about anything that degraded (e.g. one hybrid leg failed).</summary>
public sealed record RetrievalOutcome(IReadOnlyList<FusedResult> Results, IReadOnlyList<string> Warnings);

public interface IRetriever
{
    Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct);
}

public sealed class DenseRetriever(IVectorStore store) : IRetriever
{
    public const string Source = "dense";

    public async Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
    {
        var embedding = request.Embedding
            ?? throw new ArgumentException("Dense retrieval needs a query embedding.", nameof(request));
        var results = await store.SearchAsync(request.Collection, embedding, request.TopK, request.Filters, ct);
        return new RetrievalOutcome(RetrievalMapping.SingleSource(Source, results), []);
    }
}

public sealed class LexicalRetriever(ILexicalSearchStore store) : IRetriever
{
    public const string Source = "lexical";

    public async Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
    {
        var results = await store.LexicalSearchAsync(request.Collection, request.Query, request.TopK, request.Filters, ct);
        return new RetrievalOutcome(RetrievalMapping.SingleSource(Source, results), []);
    }
}

/// <summary>
/// Runs the dense and lexical legs concurrently with identical filters, then fuses by rank. A failing leg degrades
/// the query to the other leg (reported in <see cref="RetrievalOutcome.Warnings"/>); only if both fail does it throw.
/// Cancellation is never treated as a degradation.
/// </summary>
public sealed class HybridRetriever(
    IVectorStore denseStore,
    ILexicalSearchStore lexicalStore,
    IResultFusion fusion,
    RetrievalOptions options,
    ILogger logger) : IRetriever
{
    public const string DenseFailed = "hybrid:dense-leg-failed";
    public const string LexicalFailed = "hybrid:lexical-leg-failed";

    public async Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
    {
        var embedding = request.Embedding
            ?? throw new ArgumentException("Hybrid retrieval needs a query embedding.", nameof(request));

        var candidates = Math.Max(options.CandidateK, request.TopK);
        var denseTask = denseStore.SearchAsync(request.Collection, embedding, candidates, request.Filters, ct);
        var lexicalTask = lexicalStore.LexicalSearchAsync(request.Collection, request.Query, candidates, request.Filters, ct);

        await Task.WhenAll(Settle(denseTask), Settle(lexicalTask));
        ct.ThrowIfCancellationRequested();

        var lists = new List<RankedList>(2);
        var warnings = new List<string>(1);

        if (denseTask.IsCompletedSuccessfully)
        {
            lists.Add(new RankedList(DenseRetriever.Source, denseTask.Result, options.DenseWeight));
        }
        else
        {
            warnings.Add(DenseFailed);
            logger.LogWarning(denseTask.Exception?.GetBaseException(), "Dense leg of hybrid retrieval failed for {Collection}", request.Collection);
        }

        if (lexicalTask.IsCompletedSuccessfully)
        {
            lists.Add(new RankedList(LexicalRetriever.Source, lexicalTask.Result, options.LexicalWeight));
        }
        else
        {
            warnings.Add(LexicalFailed);
            logger.LogWarning(lexicalTask.Exception?.GetBaseException(), "Lexical leg of hybrid retrieval failed for {Collection}", request.Collection);
        }

        if (lists.Count == 0)
        {
            throw new InvalidOperationException(
                "Both legs of hybrid retrieval failed.",
                new AggregateException(denseTask.Exception!.GetBaseException(), lexicalTask.Exception!.GetBaseException()));
        }

        return new RetrievalOutcome(fusion.Fuse(lists, request.TopK), warnings);
    }

    // Lets Task.WhenAll finish both legs without throwing, so each outcome can be inspected individually.
    private static async Task Settle(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // inspected via task.Exception / IsCompletedSuccessfully
        }
    }
}

internal static class RetrievalMapping
{
    public static IReadOnlyList<FusedResult> SingleSource(string source, IReadOnlyList<Models.VectorSearchResult> results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mapped = new List<FusedResult>(results.Count);
        foreach (var result in results)
        {
            if (!seen.Add(result.ChunkId))
            {
                continue;
            }

            mapped.Add(new FusedResult(result, result.Score, [new SourceRank(source, mapped.Count + 1, result.Score)]));
        }

        return mapped;
    }
}
