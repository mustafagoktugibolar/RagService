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
}
