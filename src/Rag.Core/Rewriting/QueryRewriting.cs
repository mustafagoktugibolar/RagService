using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Telemetry;

namespace Rag.Core.Rewriting;

/// <summary>
/// A source of candidate rewrites. It only <i>proposes</i>: validation, fallback, timeout, caching and telemetry live in
/// <see cref="QueryRewritingStage"/>, so a new provider (another LLM, a local model, a rule set) implements one method and inherits all of it.
/// </summary>
public interface IQueryRewriter
{
    /// <summary>Provider, model and prompt/rules version. Part of the query-cache key: a different rewriter must never share cached results.</summary>
    string Name { get; }

    Task<string?> ProposeAsync(string query, CancellationToken ct);
}

public enum RewriteOutcome
{
    /// <summary>The rewriter's proposal passed validation and differs from the original.</summary>
    Rewritten,

    /// <summary>The proposal (or cached value) equals the original after normalisation.</summary>
    Unchanged,

    /// <summary>Too short to be worth a call; searched as written.</summary>
    Skipped,

    /// <summary>The proposal failed validation; the original is used.</summary>
    Rejected,

    /// <summary>The rewriter threw; the original is used.</summary>
    Failed,

    /// <summary>The rewrite exceeded its time limit; the original is used.</summary>
    TimedOut
}

/// <param name="Original">What the user sent. Always preserved: reranking judges relevance to it and the result cache is keyed on it.</param>
/// <param name="Effective">What is searched: the validated rewrite, or the original on any fallback.</param>
/// <param name="Warning">Set only for a degraded outcome (rejected, failed, timed out); such results are not cached.</param>
/// <param name="Reason">A non-sensitive code explaining a rejection.</param>
public sealed record QueryTransformation(string Original, string Effective, RewriteOutcome Outcome, string? Warning, string? Reason, bool FromCache = false);

public sealed record RewriteValidation(bool Accepted, string Text, string? Reason);

/// <summary>
/// Decides whether a proposed rewrite may replace the user's query. The checks are deliberately mechanical and conservative: a rewrite that
/// loses a number, a quoted phrase or an identifier, drifts away from the original's words, answers instead of searching, or is not a single
/// plain line is rejected and the original is used.
/// </summary>
public static partial class RewriteValidator
{
    public const string Empty = "empty";
    public const string MultiLine = "multi-line";
    public const string TooLong = "too-long";
    public const string Refusal = "refusal";
    public const string DroppedProtectedToken = "dropped-protected-token";
    public const string LowOverlap = "low-overlap";

    private static readonly string[] RefusalStarts =
        ["i'm sorry", "i am sorry", "sorry,", "sorry.", "as an ai", "i cannot", "i can't", "i can not", "i'm unable", "üzgünüm", "maalesef"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "what", "when", "which", "where", "how", "does", "did", "are", "was", "were",
        "can", "could", "would", "should", "about", "have", "has", "had", "your", "their", "there", "than", "then", "also", "ile", "için", "gibi", "nasıl", "neden", "nedir", "hangi", "olan", "veya"
    };

    public static RewriteValidation Validate(string original, string? candidate, QueryRewritingOptions options)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Reject(Empty);
        }

        var text = Unwrap(RelevanceRubric.Sanitise(candidate).Trim());
        if (text.Length == 0)
        {
            return Reject(Empty);
        }

        if (text.Contains('\n') || text.Contains('\t'))
        {
            return Reject(MultiLine);
        }

        if (text.Length > Math.Min(options.MaxCharacters, Math.Max(80, original.Length * 3)))
        {
            return Reject(TooLong);
        }

        if (RefusalStarts.Any(r => text.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(Refusal);
        }

        if (ProtectedTokens(original).Any(token => !text.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(DroppedProtectedToken);
        }

        var words = ContentWords(original);
        if (words.Count >= 2)
        {
            var kept = words.Count(w => text.Contains(w.Length > 5 ? w[..5] : w, StringComparison.OrdinalIgnoreCase));
            if (kept / (double)words.Count < options.MinimumOverlap)
            {
                return Reject(LowOverlap);
            }
        }

        return new RewriteValidation(true, text, null);
    }

    /// <summary>Quoted phrases, numbers, identifiers (digits, underscores, inner hyphens/dots/colons, camelCase) and acronyms: things a rewrite must not lose.</summary>
    public static IReadOnlyList<string> ProtectedTokens(string query)
    {
        var tokens = new List<string>();
        foreach (Match m in QuotedPhrase().Matches(query))
        {
            tokens.Add(m.Groups[1].Value.Trim());
        }

        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ',', ';', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '“', '”', '‘', '’', ':');
            if (token.Length == 0 || token.Contains(' '))
            {
                continue;
            }

            var hasDigit = token.Any(char.IsDigit);
            var innerSeparator = InnerSeparator().IsMatch(token);
            var camel = CamelCase().IsMatch(token);
            var acronym = token.Length >= 2 && token.All(c => char.IsUpper(c) || char.IsDigit(c)) && token.Any(char.IsLetter);
            if (hasDigit || innerSeparator || camel || acronym)
            {
                tokens.Add(token);
            }
        }

        return tokens.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ContentWords(string query) =>
        WordPattern().Matches(query).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4 && !StopWords.Contains(w)).Distinct().ToList();

    // LLMs often wrap the whole answer in quotes or backticks.
    private static string Unwrap(string text)
    {
        while (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '`' && text[^1] == '`') || (text[0] == '\'' && text[^1] == '\'') || (text[0] == '“' && text[^1] == '”')))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private static RewriteValidation Reject(string reason) => new(false, string.Empty, reason);

    [GeneratedRegex("[\"“]([^\"”]+)[\"”]")]
    private static partial Regex QuotedPhrase();

    [GeneratedRegex(@"[\p{L}\p{N}][-_.:/][\p{L}\p{N}]|_")]
    private static partial Regex InnerSeparator();

    [GeneratedRegex(@"\p{Ll}\p{Lu}")]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}

/// <summary>
/// The optional first stage of retrieval. Applies, in order: skip very short queries, serve a validated cached rewrite, ask the rewriter under a
/// time limit, validate what it proposes, and on <b>any</b> problem fall back to the original query with a warning. It never throws for a
/// rewriter failure; only the caller's cancellation propagates.
/// </summary>
public sealed class QueryRewritingStage(
    IQueryRewriter rewriter,
    IDistributedCache cache,
    QueryRewritingOptions options,
    ILogger logger)
{
    public const string SchemaVersion = "1";
    public const string RejectedWarning = "rewrite:rejected";
    public const string FailedWarning = "rewrite:failed";
    public const string TimeoutWarning = "rewrite:timeout";

    public string Name => rewriter.Name;

    public async Task<QueryTransformation> TransformAsync(string query, CancellationToken ct)
    {
        if (WordCount(query) < options.MinimumWords)
        {
            Count("skipped");
            return new QueryTransformation(query, query, RewriteOutcome.Skipped, null, null);
        }

        var key = CacheKey(query);
        if (options.CacheTtlHours > 0 && await TryGetCachedAsync(key, ct) is { } cached)
        {
            var check = RewriteValidator.Validate(query, cached, options);
            if (check.Accepted)   // a cached value is validated again: the cache is not trusted more than the provider
            {
                Count("cached");
                return Finish(query, check.Text, fromCache: true);
            }
        }

        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        string? proposal;
        try
        {
            proposal = await rewriter.ProposeAsync(query, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Query rewrite with {Rewriter} timed out after {Seconds}s; using the original query", rewriter.Name, options.TimeoutSeconds);
            Count("timeout");
            return new QueryTransformation(query, query, RewriteOutcome.TimedOut, TimeoutWarning, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Query rewrite with {Rewriter} failed ({ErrorType}); using the original query", rewriter.Name, ex.GetType().Name);
            Count("failed");
            return new QueryTransformation(query, query, RewriteOutcome.Failed, FailedWarning, null);
        }
        finally
        {
            RagTelemetry.RewriteDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var validation = RewriteValidator.Validate(query, proposal, options);
        if (!validation.Accepted)
        {
            logger.LogWarning("Query rewrite from {Rewriter} rejected ({Reason}); using the original query", rewriter.Name, validation.Reason);
            Count("rejected");
            return new QueryTransformation(query, query, RewriteOutcome.Rejected, RejectedWarning, validation.Reason);
        }

        if (options.CacheTtlHours > 0)
        {
            await TrySetCachedAsync(key, validation.Text, ct);   // only validated rewrites are stored; rejections are not
        }

        return Finish(query, validation.Text, fromCache: false);
    }

    private QueryTransformation Finish(string original, string rewritten, bool fromCache)
    {
        var unchanged = Normalise(original) == Normalise(rewritten);
        if (!fromCache)
        {
            Count(unchanged ? "unchanged" : "rewritten");
        }

        return new QueryTransformation(original, unchanged ? original : rewritten, unchanged ? RewriteOutcome.Unchanged : RewriteOutcome.Rewritten, null, null, fromCache);
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static string Normalise(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>A stable, non-reversible identifier for a query, safe to put on a trace span.</summary>
    public static string Fingerprint(string query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..12];

    private string CacheKey(string query) =>
        $"rag:rewrite:v{SchemaVersion}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rewriter.Name)))[..12]}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)))}";

    private async Task<string?> TryGetCachedAsync(string key, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Rewrite cache read failed ({ErrorType}); continuing without it", ex.GetType().Name);
            return null;   // a cache outage must not break a query
        }
    }

    private async Task TrySetCachedAsync(string key, string text, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(key, Encoding.UTF8.GetBytes(text),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(options.CacheTtlHours) }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Rewrite cache write failed ({ErrorType}); continuing without it", ex.GetType().Name);
        }
    }

    private static void Count(string outcome) =>
        RagTelemetry.RewriteRequests.Add(1, new TagList { { "outcome", outcome } });
}
