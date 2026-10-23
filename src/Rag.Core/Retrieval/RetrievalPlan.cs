using System.Globalization;
using System.Text.RegularExpressions;
using Rag.Core.Options;

namespace Rag.Core.Retrieval;

/// <summary>
/// The strategy a query will really use after checking what the store can do. <see cref="StrategyId"/> identifies the
/// effective retrieval behaviour (including fusion parameters) and is part of the query-cache fingerprint, so a
/// fallback to dense shares cache entries with plain dense and a tuned hybrid never shares with a different tuning.
/// </summary>
public sealed record RetrievalPlan(
    RetrievalStrategy Strategy,
    string StrategyId,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public const string HybridFellBackToDense = "hybrid-unsupported:fell-back-to-dense";

    public static RetrievalPlan Resolve(RetrievalOptions options, RetrievalCapabilities capabilities)
    {
        var lexical = capabilities.HasFlag(RetrievalCapabilities.Lexical);
        switch (options.Strategy)
        {
            case RetrievalStrategy.Dense:
                return Dense([]);

            case RetrievalStrategy.Lexical when lexical:
                return new RetrievalPlan(RetrievalStrategy.Lexical, "lexical", [], null);

            case RetrievalStrategy.Lexical:
                return new RetrievalPlan(RetrievalStrategy.Lexical, "lexical", [],
                    "Retrieval strategy 'Lexical' is not supported by the configured vector store.");

            case RetrievalStrategy.Hybrid when !lexical:
                return Dense([HybridFellBackToDense]);

            case RetrievalStrategy.Hybrid:
                var id = string.Create(CultureInfo.InvariantCulture,
                    $"hybrid:rrf{options.RrfK}:cand{options.CandidateK}:w{options.DenseWeight:R}/{options.LexicalWeight:R}");
                return new RetrievalPlan(RetrievalStrategy.Hybrid, id, [], null);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Strategy, "Unknown retrieval strategy.");
        }
    }

    private static RetrievalPlan Dense(IReadOnlyList<string> warnings) =>
        new(RetrievalStrategy.Dense, "dense", warnings, null);
}

/// <summary>
/// Metadata filters reach the store as payload field paths and column lookups. Keys are therefore restricted to a flat
/// identifier alphabet (no dots, which would address nested payload fields) and sizes are bounded.
/// </summary>
public static partial class FilterValidator
{
    public const int MaxFilters = 16;
    public const int MaxValueLength = 256;

    public static void Validate(IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null)
        {
            return;
        }

        if (filters.Count > MaxFilters)
        {
            throw new InvalidOperationException($"At most {MaxFilters} metadata filters are allowed.");
        }

        foreach (var (key, value) in filters)
        {
            if (!KeyPattern().IsMatch(key))
            {
                throw new InvalidOperationException(
                    $"Invalid metadata filter key '{key}'. Keys may contain letters, digits, '_' and '-' (1-64 characters).");
            }

            if (value is null || value.Length > MaxValueLength)
            {
                throw new InvalidOperationException($"Metadata filter '{key}' must have a value of at most {MaxValueLength} characters.");
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex KeyPattern();
}
