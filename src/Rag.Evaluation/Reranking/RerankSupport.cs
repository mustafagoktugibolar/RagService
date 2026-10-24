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

    public static double Dollars(double inputTokens, double pricePerMillion) => inputTokens / 1_000_000.0 * pricePerMillion;
}

public sealed class SpendLimitExceededException(string message) : Exception(message);

/// <summary>Money set aside for one in-flight request. It is replaced by the billed amount once the request finishes.</summary>
public sealed record SpendReservation(int EstimatedInputTokens, double ReservedDollars);

/// <summary>
/// A best-effort <b>stop-loss</b> for a paid run. It is <b>not an absolute billing cap</b>: the provider's own budget or usage limit
/// (set in its console) is the only real ceiling. What it does:
/// <list type="bullet">
/// <item>Every request reserves its estimated cost <b>before</b> it is sent, so concurrent requests cannot jointly overshoot.</item>
/// <item>When a request finishes (success or failure) the reservation is replaced by what the provider actually billed.</item>
/// <item>It learns the observed/estimated token ratio and scales later reservations by it (never below 1), so an under-estimating
/// estimator is corrected after one request instead of staying wrong for the whole run.</item>
/// <item>Until that first observation the estimate is multiplied by <c>initialSafetyFactor</c> (default 2).</item>
/// </list>
/// What it cannot prevent (the residual risk, stated plainly): the first request is reserved at <c>estimate × initialSafetyFactor</c>, so
/// real spend can pass the approved amount if the estimator is wrong by <b>more than that factor</b> on the first request (the one
/// observed OpenAI run was 1.61× under the original estimate); if the provider bills in ways it does not report (rounding, tokens not
/// in <c>usage</c>) the guard cannot see it; the estimator for a provider never run is unverified (Jev). Approve amounts with headroom
/// and set the provider-side limit.
/// </summary>
public sealed class SpendGuard(int maxRequests, double maxDollars, double pricePerMillionInputTokens, double initialSafetyFactor = 2.0)
{
    private readonly object _lock = new();
    private int _requests;
    private double _dollars;
    private double _estimatedTokens;
    private double _observedTokens;

    public int Requests => _requests;

    /// <summary>Billed plus reserved-for-in-flight, per the guard's own accounting. A best-effort figure, not an invoice.</summary>
    public double EstimatedDollars => _dollars;

    /// <summary>
    /// The multiplier currently applied to estimates: the initial safety factor until a request has finished, afterwards the observed ÷ estimated
    /// input-token ratio (never below 1).
    /// </summary>
    public double CalibrationFactor
    {
        get
        {
            lock (_lock)
            {
                return Factor();
            }
        }
    }

    private double Factor() => _estimatedTokens > 0 ? Math.Max(1.0, _observedTokens / _estimatedTokens) : Math.Max(1.0, initialSafetyFactor);

    public SpendReservation Authorise(int estimatedInputTokens)
    {
        lock (_lock)
        {
            var factor = Factor();
            var cost = CostModel.Dollars(estimatedInputTokens * factor, pricePerMillionInputTokens);
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
            return new SpendReservation(estimatedInputTokens, cost);
        }
    }

    /// <summary>Replaces the reservation by the billed amount and updates the calibration factor.</summary>
    public void Reconcile(SpendReservation reservation, long observedInputTokens)
    {
        lock (_lock)
        {
            _dollars = Math.Max(0, _dollars - reservation.ReservedDollars + CostModel.Dollars(observedInputTokens, pricePerMillionInputTokens));
            _estimatedTokens += reservation.EstimatedInputTokens;
            _observedTokens += observedInputTokens;
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
        var reservation = guard.Authorise(estimate);
        var before = (inner as IRerankUsageSource)?.Usage.InputTokens;
        try
        {
            return await inner.RerankAsync(query, candidates, ct);
        }
        finally
        {
            // Billed tokens replace the reservation even when the call failed. A provider without a usage source keeps the reservation as is.
            if (before is { } start && inner is IRerankUsageSource source)
            {
                guard.Reconcile(reservation, source.Usage.InputTokens - start);
            }
        }
    }
}
