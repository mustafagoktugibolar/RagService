using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rag.Core.Telemetry;

public static class RagTelemetry
{
    public const string ServiceName = "Rag.Service";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    private static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> DocumentsIndexed =
        Meter.CreateCounter<long>("rag.documents.indexed", description: "Total documents successfully indexed");

    public static readonly Counter<long> IndexErrors =
        Meter.CreateCounter<long>("rag.index.errors", description: "Total indexing failures");

    public static readonly Counter<long> QueriesExecuted =
        Meter.CreateCounter<long>("rag.queries.executed", description: "Total queries executed");

    public static readonly Counter<long> QueryErrors =
        Meter.CreateCounter<long>("rag.query.errors", description: "Total query failures");

    public static readonly Histogram<double> EmbeddingDurationMs =
        Meter.CreateHistogram<double>("rag.embedding.duration", unit: "ms", description: "Embedding API call duration");

    public static readonly Counter<long> RerankRequests =
        Meter.CreateCounter<long>("rag.rerank.requests", description: "Rerank provider requests by provider and outcome");

    public static readonly Counter<long> RerankInputTokens =
        Meter.CreateCounter<long>("rag.rerank.input_tokens", description: "Input tokens billed by rerank providers");

    public static readonly Counter<long> RerankFallbacks =
        Meter.CreateCounter<long>("rag.rerank.fallbacks", description: "Queries served in retrieval order because reranking failed, timed out or was incomplete");

    public static readonly Histogram<double> RerankDurationMs =
        Meter.CreateHistogram<double>("rag.rerank.duration", unit: "ms", description: "Duration of one rerank provider request");

    public static readonly Counter<long> RewriteRequests =
        Meter.CreateCounter<long>("rag.rewrite.requests", description: "Query rewrite attempts by outcome (rewritten, unchanged, skipped, rejected, failed, timeout, cached)");

    public static readonly Histogram<double> RewriteDurationMs =
        Meter.CreateHistogram<double>("rag.rewrite.duration", unit: "ms", description: "Duration of a rewrite that reached the provider");
}
