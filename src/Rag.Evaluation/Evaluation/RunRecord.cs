using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Evaluation;

/// <summary>
/// One evaluation run with every variable that can change the numbers. Regression comparison (Phase 8) only
/// compares runs that differ in the dimension under test, so all of these are captured explicitly.
/// </summary>
public sealed record RunRecord
{
    public required string Label { get; init; }

    public required string Strategy { get; init; }

    public required string Embedder { get; init; }

    /// <summary>"real" only for production embedding models; "synthetic" for the offline hashing embedder.</summary>
    public required string EmbedderKind { get; init; }

    public required HarnessConfiguration Chunking { get; init; }

    public required string DatasetVersion { get; init; }

    public string Reranker { get; init; } = "none";

    public string QueryTransformer { get; init; } = "none";

    /// <summary>The offline harness rebuilds its index per run, so there is no persistent index version.</summary>
    public string IndexVersion { get; init; } = "in-memory-per-run";

    public string? GitCommit { get; init; }

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public required EvaluationReport Report { get; init; }
}
