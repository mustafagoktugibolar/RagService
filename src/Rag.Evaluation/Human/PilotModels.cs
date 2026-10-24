namespace Rag.Evaluation.Human;

public static class PilotCategories
{
    public const string Keyword = "keyword";
    public const string Paraphrase = "paraphrase";
    public const string MultiRelevant = "multi-relevant";
    public const string Conceptual = "conceptual";
    public const string CrossLingual = "cross-lingual";
    public const string NoAnswer = "no-answer";

    /// <summary>Lower bounds that make the pilot's query set informative; the planned mix for 24 queries is 5/6/4/4/3/2.</summary>
    public static readonly IReadOnlyDictionary<string, int> MinimumPerCategory = new Dictionary<string, int>
    {
        [Keyword] = 4, [Paraphrase] = 5, [MultiRelevant] = 3, [Conceptual] = 3, [CrossLingual] = 2, [NoAnswer] = 2
    };
}

public static class AnnotatorKinds
{
    /// <summary>The only accepted kind. Relevance labels in this project are never produced by a model.</summary>
    public const string Human = "human";
}

public static class JudgmentFlags
{
    public const string UnclearQuery = "unclear-query";
    public const string Unjudgeable = "unjudgeable";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { UnclearQuery, Unjudgeable };
}

/// <summary>A person taking part in the pilot. <c>Kind</c> is an attestation that must be "human"; roles: query-writer, annotator, adjudicator.</summary>
public sealed record Annotator
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>A query written by a person, from a topic prompt, without seeing the passages.</summary>
public sealed record PilotQuery
{
    public string Id { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    /// <summary>"en" or "tr": the language of the query text.</summary>
    public string Language { get; init; } = string.Empty;

    public string WrittenBy { get; init; } = string.Empty;
}

/// <summary>A passage of the frozen corpus, exactly as it would be sent to a provider.</summary>
public sealed record PilotChunk
{
    public string ChunkId { get; init; } = string.Empty;

    public string DocumentId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}

public sealed record PoolSourceRank(string Source, int Rank);

public sealed record PoolItem(string ChunkId, IReadOnlyList<PoolSourceRank> Sources);

/// <summary>
/// The <b>unblinded</b> record of how each candidate entered the pool. Annotators never receive it; it is what lets the report evaluate
/// each pooling system afterwards. <c>Rankings</c> hold each system's own top list per query.
/// </summary>
public sealed record PoolKey
{
    public string CorpusVersion { get; init; } = string.Empty;

    public int Seed { get; init; }

    public IReadOnlyList<string> Systems { get; init; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<PoolItem>> Pools { get; init; } = new Dictionary<string, IReadOnlyList<PoolItem>>();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Rankings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
}

/// <summary>One person's grade for one (query, passage).</summary>
public sealed record GradedItem
{
    public string QueryId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    /// <summary>0..3 per docs/eval/annotation-guidelines.md.</summary>
    public int Grade { get; init; }

    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Self-reported or timer seconds spent on the item; used only to estimate effort.</summary>
    public double? Seconds { get; init; }

    public string? Comment { get; init; }
}

public sealed record AnnotatorJudgments
{
    public string AnnotatorId { get; init; } = string.Empty;

    public string GuidelinesVersion { get; init; } = string.Empty;

    public IReadOnlyList<GradedItem> Items { get; init; } = [];
}

/// <summary>A disagreement resolved by a person, with the reason recorded.</summary>
public sealed record Adjudication
{
    public string QueryId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    public int Grade { get; init; }

    public string AdjudicatorId { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;
}

public sealed record FinalJudgment(string QueryId, string ChunkId, int Grade, string Source);
