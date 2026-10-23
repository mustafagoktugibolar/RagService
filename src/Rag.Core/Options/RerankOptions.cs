namespace Rag.Core.Options;

public sealed class RerankOptions
{
    /// <summary>Off by default: reranking is enabled only after an evaluation shows it improves final relevance.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>"Onnx" (local cross-encoder, no network at runtime) is the only provider today; the abstraction admits others.</summary>
    public string Provider { get; set; } = "Onnx";

    /// <summary>How many top candidates from the inner retriever are sent to the reranker. Cost and latency scale with it.</summary>
    public int CandidateBudget { get; set; } = 20;

    /// <summary>Hard limit for one rerank call. On expiry the query falls back to the un-reranked order.</summary>
    public int TimeoutSeconds { get; set; } = 3;
}
