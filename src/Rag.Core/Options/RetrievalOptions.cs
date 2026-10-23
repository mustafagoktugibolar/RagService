namespace Rag.Core.Options;

public enum RetrievalStrategy
{
    /// <summary>Embedding similarity only (the behaviour before hybrid search existed).</summary>
    Dense,

    /// <summary>Lexical (BM25-style) only. Requires a store that supports lexical search.</summary>
    Lexical,

    /// <summary>Dense + lexical merged with reciprocal rank fusion. Falls back to dense if the store has no lexical search.</summary>
    Hybrid
}

public sealed class RetrievalOptions
{
    /// <summary>Default is Dense so enabling any other code path is an explicit, evaluated decision.</summary>
    public RetrievalStrategy Strategy { get; set; } = RetrievalStrategy.Dense;

    /// <summary>How many candidates each hybrid leg fetches before fusion. Must be at least the requested TopK to be useful.</summary>
    public int CandidateK { get; set; } = 50;

    /// <summary>RRF constant k.</summary>
    public int RrfK { get; set; } = 60;

    public double DenseWeight { get; set; } = 1.0;

    public double LexicalWeight { get; set; } = 1.0;

    /// <summary>Optional second stage that reorders the candidates of whichever strategy is selected.</summary>
    public RerankingOptions Reranking { get; set; } = new();
}

public enum RerankProviderKind
{
    /// <summary>No reordering: the stage runs but keeps retrieval order. Useful to measure plumbing and as an ablation.</summary>
    None,

    /// <summary>OpenAI Decisions API (<c>POST /v1/decisions</c>), authenticated with the existing <c>OpenAI:ApiKey</c>.</summary>
    OpenAiDecisions,

    /// <summary>TypeSafe Jev (<c>POST /v1/systemone</c>), authenticated with its own <c>Jev:ApiKey</c>.</summary>
    Jev,

    /// <summary>Local ONNX cross-encoder from the optional Rag.Reranking.Onnx project.</summary>
    Onnx
}

public enum RerankFailurePolicy
{
    /// <summary>On provider failure/timeout keep the un-reranked order and report a warning. Default: reranking is an enhancement.</summary>
    Fallback,

    /// <summary>On provider failure the query fails. For deployments where unreranked results are not acceptable.</summary>
    Fail
}

public enum RelevanceMechanism
{
    /// <summary>Ordered relevance levels; the provider returns a probability-weighted expected level. Primary mechanism.</summary>
    Score,

    /// <summary>Single yes/no relevance question; the provider returns P(relevant). Exploratory until evaluated.</summary>
    Predicate
}

public sealed class RerankingOptions
{
    /// <summary>Off by default: reranking is enabled only after an evaluation shows it improves final relevance.</summary>
    public bool Enabled { get; set; } = false;

    public RerankProviderKind Provider { get; set; } = RerankProviderKind.None;

    /// <summary>How many top candidates from the retrieval strategy are scored. Cost and latency scale with it.</summary>
    public int CandidateBudget { get; set; } = 20;

    /// <summary>Overall limit for one rerank call (all attempts and batches). On expiry the failure policy applies.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    public RerankFailurePolicy FailurePolicy { get; set; } = RerankFailurePolicy.Fallback;

    public RelevanceMechanism Mechanism { get; set; } = RelevanceMechanism.Score;

    /// <summary>
    /// Questions per provider request. The candidate text travels inside each question, so questions are independent of one
    /// another; 1 means strictly one request per candidate (the isolation control), larger values batch for cost and latency.
    /// </summary>
    public int QuestionsPerRequest { get; set; } = 20;

    /// <summary>Provider requests in flight across the whole process (bounds load and rate-limit pressure).</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Passages longer than this are truncated before being sent; bounds tokens and cost per candidate.</summary>
    public int MaxPassageCharacters { get; set; } = 6000;
}

public sealed class OpenAiDecisionsOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com";

    /// <summary>The Decisions guide lists gpt-6-luna as the only supported model.</summary>
    public string Model { get; set; } = "gpt-6-luna";
}

public sealed class JevOptions
{
    /// <summary>Separate credential from the TypeSafe console. The OpenAI key does not work here.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.typesafe.ai";

    /// <summary>"jev-latest" is an alias that can change underneath; pin a version (e.g. jev-1.13.0) for production.</summary>
    public string Model { get; set; } = "jev-latest";
}
