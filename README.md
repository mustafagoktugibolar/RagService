# RagService

A production-ready Retrieval-Augmented Generation (RAG) backend built with .NET 10. Handles document ingestion, vector embedding, and semantic search via a message-driven worker architecture.

## Architecture

### Index Flow

```mermaid
sequenceDiagram
    participant S as WikipediaSeeder
    participant MQ as RabbitMQ<br/>rag.index
    participant IW as IndexWorker
    participant MS as MinIO<br/>(rag-documents)
    participant OAI as OpenAI<br/>text-embedding-3-small
    participant QD as Qdrant<br/>(vector store)
    participant DLQ as RabbitMQ<br/>rag.index.dlq

    S->>MS: Upload passage as .txt<br/>(passages/{id}.txt)
    S->>MQ: Publish IndexDocumentRequest<br/>{ documentId, filePath, collection }

    MQ-->>IW: Deliver message (prefetch=32, concurrency=32)

    IW->>MS: OpenReadAsync(filePath)
    MS-->>IW: Stream content

    IW->>IW: Extract text → chunk<br/>(chunk size 1000, overlap 200)

    IW->>OAI: EmbedBatchAsync(chunks[])<br/>POST /v1/embeddings
    OAI-->>IW: float[1536][] embeddings

    IW->>QD: EnsureCollection + UpsertAsync<br/>gRPC :6334
    QD-->>IW: OK

    alt Success
        IW->>MQ: BasicAck
    else Failure (after Polly retries)
        IW->>MQ: BasicNack (requeue=false)
        MQ->>DLQ: Route via rag.dlx exchange
    end
```

### Query Flow

```mermaid
sequenceDiagram
    participant GW as API Gateway<br/>(external .NET app)
    participant MQ as RabbitMQ<br/>rag.query
    participant QW as QueryWorker
    participant OAI as OpenAI<br/>text-embedding-3-small
    participant QD as Qdrant<br/>(vector store)
    participant DLQ as RabbitMQ<br/>rag.query.dlq

    GW->>MQ: Publish QueryRequest<br/>{ query, collection, topK }

    MQ-->>QW: Deliver message

    QW->>OAI: EmbedAsync(query)<br/>POST /v1/embeddings
    OAI-->>QW: float[1536] embedding

    QW->>QD: SearchAsync(embedding, topK)<br/>gRPC :6334
    QD-->>QW: VectorSearchResult[]

    alt Success
        QW->>MQ: BasicAck
    else Failure (after Polly retries)
        QW->>MQ: BasicNack (requeue=false)
        MQ->>DLQ: Route via rag.dlx exchange
    end
```

### Dev/Test Flow (Rag.Api)

```mermaid
sequenceDiagram
    participant C as Client
    participant API as Rag.Api<br/>:8080
    participant OAI as OpenAI
    participant QD as Qdrant

    C->>API: POST /query<br/>{ query, collection, topK }
    API->>OAI: EmbedAsync(query)
    OAI-->>API: float[1536]
    API->>QD: SearchAsync(embedding, topK)
    QD-->>API: results
    API-->>C: 200 QueryResponse

    C->>API: POST /index<br/>{ filePath, collection, documentId }
    API->>QD: EnsureCollection + Upsert
    API-->>C: 200 IndexDocumentResponse
```

## Services

| Service | Description | Port |
|---|---|---|
| `rag.api` | Dev/test REST API (Scalar UI at `/scalar/v1`) | 8080 |
| `rag.indexworker` | Consumes `rag.index` queue, embeds and upserts documents | — |
| `rag.queryworker` | Consumes `rag.query` queue, runs semantic search | — |
| `rag.wikipedia-seeder` | One-shot seeder: downloads HuggingFace dataset → MinIO → RabbitMQ | — |
| `rabbitmq` | Message broker (management UI at `:15672`) | 5672 / 15672 |
| `qdrant` | Vector database (gRPC `:6334`, REST `:6333`) | 6333 / 6334 |
| `minio` | S3-compatible object storage (console at `:9001`) | 9000 / 9001 |
| `jaeger` | Distributed tracing UI | 16686 |
| `postgres` | PgVector provider (alternative to Qdrant, not active by default) | 5432 |

## Projects

```
src/
├── Rag.Core/               # Shared library: abstractions, services, providers, resilience
│   ├── Abstractions/       # IBlobStorage, IEmbeddingProvider, IVectorStore, ITextExtractor, IChunker
│   ├── Services/           # IndexDocumentService, VectorSearchService
│   ├── Providers/          # OpenAI, Qdrant, MinIO, PgVector, PDF/text extractors
│   ├── Resilience/         # Polly pipelines (ExternalApi, Qdrant, Storage)
│   ├── Telemetry/          # ActivitySource + Meters (rag.documents.indexed, rag.queries.executed, ...)
│   └── Extensions/         # AddRagCore, AddBlobStorage, AddRagOpenTelemetry
├── Rag.IndexWorker/        # BackgroundService consuming rag.index queue
├── Rag.QueryWorker/        # BackgroundService consuming rag.query queue
├── Rag.Api/                # Minimal API for dev/test
└── Rag.WikipediaSeeder/    # One-shot console app seeding Wikipedia dataset
```

## Resilience

Each external call is wrapped in a dedicated Polly pipeline:

| Pipeline | Targets | Retry | Timeout |
|---|---|---|---|
| `ExternalApi` | OpenAI embeddings | 3× exponential backoff + jitter | 60s |
| `Qdrant` | Vector store operations | 3× exponential backoff + jitter | 30s |
| `Storage` | MinIO get/put | 3× exponential backoff + jitter | 30s |

Failed messages (after all retries) are nacked with `requeue=false` and routed to a Dead Letter Queue via the `rag.dlx` exchange.

## Observability

| Signal | Implementation | Endpoint |
|---|---|---|
| Structured logs | Serilog → compact JSON | stdout |
| Distributed traces | OpenTelemetry → Jaeger (OTLP gRPC) | `http://localhost:16686` |
| Metrics | OpenTelemetry → Prometheus scrape | `http://localhost:8080/metrics` |
| Health — liveness | Always 200 | `GET /health/live` |
| Health — readiness | Checks Qdrant + MinIO | `GET /health/ready` |

Custom metrics:
- `rag.documents.indexed` — counter, tagged by collection
- `rag.index.errors` — counter, tagged by collection
- `rag.queries.executed` — counter, tagged by collection
- `rag.query.errors` — counter, tagged by collection
- `rag.embedding.duration` — histogram (ms)

## Getting Started

**Start infrastructure + workers:**
```bash
docker compose up -d
```

**Seed the Wikipedia dataset** (downloads ~3200 passages from HuggingFace, uploads to MinIO, publishes to `rag.index` queue):
```bash
docker compose --profile seed run --rm rag.wikipedia-seeder
```

**Query via Scalar UI:**
```
http://localhost:8080/scalar/v1
```

**Sample query:**
```bash
curl -X POST http://localhost:8080/query \
  -H "Content-Type: application/json" \
  -d '{"query": "Who invented the telephone?", "collection": "wikipedia", "topK": 3}'
```

## Configuration

Key environment variables (see `.env`):

```env
RAG__EMBEDDINGPROVIDER=OpenAI          # OpenAI | AzureOpenAI
RAG__VECTORSTOREPROVIDER=Qdrant        # Qdrant | PgVector
RAG__STORAGEPROVIDER=Minio             # Minio | Local
RAG__VECTORSIZE=1536
RAG__DEFAULTTOPK=5

OPENAI__APIKEY=sk-...
OPENTELEMETRY__OTLPENDPOINT=http://jaeger:4317
RABBITMQ__PREFETCH=32
RABBITMQ__CONCURRENCY=32
```
