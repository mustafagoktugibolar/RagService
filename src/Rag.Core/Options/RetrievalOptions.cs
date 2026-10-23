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
    /// <summary>Default is Dense so enabling the new code path is an explicit, evaluated decision.</summary>
    public RetrievalStrategy Strategy { get; set; } = RetrievalStrategy.Dense;

    /// <summary>How many candidates each leg fetches before fusion. Must be at least the requested TopK to be useful.</summary>
    public int CandidateK { get; set; } = 50;

    /// <summary>RRF constant k.</summary>
    public int RrfK { get; set; } = 60;

    public double DenseWeight { get; set; } = 1.0;

    public double LexicalWeight { get; set; } = 1.0;
}
