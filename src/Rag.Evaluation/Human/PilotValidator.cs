using System.Globalization;
using System.Text;

namespace Rag.Evaluation.Human;

public sealed class PilotValidationException(IReadOnlyList<string> errors)
    : Exception($"Pilot data is invalid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Structural checks on the pilot's human-provided inputs. Collects every problem rather than stopping at the first.</summary>
public static class PilotValidator
{
    public const int MinimumQueries = 20;
    public const int MaximumQueries = 30;
    public const int Grades = 4;

    private static readonly string[] AllowedRoles = ["query-writer", "annotator", "adjudicator"];

    public static void ValidateAnnotators(IReadOnlyList<Annotator> annotators, bool allowSingleAnnotator = false)
    {
        var errors = new List<string>();
        if (annotators.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != annotators.Count || annotators.Any(a => string.IsNullOrWhiteSpace(a.Id)))
        {
            errors.Add("Annotator ids must be non-empty and unique.");
        }

        foreach (var annotator in annotators)
        {
            if (!string.Equals(annotator.Kind, AnnotatorKinds.Human, StringComparison.Ordinal))
            {
                errors.Add($"Annotator '{annotator.Id}' has kind '{annotator.Kind}'; relevance labels must come from a human (kind 'human').");
            }

            if (annotator.Roles.Count == 0 || annotator.Roles.Any(r => !AllowedRoles.Contains(r)))
            {
                errors.Add($"Annotator '{annotator.Id}' needs roles from: {string.Join(", ", AllowedRoles)}.");
            }
        }

        var graders = annotators.Count(a => a.Roles.Contains("annotator"));
        if (graders < 2 && !allowSingleAnnotator)
        {
            errors.Add("At least two annotators are required for inter-annotator agreement (or explicitly choose the documented test-retest fallback).");
        }

        Throw(errors);
    }

    public static void ValidateQueries(IReadOnlyList<PilotQuery> queries, IReadOnlyList<Annotator> annotators)
    {
        var errors = new List<string>();
        if (queries.Count < MinimumQueries || queries.Count > MaximumQueries)
        {
            errors.Add($"The pilot needs {MinimumQueries}-{MaximumQueries} queries (found {queries.Count}).");
        }

        if (queries.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != queries.Count)
        {
            errors.Add("Query ids must be unique.");
        }

        var normalised = queries.Select(q => SplitIsolation.Normalise(q.Text)).ToList();
        if (normalised.Distinct(StringComparer.Ordinal).Count() != normalised.Count)
        {
            errors.Add("Two queries have the same text after normalisation.");
        }

        var writers = annotators.Where(a => a.Roles.Contains("query-writer")).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            if (!query.Id.StartsWith("pilot-", StringComparison.Ordinal))
            {
                errors.Add($"Query '{query.Id}': pilot query ids must start with 'pilot-' so they can never be mistaken for final-set queries.");
            }

            if (!PilotCategories.MinimumPerCategory.ContainsKey(query.Category))
            {
                errors.Add($"Query '{query.Id}': unknown category '{query.Category}'.");
            }

            if (query.Language is not ("en" or "tr"))
            {
                errors.Add($"Query '{query.Id}': language must be 'en' or 'tr'.");
            }

            if (Csv.StartsLikeFormula(query.Text))
            {
                errors.Add($"Query '{query.Id}': text must not start with =, +, -, @ or a control character (spreadsheet formula injection); rephrase it.");
            }

            if (query.Text.Trim().Length is < 8 or > 300)
            {
                errors.Add($"Query '{query.Id}': text must be 8-300 characters.");
            }

            if (!writers.Contains(query.WrittenBy))
            {
                errors.Add($"Query '{query.Id}': writtenBy '{query.WrittenBy}' is not a registered human query-writer.");
            }
        }

        foreach (var (category, minimum) in PilotCategories.MinimumPerCategory)
        {
            var count = queries.Count(q => q.Category == category);
            if (count < minimum)
            {
                errors.Add($"Category '{category}' needs at least {minimum} queries (found {count}).");
            }
        }

        Throw(errors);
    }

    /// <summary>Returns the fraction of pooled items this annotator graded.</summary>
    public static double ValidateJudgments(AnnotatorJudgments judgments, PoolKey pool)
    {
        var errors = new List<string>();
        var pooled = pool.Pools.SelectMany(p => p.Value.Select(i => (p.Key, i.ChunkId))).ToHashSet();
        var seen = new HashSet<(string, string)>();

        foreach (var item in judgments.Items)
        {
            if (!pooled.Contains((item.QueryId, item.ChunkId)))
            {
                errors.Add($"Judgment for ({item.QueryId}, {item.ChunkId}) is not in the pool.");
            }

            if (!seen.Add((item.QueryId, item.ChunkId)))
            {
                errors.Add($"Duplicate judgment for ({item.QueryId}, {item.ChunkId}).");
            }

            if (item.Grade is < 0 or >= Grades)
            {
                errors.Add($"Judgment ({item.QueryId}, {item.ChunkId}): grade {item.Grade} is outside 0..{Grades - 1}.");
            }

            if (item.Flags.Any(f => !JudgmentFlags.Allowed.Contains(f)))
            {
                errors.Add($"Judgment ({item.QueryId}, {item.ChunkId}): unknown flag.");
            }
        }

        Throw(errors);
        return pooled.Count == 0 ? 0 : seen.Count / (double)pooled.Count;
    }

    private static void Throw(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new PilotValidationException(errors);
        }
    }
}

/// <summary>
/// Keeps the pilot apart from the final held-out set. A query that appears (exactly, or nearly) in another set would let pilot-time guideline
/// tuning leak into the final evaluation, so overlaps are reported and treated as errors.
/// </summary>
public static class SplitIsolation
{
    public sealed record Overlap(string PilotQueryId, string OtherQueryId, double Similarity);

    /// <summary>Lowercase, accents removed, punctuation dropped, whitespace collapsed.</summary>
    public static string Normalise(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static IReadOnlyList<Overlap> FindOverlaps(
        IEnumerable<(string Id, string Text)> pilot, IEnumerable<(string Id, string Text)> others, double threshold = 0.8)
    {
        var otherTokens = others.Select(o => (o.Id, Tokens: Tokens(o.Text))).ToList();
        var overlaps = new List<Overlap>();
        foreach (var (id, text) in pilot)
        {
            var tokens = Tokens(text);
            foreach (var (otherId, other) in otherTokens)
            {
                var similarity = Jaccard(tokens, other);
                if (similarity >= threshold)
                {
                    overlaps.Add(new Overlap(id, otherId, similarity));
                }
            }
        }

        return overlaps;
    }

    private static HashSet<string> Tokens(string text) => Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }
}
