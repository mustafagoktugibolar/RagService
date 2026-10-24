using System.Security.Cryptography;
using System.Text;
using Rag.Core.Reranking;

namespace Rag.Evaluation.Reranking;

/// <summary>
/// Persists reranker scores per (provider identity, query, passage text). A repeated run costs nothing, and the same pair
/// scored for two pipelines (dense and hybrid share most candidates) gets one score, so a comparison between pipelines is
/// not confounded by provider noise. Only uncached pairs reach the provider. Replays are not provider requests: usage
/// reflects live calls only. The directory must not be committed.
/// </summary>
public sealed class FileCachingReranker(IReranker inner, string directory) : IReranker, IRerankUsageSource
{
    public int CacheHits { get; private set; }

    public int CacheMisses { get; private set; }

    public string Name => inner.Name;

    public RerankUsage Usage => (inner as IRerankUsageSource)?.Usage
        ?? new RerankUsage(0, 0, 0, 0, 0, 0, [], null);

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
    {
        var folder = FolderFor(directory, inner.Name);
        Directory.CreateDirectory(folder);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var missing = new List<RerankCandidate>();
        foreach (var candidate in candidates)
        {
            var path = PathFor(folder, query, candidate.Text);
            if (File.Exists(path) && double.TryParse(await File.ReadAllTextAsync(path, ct), System.Globalization.CultureInfo.InvariantCulture, out var cached))
            {
                scores[candidate.ChunkId] = cached;
                CacheHits++;
            }
            else
            {
                missing.Add(candidate);
            }
        }

        if (missing.Count > 0)
        {
            foreach (var score in await inner.RerankAsync(query, missing, ct))
            {
                scores[score.ChunkId] = score.Score;
                var text = missing.First(m => m.ChunkId == score.ChunkId).Text;
                await File.WriteAllTextAsync(PathFor(folder, query, text), score.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture), ct);
            }

            CacheMisses += missing.Count;
        }

        return candidates.Where(c => scores.ContainsKey(c.ChunkId)).Select(c => new RerankScore(c.ChunkId, scores[c.ChunkId])).ToArray();
    }

    /// <summary>Whether a pair is already scored, so a plan can count what a run would really send.</summary>
    public static bool IsCached(string directory, string rerankerName, string query, string text) =>
        File.Exists(PathFor(FolderFor(directory, rerankerName), query, text));

    private static string FolderFor(string directory, string rerankerName) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rerankerName)))[..16]);

    private static string PathFor(string folder, string query, string text) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}\n{text}"))) + ".txt");
}

/// <summary>Price and token estimates used to plan and bound paid runs. Prices come from provider documentation; verify before spending.</summary>
public static class CostModel
{
    /// <summary>OpenAI Decisions guide: $0.10 per 1M uncached input tokens, output not charged.</summary>
    public const double OpenAiDecisionsPerMillionInput = 0.10;

    /// <summary>TypeSafe announcement: $0.042 per 1M input tokens, output free. A company claim, not independently confirmed.</summary>
    public const double JevPerMillionInput = 0.042;

    /// <summary>
    /// Rubric levels, fence, instruction preamble and JSON framing repeated in every question. Calibrated from the one observed OpenAI run
    /// (383 input tokens per question vs 237 estimated with the earlier value of 150, i.e. 61 % under). Other tokenizers (Jev) are unverified.
    /// </summary>
    public const int RubricTokensPerQuestion = 300;

    /// <summary>~4 characters per token for English prose. A rough planning figure; observed usage replaces it in reports.</summary>
    public static int EstimateInputTokens(string query, IEnumerable<string> passages) =>
        (query.Length + 20) / 4 + passages.Sum(p => p.Length / 4 + RubricTokensPerQuestion);

    public static double Dollars(long inputTokens, double pricePerMillion) => inputTokens / 1_000_000.0 * pricePerMillion;
}

public sealed class SpendLimitExceededException(string message) : Exception(message);

/// <summary>Stop-loss for a paid run: refuses the next request that would exceed the approved request count or dollar amount.</summary>
public sealed class SpendGuard(int maxRequests, double maxDollars, double pricePerMillionInputTokens)
{
    private readonly object _lock = new();
    private int _requests;
    private double _dollars;

    public int Requests => _requests;

    public double EstimatedDollars => _dollars;

    public void Authorise(int estimatedInputTokens)
    {
        lock (_lock)
        {
            var cost = CostModel.Dollars(estimatedInputTokens, pricePerMillionInputTokens);
            if (_requests + 1 > maxRequests)
            {
                throw new SpendLimitExceededException($"Approved request limit of {maxRequests} reached; stopping before any further paid call.");
            }

            if (_dollars + cost > maxDollars)
            {
                throw new SpendLimitExceededException($"Approved spend limit of ${maxDollars:0.######} would be exceeded; stopping before any further paid call.");
            }

            _requests++;
            _dollars += cost;
        }
    }

    /// <summary>
    /// Replaces the estimate of a finished request by what the provider actually billed. Without this the guard bounds only its own
    /// guess: an under-estimate (it happened: 61 % low) would let real spend pass the approved limit.
    /// </summary>
    public void Reconcile(int estimatedInputTokens, long observedInputTokens)
    {
        lock (_lock)
        {
            _dollars = Math.Max(0, _dollars + CostModel.Dollars(observedInputTokens - estimatedInputTokens, pricePerMillionInputTokens));
        }
    }
}

/// <summary>Puts a <see cref="SpendGuard"/> in front of a paid provider: every request is authorised against the approved budget first.</summary>
public sealed class SpendGuardedReranker(IReranker inner, SpendGuard guard) : IReranker, IRerankUsageSource
{
    public string Name => inner.Name;

    public RerankUsage Usage => (inner as IRerankUsageSource)?.Usage ?? new RerankUsage(0, 0, 0, 0, 0, 0, [], null);

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
    {
        var estimate = CostModel.EstimateInputTokens(query, candidates.Select(c => c.Text));
        guard.Authorise(estimate);
        var before = (inner as IRerankUsageSource)?.Usage.InputTokens;
        try
        {
            return await inner.RerankAsync(query, candidates, ct);
        }
        finally
        {
            if (before is { } start && inner is IRerankUsageSource source)
            {
                guard.Reconcile(estimate, source.Usage.InputTokens - start);   // billed tokens replace the guess, even when the call failed
            }
        }
    }
}
