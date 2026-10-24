using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core.Options;
using Rag.Core.Retrieval;
using Rag.Core.Telemetry;

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
/// <summary>Raised when reranking fails and <see cref="RerankFailurePolicy.Fail"/> is configured. The message carries no query or passage text.</summary>
public sealed class RerankFailedException(string reranker, Exception inner)
    : Exception($"Reranking with '{reranker}' failed.", inner);

/// <summary>Reranker that keeps retrieval order: the second stage runs but changes nothing. Plumbing check and ablation baseline.</summary>
public sealed class NoOpReranker : IReranker
{
    public string Name => "none";

    public Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RerankScore>>(
            candidates.Select((c, i) => new RerankScore(c.ChunkId, candidates.Count - i)).ToArray());
}

public sealed class RerankingRetriever(
    IRetriever inner,
    IReranker reranker,
    RerankingOptions options,
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
                request.JudgedQuery,
                candidates.Select(c => new RerankCandidate(c.Result.ChunkId, c.Result.Text)).ToArray(),
                timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Rerank with {Reranker} timed out after {Seconds}s; {Policy}",
                reranker.Name, options.TimeoutSeconds, options.FailurePolicy == RerankFailurePolicy.Fail ? "failing the query" : "keeping retrieval order");
            RagTelemetry.RerankFallbacks.Add(1, new TagList { { "reason", "timeout" } });
            return options.FailurePolicy == RerankFailurePolicy.Fail
                ? throw new RerankFailedException(reranker.Name, new TimeoutException())
                : Truncate(fetched, request.TopK, [TimedOut]);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Rerank with {Reranker} failed ({ErrorType}); {Policy}",
                reranker.Name, ex.GetType().Name, options.FailurePolicy == RerankFailurePolicy.Fail ? "failing the query" : "keeping retrieval order");
            RagTelemetry.RerankFallbacks.Add(1, new TagList { { "reason", "failed" } });
            return options.FailurePolicy == RerankFailurePolicy.Fail
                ? throw new RerankFailedException(reranker.Name, ex)
                : Truncate(fetched, request.TopK, [Failed]);
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
            RagTelemetry.RerankFallbacks.Add(1, new TagList { { "reason", "incomplete" } });
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
