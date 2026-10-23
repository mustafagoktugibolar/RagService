using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core.Options;
using Rag.Core.Retrieval;

namespace Rag.Core.Reranking;

public sealed record RerankCandidate(string ChunkId, string Text);

/// <summary>A relevance score for a candidate, addressed by chunk id so identity never depends on list position.</summary>
public sealed record RerankScore(string ChunkId, double Score);

/// <summary>
/// Scores candidates against a query with a model that reads both together (cross-encoder style). Provider-independent:
/// local ONNX cross-encoders, hosted APIs and other providers all fit behind it.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// Stable identity of the provider and model, e.g. <c>onnx:ms-marco-MiniLM-L6-v2</c>. It is part of the query-cache
    /// fingerprint: results reranked by different models must never share a cache entry.
    /// </summary>
    string Name { get; }

    /// <summary>Returns a score per candidate it could score; higher is more relevant. May omit candidates.</summary>
    Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct);
}

/// <summary>
/// Second stage on top of any retriever: fetch a candidate budget, let the reranker order it, return the top K.
/// Chunk identity, text, metadata and the inner retriever's source attribution are preserved; a "rerank" source is added.
/// Any reranker failure or timeout degrades to the inner retriever's order and is reported in the warnings.
/// </summary>
public sealed class RerankingRetriever(
    IRetriever inner,
    IReranker reranker,
    RerankOptions options,
    ILogger logger) : IRetriever
{
    public const string Source = "rerank";
    public const string Failed = "rerank:failed";
    public const string TimedOut = "rerank:timeout";
    public const string Incomplete = "rerank:incomplete";

    public async Task<RetrievalOutcome> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
    {
        var budget = Math.Max(options.CandidateBudget, request.TopK);
        var fetched = await inner.RetrieveAsync(request with { TopK = budget }, ct);

        var candidates = fetched.Results.Take(options.CandidateBudget).ToArray();
        if (candidates.Length < 2)
        {
            return Truncate(fetched, request.TopK, []);
        }

        IReadOnlyList<RerankScore> scores;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            scores = await reranker.RerankAsync(
                request.Query,
                candidates.Select(c => new RerankCandidate(c.Result.ChunkId, c.Result.Text)).ToArray(),
                timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Rerank with {Reranker} timed out after {Seconds}s; keeping retrieval order", reranker.Name, options.TimeoutSeconds);
            return Truncate(fetched, request.TopK, [TimedOut]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rerank with {Reranker} failed; keeping retrieval order", reranker.Name);
            return Truncate(fetched, request.TopK, [Failed]);
        }

        // First score per chunk wins; scores for chunks that were not sent are ignored.
        var byChunk = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var score in scores)
        {
            byChunk.TryAdd(score.ChunkId, score.Score);
        }

        var warnings = new List<string>(1);
        var scored = candidates
            .Select((candidate, position) => (Candidate: candidate, Position: position, Has: byChunk.TryGetValue(candidate.Result.ChunkId, out var s), Score: s))
            .ToArray();
        if (scored.Any(x => !x.Has))
        {
            warnings.Add(Incomplete);
        }

        var ordered = scored.Where(x => x.Has).OrderByDescending(x => x.Score).ThenBy(x => x.Position)
            .Concat(scored.Where(x => !x.Has))                         // unscored candidates keep their relative order, after scored ones
            .Select((x, rank) => x.Has
                ? x.Candidate with
                {
                    Score = x.Score,
                    Sources = [.. x.Candidate.Sources, new SourceRank(Source, rank + 1, x.Score)]
                }
                : x.Candidate)
            .ToList();

        ordered.AddRange(fetched.Results.Skip(options.CandidateBudget));   // when TopK exceeds the budget, the tail stays in retrieval order
        return new RetrievalOutcome(ordered.Take(request.TopK).ToArray(), [.. fetched.Warnings, .. warnings]);
    }

    private static RetrievalOutcome Truncate(RetrievalOutcome outcome, int topK, IReadOnlyList<string> extraWarnings) =>
        new(outcome.Results.Take(topK).ToArray(), [.. outcome.Warnings, .. extraWarnings]);
}
