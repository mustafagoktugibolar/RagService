# RagService Technical Knowledge Base

Bu doküman seti, RagService reposunu **gerçek kaynak koduna** dayanarak anlatan bir teknik ders kitabıdır. Hedef okuyucu: .NET/backend tecrübesi olan, distributed systems ve RAG/retrieval engineering'i derinlemesine öğrenmek isteyen mühendis. Anlatım Türkçe, teknik terimler İngilizce bırakılmıştır.

Markdown dosyaları **tek doğruluk kaynağıdır** (source of truth); Claude Artifact bunların okunabilir bir görünümüdür. Uygulama planı: [../PLAN.md](../PLAN.md). Mimari değerlendirme: [../ASSESSMENT.md](../ASSESSMENT.md).

## Olgunluk (Maturity) etiketleri

Her bölümün başında ve her kavramın yanında şu etiketlerden biri bulunur. **Planlanan bir özellik asla uygulanmış gibi anlatılmaz.**

| Etiket | Anlamı |
|---|---|
| ✅ **Implemented** | Repoda var, kodu okundu, testi çalıştırıldı |
| 🟡 **Partial** | Repoda kısmen var; eksik kısım bölümde açıkça belirtilir |
| 🔵 **Planned** | `PLAN.md`'de var, henüz yazılmadı |
| 🧪 **Experimental** | Yazıldı ama ölçüm kanıtı yok; varsayılan kapalı |
| 📖 **Theory** | Sadece öğrenme amaçlı; repoda uygulaması yok ve planı da yok |

## İçindekiler

| # | Bölüm | Durum | Kapsam |
|---|---|---|---|
| 1 | [Sistem Mimarisi](01-system-architecture.md) | ✅ yazıldı | Query/Index akışı, worker'lar, bağımlılık yönü, tasarım kararları |
| 2 | [.NET Mühendisliği](02-dotnet-engineering.md) | ⬜ | DI, BackgroundService, async, CancellationToken, Options, SOLID |
| 3 | [RabbitMQ](03-rabbitmq.md) | ⬜ | Exchange/queue, ack, prefetch, DLQ, idempotency, backpressure |
| 4 | [Embeddings ve Vektör Arama](04-embeddings-vector-search.md) | ⬜ | Cosine, HNSW, Qdrant, PgVector, filtreleme |
| 5 | [Ingestion ve Chunking](05-ingestion-chunking.md) | ⬜ | Extractor'lar, tokenization, overlap, chunk identity |
| 6 | [İleri Seviye Retrieval](06-advanced-retrieval.md) | 🔵 planned | BM25, hybrid, RRF, reranking, query rewriting |
| 7 | [Retrieval Evaluation](07-retrieval-evaluation.md) | ✅ yazıldı (Faz 3) | Recall/Precision/MRR/nDCG/AP, golden dataset, harness, ilk baseline; quality gate 🔵 planned |
| 8 | [Redis ve Caching](08-redis-caching.md) | ✅ yazıldı | Embedding cache, query cache, cache key, stale sorunu |
| 9 | [Storage Sistemleri](09-storage-systems.md) | ⬜ | MinIO, Qdrant, PostgreSQL, persistent vs derived data |
| 10 | [Resilience ve Observability](10-resilience-observability.md) | ⬜ | Resilience katmanı (retry/breaker/timeout), OpenTelemetry, Prometheus, Grafana, Tempo |
| 11 | [Docker ve Deployment](11-docker-deployment.md) | ⬜ | Dockerfile, compose, health check, secrets |
| 12 | [Test ve Kalite](12-testing-quality.md) | ⬜ | Unit/integration, fake vs mock, deterministic retrieval testing |
| 13 | [Güvenlik](13-security.md) | ⬜ | Prompt injection, retrieval authorization, filter validation |
| 14 | [Mimari Kararlar ve Trade-off'lar](14-architectural-decisions.md) | ⬜ | Karşılaştırma tabloları |
| 15 | [Mülakat Hazırlığı](15-interview-prep.md) | ⬜ | Soru-cevap, troubleshooting senaryoları |
| — | [Sözlük (Glossary)](glossary.md) | ⬜ | Terimler |

`⬜` = henüz yazılmadı. Bölümler PLAN.md'deki fazlarla birlikte büyür; her faz ilgili bölümü günceller.

## Her bölümün standart iskeleti

1. Nedir? 2. Hangi problemi çözer? 3. Bu repoda neden var? 4. İçeride nasıl çalışır? 5. Kodda nerede? 6. Diğer bileşenlerle etkileşimi 7. Alternatifler 8. Trade-off'lar 9. Ne ters gidebilir? 10. Nasıl test/gözlem edilir? 11. Mülakatta nasıl anlatırım?

## Okuma ilerlemesi

Artifact üzerinde her bölüm için "okudum" işareti tutulur (tarayıcıda localStorage). Kanonik durum bu tablodur.
